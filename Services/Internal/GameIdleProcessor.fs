namespace GameTime.Services.Internal

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open System.Xml.XPath

open Dapper
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
        
    let getRandomPlaysPage urlPrefix =
        (task {
            let! firstPage = fetcher.downloadXmlAsync $"%s{urlPrefix}"
            
            let totalPlays = firstPage |> attrInt "/plays" "total"
            let pageSize = firstPage.XPathSelectElements("//plays/play").Count()
            
            match totalPlays with
            | Some t when pageSize > 0 ->
                let maxPage = ((float t) / (float pageSize) |> ceil |> int)
                return Some (Random.Shared.Next(maxPage))
            | _ -> return None
        })
            .ContinueWith(
                (fun (pageOpt: int option Task) ->
                    task {
                        match pageOpt.Result with
                        | Some page ->
                            let! randomPage = fetcher.downloadXmlAsync $"%s{urlPrefix}&page=%d{page}"
                            return Some randomPage
                        | None -> return None
                    }),
                TaskContinuationOptions.OnlyOnRanToCompletion)
            .Unwrap()
    
    let filterExistingGameIds (dbContext: DbContext) (ids: int list) =
        task {
            let! existingGames =
                select {
                    for game in dbContext.Game do
                    where (isIn game.Id ids)
                } |> dbContext.GetConnection().SelectAsync<Game>
                
            return Seq.except (existingGames |> Seq.map _.Id) ids
        }

    let getRandomGameWithSearch (dbContext: DbContext) (stoppingToken: CancellationToken) =
        task {
            let mutable result = Seq.empty
            let mutable tries = 0
            
            while (result |> Seq.isEmpty) && (tries < 10) && (not stoppingToken.IsCancellationRequested) do
                // Pick a random 3 letter prefix of a previously fetched game
                let! gameCountResult =
                    select {
                        for game in dbContext.Game do
                        count "*" "Value"
                    } |> dbContext.GetConnection().SelectAsync<{| Value: int64 |}>
                PreserveRecordFields<{| Value: int64 |}>
                    
                let gameCount = gameCountResult |> Seq.head |> _.Value |> int
                
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
                    |> Option.map _.Split(" ")
                    |> Option.map Seq.randomChoice
                    |> Option.filter (fun word -> word.Length >= 3)
                    |> Option.map _.Substring(0, 3)
                    |> Option.defaultValue "the"
                
                // Search BGG for that prefix
                let! resultXml = fetcher.downloadXmlAsync $"https://boardgamegeek.com/xmlapi2/search?query={searchString}&type=boardgame"
                
                let ids = extractGameIds resultXml
                let! missingIds = filterExistingGameIds dbContext ids
                result <- missingIds |> Seq.randomShuffle
                tries <- tries + 1
 
            return result
        }
        
    
    /// Fetch a random game from BGG that's approximately weighted by plays, by:
    ///
    /// 1. Fetch a random play from the database.
    /// 2. Fetch a random page of plays for that play's game from BGG.
    /// 3. Find a random username in that page.
    /// 4. Fetch a random page of plays for that user from BGG.
    /// 5. Take all game IDs on that page that aren't already in the DB.
    let getRandomGameFromUserPlayHistory (dbContext: DbContext) (stoppingToken: CancellationToken) =
        task {
            let mutable result = Seq.empty
            let mutable tries = 0
            
            while (result |> Seq.isEmpty) && (tries < 10) && (not stoppingToken.IsCancellationRequested) do
                use conn = dbContext.GetConnection()
                
                // Fetch a random play from the DB
                // language=sql
                let! randomPlays =
                    """
with row_count as (
    -- max(rowid) doesn't require a table scan like count(*)
    select max(rowid) as max_rowid from Play
),
random_play_loop(play_id, next_rowid, count) as (
    select
        null,
        abs(random() % (select max_rowid from row_count)) + 1 as next_row_id,
        0
    union all
    select
        case
            when (
                select count(*)
                from Play
                where Play.rowid = next_rowid
            ) = 0 then
                null
            else
                (select Id from Play where Play.rowid = next_rowid)
            end,
        abs(random() % (select max_rowid from row_count)) + 1,
        count + 1
    from random_play_loop
    where
        play_id is null
        and count < 1000
)
select Play.*
from random_play_loop
join Play
    on Play.Id = random_play_loop.play_id;
                """ |> conn.QueryAsync<Play>
                PreserveRecordFields<Play>
                
                // 
                let gameIdOpt = randomPlays |> Seq.tryHead |> Option.map _.GameId
                
                let! randomGamePlaysPage =
                    match gameIdOpt with
                    | None ->
                        task { return None }
                    | Some gameId ->
                        getRandomPlaysPage $"https://boardgamegeek.com/xmlapi2/plays?id=%d{gameId}"
                    
                let randomUsername =
                    match randomGamePlaysPage with
                    | Some page ->
                        let usernames =
                            page
                            |> Option.ofObj
                            |?> _.XPathSelectElements("//plays/play/players/player[string-length(@username) > 0]")
                            |> Option.toList
                            |> Seq.concat
                            |> Seq.collect (fun elem ->
                                attrStr "." "username" elem
                                |> Option.toList)
                        
                        if usernames.Count() > 0 then
                            usernames |> Seq.randomChoice |> Some
                        else
                            None
                    | _ -> None
                
                let! randomUserPlaysPage =
                    match randomUsername with
                    | Some name ->
                        getRandomPlaysPage $"https://boardgamegeek.com/xmlapi2/plays?username=%s{name}"
                    | _ ->
                        task { return None }
                
                // Take the first three letters of a random word
                let ids =
                    randomUserPlaysPage
                    |?> _.XPathSelectElements("//item[//subtype[@value = \"boardgame\"]]")
                    |> Option.toList
                    |> Seq.concat
                    |> Seq.collect (fun elem -> attrInt "." "objectid" elem |> Option.toList)
                    |> Seq.toList
                
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
        
    // Once a minute, if no games are being fetched, check for each of these until a
    // matching game is found.
    //
    // - Games that were unfinished but successfully fetched at least some plays
    // - Finished games that used an old version of the fetcher
    // - Games on the BGG hottest games list
    // - A random game using random search strings
    // - The least recently updated game
    member this.Start(stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()
            
            while (not stoppingToken.IsCancellationRequested) do
                let! nextGameId =
                    task {
                        let mutable result = None
                        
                        let calcResultIfNeeded (thunk: unit -> int option Task) =
                            task {
                                match result with
                                | None ->
                                    let! newResult = thunk()
                                    result <- newResult
                                | Some _ -> ()
                            }
                        
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
                                
                            result <- unfinishedGames |> Seq.tryHead |> Option.map _.Id
                            
                            do! calcResultIfNeeded (fun () ->
                                task {
                                    let! oldGames =
                                        select {
                                            for g in dbContext.Game do
                                                where (isNotNullValue g.UpdateFinishedAt)
                                                andWhere (g.UpdateVersion < currentGameVersion || isNullValue g.UpdateVersion)
                                                orderBy g.Id
                                                take 0 1
                                        }
                                        |> dbContext.GetConnection().SelectAsync<Game>
                                        
                                    return oldGames |> Seq.tryHead |> Option.map _.Id
                                })
                                
                            do! calcResultIfNeeded (fun () ->
                                task {
                                    let! hotGameIds = getNewHotGameIds dbContext
                                    
                                    return hotGameIds |> Seq.tryHead
                                })
                            
                            do! calcResultIfNeeded (fun () ->
                                task {
                                    let! randomGameIds = getRandomGameFromUserPlayHistory dbContext stoppingToken
                                    
                                    return randomGameIds |> Seq.tryHead
                                })
                            
                            do! calcResultIfNeeded (fun () ->
                                task {
                                    let! randomGameIds = getRandomGameWithSearch dbContext stoppingToken
                                    
                                    return randomGameIds |> Seq.tryHead
                                })
                            
                            do! calcResultIfNeeded (fun () ->
                                task {
                                    let! oldestGameId =
                                        select {
                                            for g in dbContext.Game do
                                                where (isNotNullValue g.UpdateFinishedAt)
                                                orderBy g.UpdateFinishedAt
                                                take 0 1
                                        }
                                        |> dbContext.GetConnection().SelectAsync<Game>
                                    
                                    return oldestGameId |> Seq.tryHead |> Option.map _.Id
                                })
                            
                            return result
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