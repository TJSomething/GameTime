namespace GameTime.Services.Internal

open System
open System.Threading
open System.Threading.Channels
open System.Xml.Linq
open System.Xml.XPath

open GameTime.Data
open GameTime.Data.Entities
open Microsoft.Extensions.DependencyInjection

open Dapper.FSharp.SQLite

type GameInitializationProcessor
    (
        fetcher: XmlFetcher,
        playFetchChannel: ChannelWriter<int>,
        jobTracker: ActiveJobTracker,
        serviceProvider: IServiceProvider
    ) =
    let jobQueue: int Channel = Channel.CreateUnbounded()

    let initializeJob (db: DbContext) (id: int) =
        task {
            let! gameResult =
                select {
                    for g in db.GameTable do
                        where (g.Id = id)
                }
                |> db.GetConnection().SelectAsync<Game>

            match Seq.tryHead gameResult with
            | None ->
                let! _ =
                    insert {
                        into db.GameTable
                        value
                            { Id = id
                              AddedAt = DateTime.Now
                              Title = None
                              FetchedPlays = 0
                              TotalPlays = 0
                              UpdateFinishedAt = None
                              UpdateTouchedAt = DateTime.Now
                              UpdateStartedAt = None }
                              //YearPublished = None
                              //BoxMinPlayTime = None
                              //BoxPlayTime = None
                              //BoxMaxPlayTime = None
                              //BoxMinPlayers = None
                              //BoxMaxPlayers = None
                              //UpdateStatus = None
                              //UpdateVersion = 2
                    }
                    |> db.GetConnection().InsertAsync

                return ()
            | Some _ ->
                let! _ =
                    update {
                        for g in db.GameTable do
                            setColumn g.UpdateStartedAt (Some DateTime.Now)
                            setColumn g.UpdateTouchedAt DateTime.Now
                            setColumn g.FetchedPlays 0
                            setColumn g.TotalPlays 0
                            where (g.Id = id)
                    }
                    |> db.GetConnection().UpdateAsync

                let! _ =
                    delete {
                        for p in db.PlayTable do
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
            let name =
                xmlDoc
                |> Option.ofObj
                |> Option.map _.XPathSelectElement("//items/item/name[@value]")
                |> Option.bind Option.ofObj
                |> Option.map _.Attribute(XName.Get("value"))
                |> Option.bind Option.ofObj
                |> Option.map _.Value
                |> Option.bind Option.ofObj

            let! _ =
                update {
                    for g in db.GameTable do
                        setColumn g.Title name
                        where (g.Id = id)
                }
                |> db.GetConnection().UpdateAsync

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
                            do! initializeJob dbContext id
                            let! gameXml = fetchGame id
                            do! writeGameInfo dbContext id gameXml
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
