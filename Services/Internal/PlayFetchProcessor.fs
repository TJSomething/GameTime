namespace GameTime.Services.Internal

open System
open System.Collections.Concurrent
open System.Data
open System.Threading
open System.Threading.Channels
open System.Xml.Linq

open Microsoft.Extensions.DependencyInjection

open Dapper
open Dapper.FSharp.SQLite

open GameTime.DataAccess

type private PlayStatus =
    | FetchNextPage
    | FetchDone

type PlayFetchProcessor(
    fetcher: XmlFetcher,
    serviceProvider: IServiceProvider,
    playJobChannel: ChannelReader<int>,
    jobTracker: ActiveJobTracker
    ) =
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
                }))
        
        
        
    let rec fetchPlayPage (id: int) (page: int) =
        task {
            let url = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}&page=%d{page}"
    
            return! fetcher.downloadXmlAsync url
        }
    
    let writePlayPage (conn: IDbConnection) (id: int) (xmlDoc: XDocument) =
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
                    
                return FetchNextPage
        }
        
    member this.Start (stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()
            while (not stoppingToken.IsCancellationRequested) do
                try
                    use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()
                    
                    let! id = playJobChannel.ReadAsync(stoppingToken)
                    
                    let mutable page = 0
                    let mutable status = FetchNextPage
                    
                    try
                        while (not stoppingToken.IsCancellationRequested && status <> FetchDone) do
                            use conn = dbContext.GetConnection()
                            
                            let! playXml = fetchPlayPage id page
                            let! newStatus = writePlayPage conn id playXml
                            page <- page + 1
                            status <- newStatus
                    finally
                        // If there's a failure, we don't want the system to think the job's still active
                        jobTracker.CloseJob(id) |> ignore
                    ()
                with
                | :? OperationCanceledException -> ()
        }

