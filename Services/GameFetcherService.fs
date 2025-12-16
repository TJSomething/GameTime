namespace GameTime.Services

open System
open System.Threading.Channels
open System.Threading.Tasks
open GameTime.Services.Internal
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type GameFetcherService(serviceProvider: IServiceProvider, logger: ILogger<GameFetcherService>, config: AppConfig) =
    inherit BackgroundService()

    let fetcher = XmlFetcher(logger, config)
    let jobTracker = ActiveJobTracker()
    let playFetchQueue: int Channel = Channel.CreateUnbounded()

    let gameInitializationProcessor =
        GameInitializationProcessor(
            serviceProvider = serviceProvider,
            fetcher = fetcher,
            playFetchChannel = playFetchQueue.Writer,
            jobTracker = jobTracker
        )

    let playFetchProcessor =
        PlayFetchProcessor(
            fetcher = fetcher,
            serviceProvider = serviceProvider,
            playJobChannel = playFetchQueue.Reader,
            jobTracker = jobTracker
        )
        
    let gameIdleProcessor =
        GameIdleProcessor(
            serviceProvider = serviceProvider,
            enqueue = gameInitializationProcessor.EnqueueFetch,
            getActiveJobCount = jobTracker.GetJobCount
        )

    /// <summary>
    /// queues a game to be fetched.
    /// </summary>
    /// <param name="id">the BGG game ID</param>
    member this.EnqueueFetch(id: int) =
        gameInitializationProcessor.EnqueueFetch(id)


    /// <summary>
    /// checks the number of games processing ahead of the game given in the queue
    /// </summary>
    /// <param name="id">the BGG game ID</param>
    member this.GetJobOrder(id: int) = jobTracker.GetJobOrder(id)

    override this.ExecuteAsync(stoppingToken) =
        let restartStrategy (t: unit -> Task<unit>) =
            task {
                while (not stoppingToken.IsCancellationRequested) do
                    try
                        do! t ()
                    with ex ->
                        if (not stoppingToken.IsCancellationRequested) then
                            logger.LogError(ex, "Error in GameFetcherService, restarting in 1 second")
                            do! Task.Delay(1000)
                        else
                            logger.LogError(ex, "Error in GameFetcherService during shutdown")
            }

        Task.WhenAll(
            restartStrategy (fun () -> gameInitializationProcessor.Start(stoppingToken)),
            restartStrategy (fun () -> playFetchProcessor.Start(stoppingToken)),
            restartStrategy (fun () -> gameIdleProcessor.Start(stoppingToken))
        )
