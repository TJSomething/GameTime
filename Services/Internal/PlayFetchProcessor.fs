namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Channels
open System.Xml.Linq

open GameTime.Data
open GameTime.Data.Entities
open Microsoft.Extensions.DependencyInjection

open Dapper
open Dapper.FSharp.SQLite

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
            
            let userIdOpt =
                play.Attribute("userid").Value
                |> Option.ofObj
                |> Option.map int

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
            | None, _
            | _, 0 -> Seq.empty
            | Some validId, validTime ->
                Seq.singleton (gameTitle, {
                    Id = validId
                    GameId = gameId
                    UserId = userIdOpt
                    Length = validTime
                    PlayerCount = playerCount
                }))
        
    let rec fetchPlayPage (id: int) (page: int) =
        task {
            let url = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}&page=%d{page}"
    
            return! fetcher.downloadXmlAsync url
        }
    
    let writePlayPage (db: DbContext) (id: int) (xmlDoc: XDocument) =
        task {
            let pagePlayCount = getPagePlayCount xmlDoc
            
            if pagePlayCount = 0 then
                let! _ =
                    db.GetConnection().ExecuteAsync(
                        """
                        update Game
                        set UpdateTouchedAt = @now,
                            UpdateStartedAt = coalesce(UpdateStartedAt, @now),
                            UpdateFinishedAt = @now
                        where id = @id
                        """,
                        {| now = DateTime.Now
                           id = id |})
                    
                return FetchDone
            else
                let titlePlayPairs = extractDataFromXml xmlDoc
                
                let title =
                    match Seq.tryHead titlePlayPairs with
                    | Some (t, _) -> Some t
                    | None -> None
                    
                let total = getPlayTotal xmlDoc
                
                let! _ =
                    db.GetConnection().ExecuteAsync(
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
                            into db.PlayTable
                            values plays
                        } |> db.GetConnection().InsertOrReplaceAsync
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
                            let! playXml = fetchPlayPage id page
                            let! newStatus = writePlayPage dbContext id playXml
                            page <- page + 1
                            status <- newStatus
                    finally
                        // If there's a failure, we don't want the system to think the job's still active
                        jobTracker.CloseJob(id) |> ignore
                    ()
                with
                | :? OperationCanceledException -> ()
        }

