namespace GameTime.Services

open System
open System.Collections.Concurrent
open System.Data
open System.Net.Http
open System.Threading.Tasks
open System.Xml.Linq
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

open Dapper
open Dapper.FSharp.SQLite

open GameTime.DataAccess

type private Job =
    | FetchStart of id : int
    | FetchNextPage of id : int * page : int
    | GotPage of id : int * page : int * xmlDoc : XDocument
    | FetchDone
    
type GameFetcherService(serviceProvider: IServiceProvider, logger: ILogger<GameFetcherService>) =
    inherit BackgroundService()
    
    let mutable delay = 1000
    let jobQueue: Job ConcurrentQueue = ConcurrentQueue()
        
    let getPagePlayCount (xmlDoc: XDocument) =
        xmlDoc.Descendants("plays")
        |> Seq.collect _.Descendants("play")
        |> Seq.length

    let getPlayTotal (xmlDoc: XDocument) =
        xmlDoc.Descendants("plays")
        |> Seq.tryHead
        |> Option.map _.Attribute(XName.Get("total")).Value
        |> Option.map int
        |> Option.defaultValue 0

    let extractDataFromXml (doc: XDocument) =
        doc.Descendants("plays")
        |> Seq.collect _.Descendants("play")
        |> Seq.collect (fun play ->
            let id =
                play.Attribute("id").Value
                |> Option.ofObj
                |> Option.map int
            
            let gameTitle =
                play.Descendants(XName.Get("item"))
                |> Seq.head
                |> _.Attribute(XName.Get("name")).Value
            
            let gameId =
                play.Descendants(XName.Get("item"))
                |> Seq.head
                |> _.Attribute(XName.Get("objectid")).Value
                |> int

            let time =
                play.Attribute(XName.Get("length")).Value
                |> Option.ofObj
                |> Option.map int
                |> Option.defaultValue 0

            let playerCountOpt =
                play.Descendants(XName.Get("players"))
                |> Seq.tryHead
                |> Option.map (fun players -> players.Descendants(XName.Get("player")) |> Seq.length)

            let playerCount = playerCountOpt |> Option.defaultValue 0

            match (id, time) with
            | _, 0 -> Seq.empty
            | None, _ -> Seq.empty
            | Some validId, validTime ->
                Seq.singleton (gameTitle, {
                    Id = validId
                    GameId = gameId
                    Length = validTime
                    PlayerCount = playerCount
                    FetchedAt = DateTime.Now
                }))
        
    let downloadXmlAsync (baseUrl: string) (page: int) (delay: int) =
        task {
            use client = new HttpClient()
            let url = $"%s{baseUrl}&page=%d{page}"
            do! Task.Delay(delay) // Throttle the request with a delay

            let! response = client.GetAsync(url)

            match response.StatusCode with
            | System.Net.HttpStatusCode.OK ->
                let! xmlContent = response.Content.ReadAsStringAsync()
                return Some(XDocument.Parse(xmlContent)) // Return the parsed XDocument
            | System.Net.HttpStatusCode.TooManyRequests ->
                // Handle 429 errors by increasing the delay and retrying
                logger.LogWarning("Received 429 Too Many Requests. Retrying with backoff...")
                return None
            | _ ->
                // Handle other status codes (e.g., 500, 404) as needed
                logger.LogError($"Error: Status code %d{int response.StatusCode}")
                return None
        }
        
    let handleStart (conn: IDbConnection) (id: int) =
        task {
            let! gameResult =
                select {
                    for g in gameTable do
                    where (g.Id = id)
                }
                |> conn.SelectAsync<Game>
                
            let jobExists =
                jobQueue.AsList().Exists(
                    fun j ->
                        let candidateId =
                            match j with
                            | FetchNextPage(id = candidateId) -> Some candidateId
                            | GotPage(id = candidateId) -> Some candidateId
                            | _ -> None
                            
                        candidateId = Some id)
            
            match Seq.tryHead gameResult with
            | None ->
                let! _ =
                    insert {
                        into gameTable
                        value {
                            Id = id
                            AddedAt = DateTime.Now
                            Title = None
                            FetchedPlays = 0
                            TotalPlays = 0
                            UpdateFinishedAt = None
                            UpdateTouchedAt = DateTime.Now
                            UpdateStartedAt = None
                        }
                    } |> conn.InsertAsync
                return FetchNextPage(id = id, page = 0)
            | Some _ when jobExists ->
                // Skip the job if it exists already
                return FetchDone
            | Some _ ->
                let! _ =
                    update {
                        for g in gameTable do
                        setColumn g.UpdateStartedAt (Some DateTime.Now)
                        setColumn g.UpdateTouchedAt DateTime.Now
                        setColumn g.FetchedPlays 0
                        setColumn g.TotalPlays 0
                        where (g.Id = id)
                    } |> conn.UpdateAsync
                    
                let! _ =
                    delete {
                        for p in playTable do
                        where (p.GameId = id)
                    }
                    |> conn.DeleteAsync
                    
                return FetchNextPage(id = id, page = 0)
        }
        
    let fetchPage (id: int) (page: int) =
        task {
            let baseUrl = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}"
    
            let! xmlResult = downloadXmlAsync baseUrl page delay
            
            match xmlResult with
            | Some xmlDoc ->
                return GotPage(id = id, page = page, xmlDoc = xmlDoc)
            | None ->
                delay <- delay * 2
                return FetchNextPage(id = id, page = page)
        }
    
    let processPage (conn: IDbConnection) (id: int) (page: int) (xmlDoc: XDocument) =
        task {
            let pagePlayCount = getPagePlayCount xmlDoc
            
            if pagePlayCount = 0 then
                let! _ =
                    update {
                        for g in gameTable do
                        setColumn g.UpdateFinishedAt (Some DateTime.Now)
                        setColumn g.UpdateTouchedAt DateTime.Now
                        where (g.Id = id)
                    } |> conn.UpdateAsync
                    
                return FetchDone
            else
                let titlePlayPairs = extractDataFromXml xmlDoc
                
                let title =
                    match Seq.tryHead titlePlayPairs with
                    | Some (t, _) -> Some t
                    | None -> None
                    
                let total = getPlayTotal xmlDoc
                
                let! _ =
                    conn.ExecuteAsync(
                        """
                        update Game
                        set FetchedPlays = FetchedPlays + @pagePlayCount,
                            TotalPlays = @total,
                            UpdateTouchedAt = @now,
                            UpdateStartedAt = coalesce(UpdateStartedAt, @now),
                            Title = coalesce(@title, Title)
                        where id = @id
                        """,
                        {| id = id
                           pagePlayCount = pagePlayCount
                           total = total
                           now = DateTime.Now
                           title = title |})
            
                let plays =
                    titlePlayPairs
                    |> Seq.map snd
                    |> Seq.toList
            
                if List.length plays > 0 then
                    let! _ =
                        insert {
                            into playTable
                            values plays
                        } |> conn.InsertOrReplaceAsync
                    ()
                    
                return FetchNextPage(id = id, page = page + 1)
        }
        
    member this.EnqueueFetch (id: int) =
        logger.LogDebug("enqueuing job {id}", id)
        jobQueue.Enqueue(FetchStart(id))

    override this.ExecuteAsync(stoppingToken) =
        task {
            while (not stoppingToken.IsCancellationRequested) do
                use scope = serviceProvider.CreateAsyncScope()
                use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()
                
                match jobQueue.TryDequeue() with
                | false, _ ->
                    do! Task.Delay 1000
                | true, j ->
                    logger.LogDebug("processing job {job}", j)
                    let! newJob =
                        match j with
                        | FetchStart id ->
                            use conn = dbContext.GetConnection()
                            handleStart conn id
                        | FetchNextPage(id, page) -> fetchPage id page
                        | GotPage(id, page, xmlDoc) ->
                            use conn = dbContext.GetConnection()
                            processPage conn id page xmlDoc
                        | FetchDone -> task { return FetchDone }
                    
                    if newJob <> FetchDone then
                        jobQueue.Enqueue(newJob)
        }
                
