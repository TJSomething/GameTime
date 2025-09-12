namespace GameTime.Controllers

open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine

open GameTime.ViewFns

type HomeController() =
    member this.Index(pathBase: string) =
        Results.Content(
            statusCode = 200,
            contentType = "text/html",
            content = RenderView.AsString.htmlDocument (homeView pathBase)
        )
