namespace GameTime.Services.Internal

open System
open System.Collections.Concurrent
open System.Data
open System.Threading
open System.Threading.Channels
open System.Xml.Linq
open System.Xml.XPath

open Microsoft.Extensions.DependencyInjection

open Dapper.FSharp.SQLite

open GameTime.DataAccess

type GameInitializationProcessor
    (
        fetcher: XmlFetcher,
        playFetchChannel: ChannelWriter<int>,
        activeGameIds: ConcurrentDictionary<int, unit>,
        serviceProvider: IServiceProvider
    ) =
    let jobQueue: int Channel = Channel.CreateUnbounded()

    let initializeJob (conn: IDbConnection) (id: int) =
        task {
            let! gameResult =
                select {
                    for g in gameTable do
                        where (g.Id = id)
                }
                |> conn.SelectAsync<Game>

            match Seq.tryHead gameResult with
            | None ->
                let! _ =
                    insert {
                        into gameTable

                        value
                            { Id = id
                              AddedAt = DateTime.Now
                              Title = None
                              FetchedPlays = 0
                              TotalPlays = 0
                              UpdateFinishedAt = None
                              UpdateTouchedAt = DateTime.Now
                              UpdateStartedAt = None }
                    }
                    |> conn.InsertAsync

                return ()
            | Some _ ->
                let! _ =
                    update {
                        for g in gameTable do
                            setColumn g.UpdateStartedAt (Some DateTime.Now)
                            setColumn g.UpdateTouchedAt DateTime.Now
                            setColumn g.FetchedPlays 0
                            setColumn g.TotalPlays 0
                            where (g.Id = id)
                    }
                    |> conn.UpdateAsync

                let! _ =
                    delete {
                        for p in playTable do
                            where (p.GameId = id)
                    }
                    |> conn.DeleteAsync

                return ()
        }

    let rec fetchGame (id: int) =
        task {
            let url = $"https://boardgamegeek.com/xmlapi2/thing?id=%d{id}&type=boardgame"

            return! fetcher.downloadXmlAsync url
        }

    let writeGameInfo (conn: IDbConnection) (id: int) (xmlDoc: XDocument) =
        task {
            let name =
                xmlDoc.XPathSelectElement("//items/item/name[@value]").Attribute(
                    XName.Get("value")
                )
                    .Value

            let! _ =
                update {
                    for g in gameTable do
                        setColumn g.Title (Some name)
                        where (g.Id = id)
                }
                |> conn.UpdateAsync

            ()
        }

    member this.Start(stoppingToken: CancellationToken) =
        task {
            use scope = serviceProvider.CreateAsyncScope()

            while (not stoppingToken.IsCancellationRequested) do
                try
                    use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()

                    let! id = jobQueue.Reader.ReadAsync(stoppingToken)
                    use conn = dbContext.GetConnection()

                    if activeGameIds.TryAdd(id, ()) then
                        try
                            do! initializeJob conn id
                            let! gameXml = fetchGame id
                            do! writeGameInfo conn id gameXml
                        with ex ->
                            activeGameIds.TryRemove(id) |> ignore
                            raise (Exception($"Error in fetching game {id}", ex))

                        do! playFetchChannel.WriteAsync(id, stoppingToken)
                    else
                        ()

                    ()
                with :? OperationCanceledException ->
                    ()
        }

    member this.EnqueueFetch(id: int) = jobQueue.Writer.TryWrite(id) |> ignore
