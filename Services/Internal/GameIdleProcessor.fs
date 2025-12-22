namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open System.Xml.XPath

open Dapper.FSharp.SQLite

open Microsoft.Extensions.DependencyInjection

open GameTime.Data
open GameTime.Data.Entities
open GameTime.XmlUtils

type GameIdleProcessor
    (
        enqueue: int -> unit,
        getActiveJobCount: unit -> int,
        serviceProvider: IServiceProvider,
        fetcher: XmlFetcher
    ) =
    let currentGameVersion = Some 1
    
    let extractGameIds (xmlDoc: XDocument) =
        xmlDoc
        |> Option.ofObj
        |?> _.XPathSelectElements("//items/item[@id]")
        |?> Seq.collect (fun e ->
            e.Attribute(XName.Get("id"))
            |> Option.ofObj
            |?> _.Value
            |> Option.toList)
        |> Option.toList
        |> Seq.concat
        |> Seq.map int
        |> Seq.toList
    
    let filterExistingGameIds (dbContext: DbContext) (ids: int list) =
        task {
            let! existingGames =
                select {
                    for game in dbContext.Game do
                    where (isIn game.Id ids)
                } |> dbContext.GetConnection().SelectAsync<Game>
                
            return Seq.except (existingGames |> Seq.map _.Id) ids
        }

    let getRandomGameSearch (dbContext: DbContext) (stoppingToken: CancellationToken) =
        task {
            let mutable result = Seq.empty
            let mutable tries = 0
            
            while (result |> Seq.isEmpty) && (tries < 10) && (not stoppingToken.IsCancellationRequested) do
                // Pick a random 3 letter prefix of a previously fetched game
                let! gameCountResult =
                    select {
                        for game in dbContext.Game do
                        count "*" "Value"
                    } |> dbContext.GetConnection().SelectAsync<{| Value: int |}>
                let gameCount = gameCountResult |> Seq.head |> _.Value
                
                let randomOffset = Random.Shared.Next(gameCount)
                    
                let! randomGame =
                    select {
                        for game in dbContext.Game do
                        take randomOffset 1
                    } |> dbContext.GetConnection().SelectAsync<Game>
                
                // Take the first three letters of a random word
                let searchString =
                    randomGame
                    |> Seq.tryHead
                    |> Option.bind _.Title
                    |> Option.defaultValue "the"
                    |> _.Split(" ")
                    |> Seq.randomChoice
                    |> _.Substring(0, 3)
                
                // Search BGG for that prefix
                let! resultXml = fetcher.downloadXmlAsync $"https://boardgamegeek.com/xmlapi2/search?query={searchString}&type=boardgame"
                
                let ids = extractGameIds resultXml
                let! missingIds = filterExistingGameIds dbContext ids
                result <- missingIds |> Seq.randomShuffle
                    
                tries <- tries + 1

            return result
        }
    
    let getNewHotGameIds(dbContext: DbContext) =
        task {
            let! hotnessXml = fetcher.downloadXmlAsync "https://boardgamegeek.com/xmlapi2/hot?type=boardgame"
            let hotIds = extractGameIds hotnessXml
            let! missingHotIds = filterExistingGameIds dbContext hotIds
            
            return missingHotIds
        }
        
    member this.Start(stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()
            
            while (not stoppingToken.IsCancellationRequested) do
                let! nextGameId =
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
                            
                            let! hotGameIds = getNewHotGameIds dbContext
                            
                            let! randomGameIds = getRandomGameSearch dbContext stoppingToken
                            
                            return [
                                unfinishedGames |> Seq.map _.Id
                                oldGames |> Seq.map _.Id
                                hotGameIds
                                randomGameIds
                            ] |> Seq.concat |> Seq.tryHead
                        else
                            return None
                    }
                
                match nextGameId with
                | Some id -> enqueue id
                | None -> ()
                
                try
                    do! Task.Delay(60_000, stoppingToken)
                with :? OperationCanceledException ->
                    ()
        }