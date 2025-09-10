module GameTime.FetchGame

open System
open System.Data
open System.Net.Http
open System.Xml.Linq
open Dapper
open Dapper.FSharp.SQLite
open GameTime.Models.DbModel

type private FetchAccumulator =
    { delay : int
      page : int }

type private FetchState =
    | FetchStart
    | FetchNextPage
    | GotPage of xmlDoc : XDocument
    | FetchDone

let private downloadXmlAsync (baseUrl: string) (page: int) (delay: int) =
    task {
        use client = new HttpClient()
        let url = $"%s{baseUrl}&page=%d{page}"
        do! Async.Sleep(delay) // Throttle the request with a delay

        let! response = client.GetAsync(url)

        match response.StatusCode with
        | System.Net.HttpStatusCode.OK ->
            let! xmlContent = response.Content.ReadAsStringAsync()
            return Some(XDocument.Parse(xmlContent)) // Return the parsed XDocument
        | System.Net.HttpStatusCode.TooManyRequests ->
            // Handle 429 errors by increasing the delay and retrying
            printfn "Received 429 Too Many Requests. Retrying with backoff..."
            return None
        | _ ->
            // Handle other status codes (e.g., 500, 404) as needed
            printfn $"Error: Status code %d{int response.StatusCode}"
            return None
    }

let private getPagePlayCount (xmlDoc: XDocument) =
    xmlDoc.Descendants("plays")
    |> Seq.collect (fun plays -> plays.Descendants("play"))
    |> Seq.length

let private getPlayTotal (xmlDoc: XDocument) =
    xmlDoc.Descendants("plays")
    |> Seq.tryHead
    |> Option.map _.Attribute(XName.Get("total")).Value
    |> Option.map int
    |> Option.defaultValue 0

let private extractDataFromXml (doc: XDocument) =
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

let startFetchGameTask (conn: IDbConnection) (id: int)  =
    let baseUrl = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}"
    
    let start (acc: FetchAccumulator) =
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
                        value {
                            Id = id
                            AddedAt = DateTime.Now
                            Title = None
                            FetchedPlays = 0
                            TotalPlays = 0
                            UpdateFinishedAt = None
                            UpdateTouchedAt = DateTime.Now
                            UpdateStartedAt = Some DateTime.Now
                        }
                    } |> conn.InsertAsync
                return (FetchNextPage, acc)
            | Some g when g.IsAbandoned() |> not ->
                // If there is an unabandoned job already in progress, let it be.
                return (FetchDone, acc)
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
                    
                return (FetchNextPage, acc)
        }
    
    let fetchPage (acc: FetchAccumulator) =
        task {
            let! xmlResult = downloadXmlAsync baseUrl acc.page acc.delay
            
            match xmlResult with
            | Some xmlDoc ->
                return ((GotPage xmlDoc), acc)
            | None ->
                return (FetchNextPage, { acc with delay = acc.delay * 2 })
        }
    
    let processPage (xmlDoc: XDocument) (acc: FetchAccumulator) =
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
                    
                return (FetchDone, acc)
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
                            Title = COALESCE(@title, Title)
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
                    
                return (FetchNextPage, { acc with page = acc.page + 1 })
        }
    
    task {
        let mutable state = FetchStart
        let mutable acc = { delay = 1000; page = 0 }
        
        while state <> FetchDone do
            let! (newState, newAcc) =
                match state with
                | FetchStart -> start acc
                | FetchNextPage -> fetchPage acc
                | GotPage xmlDoc -> processPage xmlDoc acc
                | FetchDone -> task { return (FetchDone, acc) }
            state <- newState
            acc <- newAcc
    }
    