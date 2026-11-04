namespace GameTime.Services

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open GameTime.Data
open GameTime.Data.DbCache
open GameTime.Services.Internal
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type HomeDashboardService(serviceProvider: IServiceProvider, logger: ILogger<HomeDashboardService>) =
    inherit BackgroundService()
    
    let jobTrigger: unit Channel = Channel.CreateUnbounded()
    
    static member REPORT_GAME_COUNT_KEY = "dashboard-report:game-count"
    static member REPORT_RECENT_GAMES_KEY = "dashboard-report:recent-games"
    static member REPORT_RANDOM_GAMES_KEY = "dashboard-report:random-games"
    static member CACHE_VERSION = 1
    static member REFRESH_DURATION_SECONDS = 60
    
    /// <summary>
    /// triggers an update of the dashboard
    /// </summary>
    member this.TriggerUpdate() =
        // This is a fire and forget trigger
        jobTrigger.Writer.TryWrite(()) |> ignore

    override this.ExecuteAsync(stoppingToken) =
        let restartStrategy (t: unit -> Task<unit>) =
            task {
                while (not stoppingToken.IsCancellationRequested) do
                    try
                        do! t ()
                    with ex ->
                        if (not stoppingToken.IsCancellationRequested) then
                            logger.LogError(ex, "Error in DashboardUpdateService, restarting in 1 second")
                            do! Task.Delay(1000)
                        else
                            logger.LogError(ex, "Error in DashboardUpdateService during shutdown")
            }

        Task.WhenAll(
            restartStrategy (fun () -> this.TickReports(stoppingToken)),
            restartStrategy (fun () -> this.RefreshReports(stoppingToken))
        )
        
    member private this.TickReports (stoppingToken: CancellationToken) =
        task {
            let delay = TimeSpan.FromSeconds(HomeDashboardService.REFRESH_DURATION_SECONDS |> int64)
               
            while (not stoppingToken.IsCancellationRequested) do
                try
                    do! Task.Delay(delay, stoppingToken)
                    this.TriggerUpdate()
                    
                    ()
                with
                | :? OperationCanceledException -> ()
        }
    
    member private this.RefreshReports (stoppingToken: CancellationToken) =
        task {
            logger.LogInformation(GameListReport.IsSerializable)
            logger.LogInformation(GameCountReport.IsSerializable)
            while (not stoppingToken.IsCancellationRequested) do
                try
                    do! jobTrigger.Reader.ReadAsync(stoppingToken)
                    
                    use scope = serviceProvider.CreateAsyncScope()
                    use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()
                    let queries = HomeDashboardQuery(dbContext)
                    
                    let! gameCount = queries.gameCount()
                    let! recentGames = queries.recentGames()
                    let! randomGames = queries.randomGames()
                    
                    do!
                        addToCache
                            dbContext
                            HomeDashboardService.REPORT_GAME_COUNT_KEY
                            HomeDashboardService.CACHE_VERSION
                            gameCount
                    
                    do!
                        addToCache
                            dbContext
                            HomeDashboardService.REPORT_RECENT_GAMES_KEY
                            HomeDashboardService.CACHE_VERSION
                            recentGames
                    
                    do!
                        addToCache
                            dbContext
                            HomeDashboardService.REPORT_RANDOM_GAMES_KEY
                            HomeDashboardService.CACHE_VERSION
                            randomGames
                            
                    logger.LogDebug("Home refreshed")
                    
                    ()
                with
                | :? OperationCanceledException -> ()
        }
