namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Xml.Linq

open GameTime.Data
open GameTime.Data.DbCache
open GameTime.Data.Entities
open GameTime.Services.Internal.PlayStats
open GameTime.XmlUtils
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection

open Dapper
open Dapper.FSharp.SQLite

type private PlayStatus =
    | FetchNextPage
    | FetchDone
    
type private ParsedPlay =
    { GameTitle: string option
      Play: Play }

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
            let id = attrInt "." "id" play
            
            let gameTitle = attrStr "item" "name" play
            
            let gameId =
                attrInt "item" "objectid" play
                |> Option.get
            
            let userIdOpt = attrInt "." "userid" play

            let time =
                play
                |> attrInt "." "length"
                |> Option.defaultValue 0

            let day =
                play
                |> attrStr "." "date"
                |> Option.bind (fun str ->
                    match DateOnly.TryParse(str) with
                    | true, date -> Some date.DayNumber
                    | false, _ -> None)
                
            let playerCountOpt =
                play.Descendants(XName.Get("players"))
                |> Seq.tryHead
                |> Option.map (fun players -> players.Descendants(XName.Get("player")) |> Seq.length)

            let playerCount = playerCountOpt |> Option.defaultValue 0

            match (id, time) with
            | None, _
            | _, 0 -> Seq.empty
            | Some validId, validTime ->
                Seq.singleton {
                    GameTitle = gameTitle
                    Play = {
                        Id = validId
                        GameId = gameId
                        UserId = userIdOpt
                        Length = validTime
                        PlayerCount = playerCount
                        PlayedGregorianDay = day
                    }
                })
        
    let rec fetchPlayPage (id: int) (page: int) =
        task {
            let url = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}&page=%d{page}"
    
            return! fetcher.downloadXmlAsync url
        }
    
    let writePlayPage (db: DbContext) (id: int) (xmlDoc: XDocument) =
        task {
            let pagePlayCount = getPagePlayCount xmlDoc
            
            if pagePlayCount = 0 then
                return FetchDone
            else
                let parsedPlays = extractDataFromXml xmlDoc
                
                let title =
                    match Seq.tryHead parsedPlays with
                    | Some { GameTitle = Some t } -> Some t
                    | _ -> None
                    
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
                    parsedPlays
                    |> Seq.map _.Play
                    |> Seq.toList
            
                if List.length plays > 0 then
                    let! _ =
                        insert {
                            into db.Play
                            values plays
                        } |> db.GetConnection().InsertOrReplaceAsync
                    ()
            
                return FetchNextPage
        }
        
    
    let finalizePlays (db: DbContext) (id: int) =
        task {
            let mutable notDone = true
            let mutable currentOffset = 0
            let statsJob = MonthlyPlayStatsJob(id)
            
            while notDone do
                let! plays =
                    select {
                        for p in db.Play do
                            where (p.GameId = id)
                            take currentOffset 1000
                    }
                    |> db.GetConnection().SelectAsync<Play>
                    
                let playsFound = plays |> Seq.length
                
                if playsFound = 0 then
                    notDone <- false
                else
                    statsJob.ProcessPlays(plays)
                    currentOffset <- currentOffset + playsFound
            
            let stats = statsJob.GetStats()
            
            if Seq.length stats > 0 then
                let! _ =
                    delete {
                        for s in db.PlayAmountStats do
                        where (s.GameId = id)
                    } |> db.GetConnection().DeleteAsync
                let! _ =
                    insert {
                        into db.PlayAmountStats
                        values (Seq.toList stats)
                    } |> db.GetConnection().InsertOrReplaceAsync
                ()
            
            let now = DateTime.Now
            
            let! playTimePercentileTable = PlayTimePercentileTableJob.Run(db, id, now)
            do! addToCache
                    db
                    (PlayTimePercentileTableJob.GetCacheKey(id))
                    PlayTimePercentileTableJob.STAT_VERSION
                    playTimePercentileTable
                
            let! _ =
                db.GetConnection().ExecuteAsync(
                    """
                    update Game
                    set UpdateTouchedAt = @now,
                        UpdateStartedAt = coalesce(UpdateStartedAt, @now),
                        UpdateFinishedAt = @now
                    where id = @id
                    """,
                    {| now = now
                       id = id |})
            ()
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
                            let mutable notDone = true
                            while notDone do
                                try
                                    let! newStatus = writePlayPage dbContext id playXml
                                    page <- page + 1
                                    status <- newStatus
                                    notDone <- false
                                with
                                | :? SqliteException as ex ->
                                    // Wait if the database is locked
                                    if ex.SqliteErrorCode <> 5 then
                                        raise ex
                                    else
                                        do! Task.Delay 100
                            
                            if status = FetchDone then
                                do! finalizePlays dbContext id
                    finally
                        // If there's a failure, we don't want the system to think the job's still active
                        jobTracker.CloseJob(id) |> ignore
                    ()
                with
                | :? OperationCanceledException -> ()
        }

