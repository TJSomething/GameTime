namespace GameTime.Controllers

open System
open System.Globalization

open Microsoft.AspNetCore.Http

open Dapper.FSharp.SQLite

open GameTime.Data
open GameTime.Data.DbCache
open GameTime.Data.Entities
open GameTime.Services
open GameTime.Services.Internal.PlayStats
open GameTime.ViewFns

type private CachedGameStats =
    { ModifiedAt: DateTime
      PercentileTable: string seq seq
      Average: float }

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService) =
    static let STAT_VERSION = 1
    
    let getOrMakeGameStats (db: DbContext) (id: int) (gameModifiedDateTime: DateTime) =
        task {
            let key = $"game-stats-{id}"
            
            let create () =
                task {
                    let job = PlayTimePercentileTableJob(db, id)
                    
                    do! job.InitializeFromDb()
                    
                    while! job.FetchAndProcessPlayPage() do
                        ()
                    
                    return
                        { ModifiedAt = gameModifiedDateTime
                          PercentileTable = job.BuildTable()
                          Average = job.GetAverage() }
                }

            let! cached = getOrCreateFromCache dbContext key STAT_VERSION create

            // Reset cache entry if we have newer data
            if cached.ModifiedAt < gameModifiedDateTime then
                let! newStats = create ()
                do! addToCache dbContext key STAT_VERSION newStats
                return newStats
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
                let playerCounts =
                    split.ByPlayerCountAndMonth
                    |> Seq.map (fun x -> fst x.Key)
                    |> Seq.distinct
                    |> Seq.sort
                
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
