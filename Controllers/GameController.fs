namespace GameTime.Controllers

open System

open GameTime.Services
open Microsoft.AspNetCore.Http

open Dapper.FSharp.SQLite
open FSharp.Stats

open GameTime.DataAccess
open GameTime.ViewFns

type GameController(dbContext: DbContext, gameFetcher: GameFetcherService) =
    let showPercentiles (plays: Play seq) =
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

        let textChunks =
            seq {
                yield " Players |  Plays"

                for p in ps do
                    yield (sprintf " | %4d%%" (int (p * 100.0)))

                yield "\n"

                for (playerCount, playTimes) in (playerCountToTimes |> Map.toSeq) do
                    let playCount = playTimes |> Seq.length
                    let sorted = playTimes |> Seq.sort |> Seq.map float
                    let qs = Quantile.computePercentiles (Quantile.OfSorted.compute) ps sorted

                    yield $" %7d{playerCount} | %6d{playCount}"

                    for q in qs do
                        yield $" | %5.0f{q}"

                    yield "\n"
            }

        String.Join("", textChunks)

    member this.Listing(id: int) =
        task {
            use conn = dbContext.GetConnection()

            let! gameResult =
                select {
                    for g in gameTable do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            let game = Seq.tryHead gameResult

            let! plays =
                task {
                    match game with
                    | None ->
                        // Start in the background
                        gameFetcher.EnqueueFetch(id) |> ignore
                        return List.empty
                    | Some g when g.UpdateFinishedAt.IsNone ->
                        gameFetcher.EnqueueFetch(id) |> ignore
                        return List.empty
                    | Some _ ->
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

            let (status, title, fetchedCount, totalPlays, eta) =
                match game with
                | Some g ->
                    match (g.Title, g.UpdateStartedAt, g.UpdateFinishedAt) with
                    | (Some t, Some st, None) ->
                        let eta =
                            match g.FetchedPlays with
                            | 0 -> None
                            | validatedPlays ->
                                let timeSpent = DateTime.Now - st
                                let timePerItem = timeSpent / (float validatedPlays)
                                let itemsLeft = g.TotalPlays - g.FetchedPlays
                                let timeLeft = timePerItem * (float itemsLeft)
                                Some(DateTime.Now + timeLeft)
                        ("Loading", t, g.FetchedPlays, g.TotalPlays, eta)
                    | (Some t, _, Some _) -> ("Loaded", t, g.FetchedPlays, g.TotalPlays, None)
                    | (Some t, None, _) -> ("Loading", t, 0, 0, None)
                    | (None, _, _) -> ("Initial", $"Game #{id}", 0, 0, None)

                | None -> ("Initial", $"Game #{id}", 0, 0, None)


            let view =
                Listing.Render(
                    id = id,
                    status = status,
                    title = title,
                    playCount = fetchedCount,
                    totalPlays = totalPlays,
                    averagePlayTime = average,
                    eta = eta,
                    percentileTable = showPercentiles plays
                )

            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = Giraffe.ViewEngine.RenderView.AsString.htmlDocument view
                )
        }
