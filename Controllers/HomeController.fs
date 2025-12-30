namespace GameTime.Controllers

open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine

open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open GameTime.ViewFns

// This can't be opened before Data or the app crashes
open Dapper.FSharp.SQLite

type HomeController(dbContext: DbContext, config: AppConfig) =
    member this.Index(pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! gameCountResult =
                select {
                    for g in dbContext.Game do
                        count "*" "Value"
                }
                |> conn.SelectAsync<{| Value: int64 |}>

            let! playCountResult =
                select {
                    for g in dbContext.Play do
                        count "*" "Value"
                }
                |> conn.SelectAsync<{| Value: int64 |}>
                
            let gameCount =
                gameCountResult
                |> Seq.tryHead
                |> Option.map _.Value
                |> Option.defaultValue -1
                
            let playCount =
                playCountResult
                |> Seq.tryHead
                |> Option.map _.Value
                |> Option.defaultValue -1
 
            let! recentGames =
                select {
                    for g in dbContext.Game do
                        where (isNotNullValue g.UpdateFinishedAt)
                        andWhere (isNotNullValue g.Title)
                        orderByDescending g.UpdateFinishedAt
                        take 0 10
                }
                |> conn.SelectAsync<Game>
 
            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = (
                        homeView pathBase gameCount playCount recentGames config.BggFrontendToken
                        |> RenderView.AsString.htmlDocument)
                )
        }
