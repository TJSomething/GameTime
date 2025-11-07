namespace GameTime.Controllers

open GameTime.Data.Entities
open GameTime.Services
open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine

open GameTime.Data
open GameTime.ViewFns

// This can't be opened before Data or the app crashes
open Dapper.FSharp.SQLite

type HomeController(dbContext: DbContext, config: AppConfig) =
    member this.Index(pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

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
                    content = RenderView.AsString.htmlDocument (homeView pathBase recentGames config.BggFrontendToken)
                )
        }
