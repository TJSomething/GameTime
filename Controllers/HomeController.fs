namespace GameTime.Controllers

open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine
// This can't be opened before Giraffe for some reason
open Dapper.FSharp.SQLite

open GameTime.ViewFns
open GameTime.DataAccess

type HomeController(dbContext: DbContext) =
    member this.Index(pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! recentGames =
                select {
                    for g in gameTable do
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
                    content = RenderView.AsString.htmlDocument (homeView pathBase recentGames)
                )
        }
