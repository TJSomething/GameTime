namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Channels
open System.Xml.Linq
open System.Xml.XPath

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

open GameTime.Data
open GameTime.Data.Entities
open GameTime.XmlUtils

open Dapper.FSharp.SQLite

type GameInitializationProcessor
    (
        fetcher: XmlFetcher,
        logger: ILogger,
        playFetchChannel: ChannelWriter<int>,
        jobTracker: ActiveJobTracker,
        serviceProvider: IServiceProvider
    ) =
    let jobQueue: int Channel = Channel.CreateUnbounded()

    let initializeJob (db: DbContext) (id: int) =
        task {
            let! gameResult =
                select {
                    for g in db.Game do
                        where (g.Id = id)
                }
                |> db.GetConnection().SelectAsync<Game>

            match Seq.tryHead gameResult with
            | None ->
                let! _ =
                    insert {
                        into db.Game
                        value
                            { Id = id
                              AddedAt = DateTime.Now
                              Title = None
                              FetchedPlays = 0
                              TotalPlays = 0
                              UpdateFinishedAt = None
                              UpdateTouchedAt = DateTime.Now
                              UpdateStartedAt = None
                              YearPublished = None
                              BoxMinPlayTime = None
                              BoxPlayTime = None
                              BoxMaxPlayTime = None
                              BoxMinPlayers = None
                              BoxMaxPlayers = None
                              UpdateVersion = Some 1 }
                    }
                    |> db.GetConnection().InsertAsync

                return ()
            | Some _ ->
                let! _ =
                    update {
                        for g in db.Game do
                            setColumn g.UpdateStartedAt (Some DateTime.Now)
                            setColumn g.UpdateFinishedAt None
                            setColumn g.UpdateTouchedAt DateTime.Now
                            setColumn g.FetchedPlays 0
                            setColumn g.TotalPlays 0
                            setColumn g.UpdateVersion (Some 1)
                            where (g.Id = id)
                    }
                    |> db.GetConnection().UpdateAsync

                let! _ =
                    delete {
                        for p in db.Play do
                            where (p.GameId = id)
                    }
                    |> db.GetConnection().DeleteAsync

                return ()
        }

    let rec fetchGame (id: int) =
        task {
            let url = $"https://boardgamegeek.com/xmlapi2/thing?id=%d{id}&type=boardgame"

            return! fetcher.downloadXmlAsync url
        }
    
    let writeGameInfo (db: DbContext) (id: int) (xmlDoc: XDocument) =
        task {
            let name = attrStr "//items/item/name[@value]" "value" xmlDoc
            let year = attrInt "//items/item/yearpublished[@value]" "value" xmlDoc
            let minPlayers = attrInt "//items/item/minplayers[@value]" "value" xmlDoc
            let maxPlayers = attrInt "//items/item/maxplayers[@value]" "value" xmlDoc
            let playTime = attrInt "//items/item/playingtime[@value]" "value" xmlDoc
            let minPlayTime = attrInt "//items/item/minplaytime[@value]" "value" xmlDoc
            let maxPlayTime = attrInt "//items/item/maxplaytime[@value]" "value" xmlDoc

            let! _ =
                update {
                    for g in db.Game do
                        setColumn g.Title name
                        setColumn g.YearPublished year
                        setColumn g.BoxPlayTime playTime
                        setColumn g.BoxMinPlayTime minPlayTime
                        setColumn g.BoxMaxPlayTime maxPlayTime
                        setColumn g.BoxMinPlayers minPlayers
                        setColumn g.BoxMaxPlayers maxPlayers
                        where (g.Id = id)
                }
                |> db.GetConnection().UpdateAsync

            ()
        }
    
    let getPlayerCountVotes (id: int) (xmlDoc: XDocument) =
        xmlDoc
        |> Option.ofObj
        |?> _.XPathSelectElements("//items/item/poll[@name=\"suggested_numplayers\"]/results")
        |> Option.toList
        |> Seq.concat
        |> Seq.collect (fun elem ->
            let playerCount = attrInt "." "numplayers" elem
            let best = attrInt "result[@value=\"Best\"]" "numvotes" elem
            let recommended = attrInt "result[@value=\"Recommended\"]" "numvotes" elem
            let notRecommended = attrInt "result[@value=\"Not Recommended\"]" "numvotes" elem
            
            match best, recommended, notRecommended with
            | Some b, Some r, Some n ->
                [{ GameId = id
                   PlayerCount = playerCount
                   Best = b
                   Recommended = r
                   NotRecommended = n }]
            | _ -> [])
        
    let writePlayerCountVotes (db: DbContext) (votes: PlayerCountVote seq) =
        task {
            let conn = db.GetConnection()
            let! _ =
                insert {
                    into db.PlayerCountVote
                    values (Seq.toList votes)
                }
                |> conn.InsertAsync
            ()
        }
    
    member this.Start(stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()

            while (not stoppingToken.IsCancellationRequested) do
                try
                    use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()

                    let! id = jobQueue.Reader.ReadAsync(stoppingToken)

                    if jobTracker.StartJob(id) then
                        try
                            logger.LogInformation("Fetching game #{Game}", id)
                            do! initializeJob dbContext id
                            let! gameXml = fetchGame id
                            do! writeGameInfo dbContext id gameXml
                            let playerCountVotes = getPlayerCountVotes id gameXml
                            if playerCountVotes |> Seq.isEmpty |> not then
                                do! writePlayerCountVotes dbContext playerCountVotes
                        with ex ->
                            jobTracker.CloseJob(id) |> ignore
                            raise (Exception($"Error in fetching game {id}", ex))

                        do! playFetchChannel.WriteAsync(id, stoppingToken)
                    else
                        ()

                    ()
                with :? OperationCanceledException ->
                    ()
        }

    member this.EnqueueFetch(id: int) = jobQueue.Writer.TryWrite(id) |> ignore
