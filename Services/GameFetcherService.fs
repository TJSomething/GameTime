namespace GameTime.Services

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open GameTime.Services.Internal
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type GameFetcherService(serviceProvider: IServiceProvider, logger: ILogger<GameFetcherService>) =
    inherit BackgroundService()

    let fetcher = XmlFetcher(logger)
    let activeGameIds: ConcurrentDictionary<int, unit> = ConcurrentDictionary()
    let playFetchQueue: int Channel = Channel.CreateUnbounded()

    let gameInitializationProcessor =
        GameInitializationProcessor(
            serviceProvider = serviceProvider,
            fetcher = fetcher,
            playFetchChannel = playFetchQueue.Writer,
            activeGameIds = activeGameIds
        )

    let playFetchProcessor =
        PlayFetchProcessor(
            fetcher = fetcher,
            serviceProvider = serviceProvider,
            playJobChannel = playFetchQueue.Reader,
            activeGameIds = activeGameIds
        )

    member this.EnqueueFetch(id: int) =
        gameInitializationProcessor.EnqueueFetch(id)

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
            restartStrategy (fun () -> playFetchProcessor.Start(stoppingToken))
        )
