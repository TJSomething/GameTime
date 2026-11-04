namespace GameTime.Controllers

open GameTime.Data.DbCache
open GameTime.Services.Internal
open GameTime.Views
open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine

open GameTime.Data
open GameTime.Services

type HomeController(dbContext: DbContext, config: AppConfig) =
    member this.Index() =
        task {
            let! gameCount =
                getOrCreateFromCache
                    dbContext
                    HomeDashboardService.REPORT_GAME_COUNT_KEY
                    HomeDashboardService.CACHE_VERSION
                    (fun () -> HomeDashboardQuery(dbContext).gameCount())
            
            let! recentGames =
                getOrCreateFromCache
                    dbContext
                    HomeDashboardService.REPORT_RECENT_GAMES_KEY
                    HomeDashboardService.CACHE_VERSION
                    (fun () -> HomeDashboardQuery(dbContext).recentGames())
            
            let! randomGames =
                getOrCreateFromCache
                    dbContext
                    HomeDashboardService.REPORT_RANDOM_GAMES_KEY
                    HomeDashboardService.CACHE_VERSION
                    (fun () -> HomeDashboardQuery(dbContext).randomGames())
            
            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = (
                        HomeView.Render(
                            pathBase = config.PathBase,
                            gameCount = gameCount.Count,
                            recentGames = recentGames.Games,
                            randomGames = randomGames.Games,
                            bggToken = config.BggFrontendToken
                        )
                        |> RenderView.AsString.htmlDocument)
                )
        }
