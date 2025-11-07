namespace GameTime.Controllers

open System

open System.Collections.Generic
open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory

open Dapper.FSharp.SQLite
open FSharp.Stats
open Microsoft.FSharpLu.Json

open GameTime.ViewFns

type private CachedGameStats =
    { ModifiedAt: DateTime
      PercentileTable: string[][]
      Average: float }

    member this.GetCacheSize() =
        // It's not a perfect measure, but JSON serialization is going to be a proportional upper bound on memory usage
        (Default.serialize this).Length


type PlayAmountStatsDraft =
    { UniquePlayerIds: int Set
      MinutesPlayed: int
      PlayCount: int }
    
    member this.ToStats(gameId: int, month: int option, playerCount: int option) =
        { GameId = gameId
          Month = month
          PlayerCount = playerCount
          UniquePlayers = this.UniquePlayerIds.Count
          MinutesPlayed = this.MinutesPlayed
          PlayCount = this.PlayCount }

type GamePlayAmountTables =
    {
        Overall: PlayAmountStats
        ByMonth: Map<int, PlayAmountStats>
        ByPlayerCount: Map<int, PlayAmountStats>
        ByPlayerCountAndMonth: Map<int * int, PlayAmountStats>
    }

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService, cache: IMemoryCache) =

    let makePercentileTable (plays: Play seq) =
        let playerCountToTimes = Dictionary<int, int list>()
        
        for play in plays do
            match playerCountToTimes.TryGetValue play.PlayerCount with
            | true, counts ->
                playerCountToTimes.Add(play.PlayerCount, play.Length :: counts)
            | false, _ ->
                playerCountToTimes.Add(play.PlayerCount, [play.Length])

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
            for countTimesPair in playerCountToTimes do
                let playCount = countTimesPair.Value |> Seq.length
                let sorted = countTimesPair.Value |> Seq.sort |> Seq.map float
                let qs = Quantile.computePercentiles Quantile.OfSorted.compute ps sorted

                yield
                    seq {
                        yield $"%d{countTimesPair.Key}"
                        yield $"%d{playCount}"

                        for q in qs do
                            yield $"%.0f{q}"
                    }
                    |> Seq.toArray
        }
        |> Seq.toArray
        
    
    let calcMonthlyStats (gameId: int) (plays: Play seq) =
        let mutable playerCountToDraft = Map.empty<int, PlayAmountStatsDraft>
        let mutable monthToDraft = Map.empty<int, PlayAmountStatsDraft>
        let mutable playerMonthToDraft = Map.empty<int * int, PlayAmountStatsDraft>
        let mutable overallDraft = None
        
        let updateDraft play draftOpt =
            match draftOpt with
            | Some draft ->
                let newUniquePlayers =
                    match play.UserId with
                    | Some userId -> Set.add userId draft.UniquePlayerIds
                    | None -> draft.UniquePlayerIds
                
                Some { PlayCount = draft.PlayCount + 1
                       UniquePlayerIds = newUniquePlayers
                       MinutesPlayed = draft.MinutesPlayed + play.Length }
            | None ->
                Some { PlayCount = 1
                       UniquePlayerIds =
                          match play.UserId with
                          | Some id ->
                              Set.singleton id
                          | None -> Set.empty
                       MinutesPlayed = play.Length }
        
        for play in plays do
            let month =
                play.PlayedGregorianDay
                |> Option.map (fun day ->
                    let date = DateOnly.FromDayNumber(day)
                    date.Year * 12 + date.Month - 1)
                |> Option.defaultValue -1
                
            playerCountToDraft <-
                playerCountToDraft
                |> Map.change play.PlayerCount (updateDraft play)
                
            monthToDraft <-
                monthToDraft
                |> Map.change month (updateDraft play)
                
            playerMonthToDraft <-
                playerMonthToDraft
                |> Map.change (play.PlayerCount, month) (updateDraft play)
            
            overallDraft <- updateDraft play overallDraft
                    
        { Overall =
            match overallDraft with
            | Some draft -> draft.ToStats(
                gameId = gameId,
                month = None,
                playerCount = None)
            | None ->
                { GameId = gameId
                  Month = None
                  PlayerCount = None
                  UniquePlayers = 0
                  MinutesPlayed = 0
                  PlayCount = 0 }
          ByMonth =
             monthToDraft
             |> Map.map (fun month draft -> draft.ToStats(gameId, Some month, None))
          ByPlayerCount =
             playerCountToDraft
             |> Map.map (fun playerCount draft -> draft.ToStats(gameId, None, Some playerCount))
          ByPlayerCountAndMonth =
             playerMonthToDraft
             |> Map.map (fun (playerCount, month) draft -> draft.ToStats(gameId, Some month, Some playerCount)) }

    let getOrMakeGameStats (db: DbContext) (id: int) (gameModifiedDateTime: DateTime) =
        task {
            let key = $"game-stats-{id}"

            let create () =
                task {
                    let! plays =
                        select {
                            for p in db.Play do
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
                let opts = MemoryCacheEntryOptions()
                opts.Size <- newStats.GetCacheSize()
                return cache.Set(key, newStats, opts)
            else
                return cached
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
                    otherGamesAheadOfThisOne = gameOrder
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
