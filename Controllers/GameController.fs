namespace GameTime.Controllers

open System

open System.Collections.Generic
open System.Globalization
open System.Linq
open Dapper
open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open GameTime.Services.Internal.PlayStats
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory

open Dapper.FSharp.SQLite
open FSharp.Stats
open Microsoft.FSharpLu.Json

open GameTime.ViewFns

type private CachedGameStats =
    { ModifiedAt: DateTime
      PercentileTable: string seq seq
      Average: float }

    member this.GetCacheSize() =
        // It's not a perfect measure, but JSON serialization is going to be a proportional upper bound on memory usage
        (Default.serialize this).Length

type private PlayForTimeStats =
    { PlayerCount: int
      Length: int }
    
type GameController(dbContext: DbContext, gameFetcher: GameFetcherService, cache: IMemoryCache) =

    let makePercentileTable (plays: PlayForTimeStats seq) =
        // Let's only allocate the times array once
        let playerCountToCount = Dictionary<int, int>()
        
        for play in plays do
            playerCountToCount[play.PlayerCount] <-
                playerCountToCount.GetValueOrDefault(play.PlayerCount, 0) + 1
        
        let playerCountToTimes =
            Array.init
                (playerCountToCount.Keys.Max() + 1)
                (fun i ->
                    let l = playerCountToCount.GetValueOrDefault(i, 0)
                    Array.create l 0.0)
                                     
        for play in plays do
            let count = play.PlayerCount
            let index = playerCountToCount[count] - 1
            playerCountToTimes[count][index] <- play.Length |> float
            playerCountToCount[count] <- index

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

            // Row for each player count
            for playerCount, times in playerCountToTimes.Index() do
                Array.Sort(times)
                let playCount = times.Length
                if playCount > 0 then
                    let qs = Seq.map (fun p -> Quantile.OfSorted.compute p times) ps

                    yield
                        seq {
                            yield $"%d{playerCount}"
                            yield $"%d{playCount}"

                            for q in qs do
                                yield $"%.0f{q}"
                        }
        }

    let getOrMakeGameStats (db: DbContext) (id: int) (gameModifiedDateTime: DateTime) =
        task {
            let key = $"game-stats-{id}"

            let create () =
                task {
                    // Implement reading manually because Dapper doesn't
                    // like mismatched tables and records in static linking
                    // mode.
                    let! reader =
                        db.GetConnection().ExecuteReaderAsync(
                            """
                                select PlayerCount, Length
                                from Play
                                where GameId = @id
                            """,
                            {| id = id |})
                    
                    let plays = List<PlayForTimeStats>()
                    
                    try
                        while reader.Read() do
                            plays.Add { PlayerCount = reader.GetInt32(0)
                                        Length = reader.GetInt32(1) }
                    finally
                        reader.Close()

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
                let opts = MemoryCacheEntryOptions()
                opts.Size <- newStats.GetCacheSize()
                return cache.Set(key, newStats, opts)
            else
                return cached
        }
    
    let getMonthlyStats (db: DbContext) (id: int) (updatedAt: DateTime)  =
        task {
            let lastMonth = updatedAt |> DateOnly.FromDateTime |> dateToMonth
            let firstMonth = Some (lastMonth - 12)
            
            let! stats = 
                select {
                    for s in db.PlayAmountStats do
                        where (s.GameId = id)
                        andWhere (isNotNullValue s.Month)
                        andWhere (s.Month > firstMonth)
                }
                |> db.GetConnection().SelectAsync<PlayAmountStats>
            
            if stats |> Seq.length > 0 then
                let split = splitStats stats
                let playerCounts = split.ByPlayerCount.Keys |> Seq.sort
                
                let byPlay =
                    seq {
                        // Corner cell
                        yield seq {
                            yield "Players"
                            
                            for month in lastMonth - 11 .. lastMonth do
                                yield (monthToFirstDate month).ToString("MMM \"'\"yy", CultureInfo.InvariantCulture)
                        }
                        
                        for playerCount in playerCounts do
                            yield seq {
                                yield $"{playerCount}"
                                
                                for month in lastMonth - 11 .. lastMonth do
                                    let plays =
                                        match split.ByPlayerCountAndMonth.TryGetValue((playerCount, month)) with
                                        | true, stat ->
                                            stat.PlayCount
                                        | false, _ -> 0
                                    
                                    yield $"{plays}"
                            }
                    }
                
                return byPlay
            else
                return "No data available" |> Seq.singleton |> Seq.singleton
        }

    member this.Listing(id: int, pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! gameResult =
                select {
                    for g in dbContext.Game do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            let game = Seq.tryHead gameResult
            let gameOrder = gameFetcher.GetJobOrder(id)

            let! percentileTable, monthlyPlayTable, average =
                task {
                    match (game, gameOrder) with
                    | None, _ ->
                        // Start if no data
                        gameFetcher.EnqueueFetch(id)
                        return (Seq.empty, Seq.empty, 0.0)
                    | Some g, None when g.UpdateFinishedAt.IsNone ->
                        // If there is incomplete data, but no job, then assume that the job failed
                        gameFetcher.EnqueueFetch(id)
                        return (Seq.empty, Seq.empty, 0.0)
                    | _, Some _ ->
                        // Don't start but don't report any plays if a job is in progress
                        return (Seq.empty, Seq.empty, 0.0)
                    | Some g, None ->
                        let! result = getOrMakeGameStats dbContext id g.UpdateTouchedAt
                        let! byMonth = getMonthlyStats dbContext g.Id g.UpdateTouchedAt
                        return (result.PercentileTable, byMonth, result.Average)
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
            
            let year = game |> Option.bind _.YearPublished
            let minPlayers = game |> Option.bind _.BoxMinPlayers
            let maxPlayers = game |> Option.bind _.BoxMaxPlayers

            let view =
                Listing.Render(
                    id = id,
                    pathBase = pathBase,
                    status = status,
                    title = title,
                    year = year,
                    minPlayers = minPlayers,
                    maxPlayers = maxPlayers,
                    playCount = fetchedCount,
                    totalPlays = totalPlays,
                    averagePlayTime = average,
                    timeLeft = timeLeft,
                    percentileTable = percentileTable,
                    monthlyPlayTable = monthlyPlayTable,
                    otherGamesAheadOfThisOne = gameOrder
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
