namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Tasks

open Microsoft.Extensions.DependencyInjection

open Dapper.FSharp.SQLite

open GameTime.Data
open GameTime.Data.Entities


type GameIdleProcessor
    (
        enqueue: int -> unit,
        getActiveJobCount: unit -> int,
        serviceProvider: IServiceProvider
    ) =
    let currentGameVersion = Some 1
    
    member this.Start(stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()
            
            while (not stoppingToken.IsCancellationRequested) do
                let! nextGame =
                    task {
                        if getActiveJobCount() = 0 then
                            use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()
                            
                            let! unfinishedGames =
                                select {
                                    for g in dbContext.Game do
                                        where (isNullValue g.UpdateFinishedAt)
                                        andWhere (g.FetchedPlays > 0)
                                        orderBy g.Id
                                        take 0 1
                                }
                                |> dbContext.GetConnection().SelectAsync<Game>
                            
                            let! oldGames =
                                select {
                                    for g in dbContext.Game do
                                        where (isNotNullValue g.UpdateFinishedAt)
                                        andWhere (g.UpdateVersion < currentGameVersion || isNullValue g.UpdateVersion)
                                        orderBy g.Id
                                        take 0 1
                                }
                                |> dbContext.GetConnection().SelectAsync<Game>
                            
                            return [unfinishedGames; oldGames] |> Seq.concat |> Seq.tryHead
                        else
                            return None
                    }
                
                match nextGame with
                | Some g -> enqueue g.Id
                | None -> ()
                
                try
                    do! Task.Delay(60_000, stoppingToken)
                with :? OperationCanceledException ->
                    ()
        }