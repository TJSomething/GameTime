namespace GameTime.Controllers

open System

open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory

open Dapper.FSharp.SQLite
open FSharp.Stats

open GameTime.ViewFns

type private CachedGameStats =
    { ModifiedAt: DateTime
      PercentileTable: string[][]
      Average: float }

    member this.GetCacheSize() =
        let tableSize =
            // Array has 3 pointers of overhead
            3 * IntPtr.Size
            + (this.PercentileTable
               |> Array.fold
                   (fun acc row ->
                       acc
                       + 3 * IntPtr.Size
                       + (row
                          |> Array.fold
                              // Strings are UTF-16 with 2 bytes of overhead
                              (fun innerAcc (cell: string) -> innerAcc + IntPtr.Size * 3 + 2 + cell.Length * 2)
                              0))
                   0)

        // This object has 3 pointers of overhead
        // DateTime is a struct of two longs
        // Average is a double precision float
        3 * IntPtr.Size + 16 + 8 + tableSize

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService, cache: IMemoryCache) =

    let makePercentileTable (plays: Play seq) =
        let playerCountToTimes =
            plays
            |> Seq.fold
                (fun acc play ->
                    Map.change
                        play.PlayerCount
                        (fun oldTimesOpt ->
                            match oldTimesOpt with
                            | Some ts -> Some(play.Length :: ts)
                            | None -> Some([ play.Length ]))
                        acc)
                Map.empty

        let ps = [ 0.1..0.1..0.9 ]

        seq {
            // Header row
            yield
                seq {
                    yield "Players"
                    yield "Plays"

                    for p in ps do
                        yield (sprintf "%d%%" (int (p * 100.0)))
                }
                |> Seq.toArray

            // Row for each player count
            for playerCount, playTimes in (playerCountToTimes |> Map.toSeq) do
                let playCount = playTimes |> Seq.length
                let sorted = playTimes |> Seq.sort |> Seq.map float
                let qs = Quantile.computePercentiles Quantile.OfSorted.compute ps sorted

                yield
                    seq {
                        yield $"%d{playerCount}"
                        yield $"%d{playCount}"

                        for q in qs do
                            yield $"%.0f{q}"
                    }
                    |> Seq.toArray
        }
        |> Seq.toArray

    let getOrMakeGameStats (db: DbContext) (id: int) (gameModifiedDateTime: DateTime) =
        task {
            let key = $"game-stats-{id}"

            let create () =
                task {
                    let! plays =
                        select {
                            for p in db.PlayTable do
                                where (p.GameId = id)
                        }
                        |> db.GetConnection().SelectAsync<Play>

                    let average =
                        if Seq.length plays > 0 then
                            plays |> Seq.averageBy (fun p -> p.Length |> float)
                        else
                            0.0

                    return
                        { ModifiedAt = gameModifiedDateTime
                          PercentileTable = makePercentileTable plays
                          Average = average }
                }

            let! cached =
                cache.GetOrCreateAsync(
                    key,
                    (fun cacheItem ->
                        task {
                            let! result = create ()
                            cacheItem.Size <- result.GetCacheSize()
                            cacheItem.SlidingExpiration <- TimeSpan.FromDays(7)
                            return result
                        })
                )

            // Reset cache entry if we have newer data
            if cached.ModifiedAt < gameModifiedDateTime then
                let! newStats = create ()
                return cache.Set(key, newStats)
            else
                return cached
        }

    member this.Listing(id: int, pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! gameResult =
                select {
                    for g in dbContext.GameTable do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            let game = Seq.tryHead gameResult
            let gameOrder = gameFetcher.GetJobOrder(id)

            let! percentileTable, average =
                task {
                    match (game, gameOrder) with
                    | None, _ ->
                        // Start if no data
                        gameFetcher.EnqueueFetch(id)
                        return (Array.empty, 0.0)
                    | Some g, None when g.UpdateFinishedAt.IsNone ->
                        // If there is incomplete data, but no job, then assume that the job failed
                        gameFetcher.EnqueueFetch(id)
                        return (Array.empty, 0.0)
                    | _, Some _ ->
                        // Don't start but don't report any plays if a job is in progress
                        return (Array.empty, 0.0)
                    | Some g, None ->
                        let! result = getOrMakeGameStats dbContext id g.UpdateTouchedAt
                        return (result.PercentileTable, result.Average)
                }

            let status, title, fetchedCount, totalPlays, timeLeft =
                match (game, gameOrder) with
                | Some g, Some _ ->
                    match (g.Title, g.UpdateStartedAt, g.UpdateFinishedAt) with
                    | Some t, Some st, None ->
                        let eta =
                            match g.FetchedPlays with
                            | 0 -> None
                            | validatedPlays ->
                                let timeSpent = DateTime.Now - st
                                let timePerItem = timeSpent / (float validatedPlays)
                                let itemsLeft = g.TotalPlays - g.FetchedPlays
                                Some(timePerItem * (float itemsLeft))

                        ("Loading", t, g.FetchedPlays, g.TotalPlays, eta)
                    | Some t, _, Some _ -> ("Loaded", t, g.FetchedPlays, g.TotalPlays, None)
                    | Some t, None, _ -> ("Loading", t, 0, 0, None)
                    | None, _, _ -> ("Initial", $"Game #{id}", 0, 0, None)

                | Some g, None ->
                    match (g.Title, g.UpdateFinishedAt) with
                    | Some t, Some _ -> ("Loaded", t, g.FetchedPlays, g.TotalPlays, None)
                    | None, Some _ -> ("Loaded", "Game not found", g.FetchedPlays, g.TotalPlays, None)
                    | _, None -> ("Initial", $"Game #{id}", 0, 0, None)
                | _, None
                | None, _ -> ("Initial", $"Game #{id}", 0, 0, None)

            let view =
                Listing.Render(
                    id = id,
                    pathBase = pathBase,
                    status = status,
                    title = title,
                    playCount = fetchedCount,
                    totalPlays = totalPlays,
                    averagePlayTime = average,
                    timeLeft = timeLeft,
                    percentileTable = percentileTable,
                    otherGamesAheadOfThisOne = gameOrder
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
