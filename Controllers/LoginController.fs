namespace GameTime.Controllers

open System.Security.Claims
open GameTime.Services
open GameTime.Views
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http

type LoginController(config: AppConfig, forgeryService: IAntiforgery) =
    member this.Form(context: HttpContext) =
        let tokens = forgeryService.GetAndStoreTokens(context)
        
        if context.User.Identity.IsAuthenticated then
            Results.Content(
                statusCode = 200,
                contentType = "text/html",
                content = (
                    LoginView.RenderAccount(
                        pathBase = config.PathBase,
                        email = context.User.FindFirstValue(ClaimTypes.Email),
                        antiforgeryToken = tokens.RequestToken
                    )
                    |> RenderView.AsString.htmlDocument)
            )
        else
            let isLoggedOut = context.Request.Query.ContainsKey("logout")
            Results.Content(
                statusCode = 200,
                contentType = "text/html",
                content = (
                    LoginView.RenderLoginForm(
                        pathBase = config.PathBase,
                        message = (if isLoggedOut then "Logged out successfully." else ""),
                        antiforgeryToken = tokens.RequestToken
                    )
                    |> RenderView.AsString.htmlDocument)
            )
