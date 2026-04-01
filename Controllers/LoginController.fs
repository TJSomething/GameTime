namespace GameTime.Controllers

open System.Security.Claims
open GameTime.Services
open GameTime.ViewFns
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http

type LoginController(config: AppConfig) =
    member this.Form(context: HttpContext) =
        if context.User.Identity.IsAuthenticated then
            Results.Content(
                statusCode = 200,
                contentType = "text/html",
                content = (
                    Login.RenderAccount(
                        pathBase = config.PathBase,
                        email = context.User.FindFirstValue(ClaimTypes.Email)
                    )
                    |> RenderView.AsString.htmlDocument)
            )
        else
            let isLoggedOut = context.Request.Query.ContainsKey("logout")
            Results.Content(
                statusCode = 200,
                contentType = "text/html",
                content = (
                    Login.RenderLoginForm(
                        pathBase = config.PathBase,
                        message = if isLoggedOut then "Logged out successfully." else ""
                    )
                    |> RenderView.AsString.htmlDocument)
            )
