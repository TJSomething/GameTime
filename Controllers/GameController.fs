namespace GameTime.Controllers

open System

open GameTime.Services
open Microsoft.AspNetCore.Http

open Dapper.FSharp.SQLite
open FSharp.Stats

open GameTime.DataAccess
open GameTime.ViewFns

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService) =
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
                |> Seq.toList

            // Row for each player count
            for (playerCount, playTimes) in (playerCountToTimes |> Map.toSeq) do
                let playCount = playTimes |> Seq.length
                let sorted = playTimes |> Seq.sort |> Seq.map float
                let qs = Quantile.computePercentiles (Quantile.OfSorted.compute) ps sorted

                yield
                    seq {
                        yield $"%d{playerCount}"
                        yield $"%6d{playCount}"

                        for q in qs do
                            yield $"%.0f{q}"
                    }
                    |> Seq.toList
        }
        |> Seq.toList

    member this.Listing(id: int, pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! gameResult =
                select {
                    for g in gameTable do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            let game = Seq.tryHead gameResult
            let gameOrder = gameFetcher.GetJobOrder(id)

            let! plays =
                task {
                    match (game, gameOrder) with
                    | (None, _) ->
                        // Start if no data
                        gameFetcher.EnqueueFetch(id) |> ignore
                        return List.empty
                    | (Some g, None) when g.UpdateFinishedAt.IsNone ->
                        // If there is incomplete data, but no job, then assume that the job failed
                        gameFetcher.EnqueueFetch(id) |> ignore
                        return List.empty
                    | (_, Some _) ->
                        // Don't start but don't report any plays if a job is in progress
                        return List.empty
                    | (Some _, None) ->
                        let! ps =
                            select {
                                for p in playTable do
                                    where (p.GameId = id)
                            }
                            |> conn.SelectAsync<Play>

                        return Seq.toList ps
                }

            let average =
                if List.length plays > 0 then
                    plays |> List.averageBy (fun p -> p.Length |> float)
                else
                    0.0

            let (status, title, fetchedCount, totalPlays, timeLeft) =
                match (game, gameOrder) with
                | (Some g, Some _) ->
                    match (g.Title, g.UpdateStartedAt, g.UpdateFinishedAt) with
                    | (Some t, Some st, None) ->
                        let eta =
                            match g.FetchedPlays with
                            | 0 -> None
                            | validatedPlays ->
                                let timeSpent = DateTime.Now - st
                                let timePerItem = timeSpent / (float validatedPlays)
                                let itemsLeft = g.TotalPlays - g.FetchedPlays
                                Some(timePerItem * (float itemsLeft))

                        ("Loading", t, g.FetchedPlays, g.TotalPlays, eta)
                    | (Some t, _, Some _) -> ("Loaded", t, g.FetchedPlays, g.TotalPlays, None)
                    | (Some t, None, _) -> ("Loading", t, 0, 0, None)
                    | (None, _, _) -> ("Initial", $"Game #{id}", 0, 0, None)

                | (Some g, None) ->
                    match (g.Title, g.UpdateFinishedAt) with
                    | (Some t, Some _) -> ("Loaded", t, g.FetchedPlays, g.TotalPlays, None)
                    | (None, _)
                    | (_, None) -> ("Initial", $"Game #{id}", 0, 0, None)
                | (_, None)
                | (None, _) -> ("Initial", $"Game #{id}", 0, 0, None)

            let view =
                Listing.Render(
                    pathBase = pathBase,
                    status = status,
                    title = title,
                    playCount = fetchedCount,
                    totalPlays = totalPlays,
                    averagePlayTime = average,
                    timeLeft = timeLeft,
                    percentileTable = makePercentileTable plays,
                    otherGamesAheadOfThisOne = gameOrder
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
