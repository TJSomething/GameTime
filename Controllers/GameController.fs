namespace GameTime.Controllers

open System
open System.Globalization

open System.Threading.Tasks
open GameTime.Views
open Microsoft.AspNetCore.Http

open Dapper.FSharp.SQLite

open GameTime.Data
open GameTime.Data.DbCache
open GameTime.Data.Entities
open GameTime.Services
open GameTime.Services.Internal.PlayStats

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService, config: AppConfig) =
    let getOrMakeGameStats (db: DbContext) (id: int) (gameModifiedDateTime: DateTime) =
        task {
            let key = PlayTimePercentileTableJob.GetCacheKey(id)
            let version = PlayTimePercentileTableJob.STAT_VERSION
            let create () = PlayTimePercentileTableJob.Run(db, id, gameModifiedDateTime)
            let! cached = getOrCreateFromCache dbContext key version create

            // Reset cache entry if we have newer data
            if cached.ModifiedAt < gameModifiedDateTime then
                let! newStats = create ()
                do! addToCache dbContext key version newStats
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

    member this.Listing(id: int) =
        task {
            use conn = dbContext.GetConnection()

            let! gameResult =
                select {
                    for g in dbContext.Game do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            let! playerCountRatingResults =
                select {
                    for g in dbContext.PlayerCountVote do
                        where (g.GameId = id)
                }
                |> conn.SelectAsync<PlayerCountVote>

            let game = Seq.tryHead gameResult
            let gameOrder = gameFetcher.GetJobOrder(id)

            let! statResultTask =
                Task.WhenAny(
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
                        },
                        task {
                            do! Task.Delay 20_000
                            let loading = "Loading..." |> Seq.singleton |> Seq.singleton
                            return (loading, loading, 0.0)
                        }
                    )
                
            let! percentileTable, monthlyPlayTable, average = statResultTask

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
                    | Some t, _, Some _ -> ("Loaded", t, g.TotalPlays, g.TotalPlays, None)
                    | Some t, None, _ -> ("Loading", t, 0, 0, None)
                    | None, _, _ -> ("Initial", $"Game #{id}", 0, 0, None)

                | Some g, None ->
                    match (g.Title, g.UpdateFinishedAt) with
                    | Some t, Some _ -> ("Loaded", t, g.TotalPlays, g.TotalPlays, None)
                    | None, Some _ -> ("Loaded", "Game not found", g.FetchedPlays, g.TotalPlays, None)
                    | _, None -> ("Initial", $"Game #{id}", 0, 0, None)
                | _, None
                | None, _ -> ("Initial", $"Game #{id}", 0, 0, None)
            
            let year = game |> Option.bind _.YearPublished
            let minPlayers = game |> Option.bind _.BoxMinPlayers
            let maxPlayers = game |> Option.bind _.BoxMaxPlayers
            
            let playerCountRatingTable =
                playerCountRatingResults
                // Workaround the fact that I'm not upserting rating votes by filtering the row with the most votes
                |> Seq.groupBy _.PlayerCount
                |> Seq.map (fun (_, rs) -> rs |> Seq.maxBy (fun r -> r.Best + r.Recommended + r.NotRecommended))
                // Null player count means more than any player cout listed
                |> Seq.sortBy (fun p ->
                    match p.PlayerCount with
                    | Some n -> (0, n)
                    | None -> (1, 0))
                |> Seq.map (fun p ->
                    let total = p.Best + p.NotRecommended + p.Recommended
                    
                    let playerCountText =
                        p.PlayerCount
                        |> Option.map _.ToString()
                        |> Option.defaultValue "More"
                        
                    let ratingsCells =
                        [p.Best; p.Recommended; p.NotRecommended]
                        |> List.map (fun n ->
                                    let percent = (float n) / (float total) * 100.0
                                    $"%.1f{percent}%% (%i{n})")
                        
                    [
                        [playerCountText]
                        ratingsCells
                        [$"{total}"]
                    ]
                    |> Seq.concat)
                |> Seq.insertAt 0 [""; "Best"; "Recommended"; "Not recommended"; "Vote count"]
            
            let updatedAt =
                game
                |> Option.map _.UpdateTouchedAt.ToUniversalTime().ToString("o")
                |> Option.defaultValue "Never"

            let view =
                ListingView.Render(
                    id = id,
                    pathBase = config.PathBase,
                    status = status,
                    title = title,
                    year = year,
                    updatedAt = updatedAt,
                    minPlayers = minPlayers,
                    maxPlayers = maxPlayers,
                    playCount = fetchedCount,
                    totalPlays = totalPlays,
                    averagePlayTime = average,
                    timeLeft = timeLeft,
                    percentileTable = percentileTable,
                    monthlyPlayTable = monthlyPlayTable,
                    otherGamesAheadOfThisOne = gameOrder,
                    playerCountRatingTable = playerCountRatingTable
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
