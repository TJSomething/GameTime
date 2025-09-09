module gametime.FetchGame

open System
open System.Data
open System.Net.Http
open System.Xml.Linq
open Dapper.FSharp.SQLite
open gametime.Models.DbModel

type private FetchAccumulator =
    { playsCounted: int
      delay : int
      page : int
      entityInitialized : bool }

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
    let rec go (state: FetchState) (acc: FetchAccumulator) =
        task {
            let baseUrl = $"https://boardgamegeek.com/xmlapi2/plays?id=%d{id}"
            
            match state with
            | FetchStart ->
                let! _ =
                    delete {
                        for p in playTable do
                        where (p.GameId = id)
                    }
                    |> conn.DeleteAsync
                    
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
                                TotalPlays = 0
                                UpdateFinishedAt = None
                                UpdateStartedAt = Some DateTime.Now
                            }
                        } |> conn.InsertAsync
                    ()
                | Some _ ->
                    let! _ =
                        update {
                            for g in gameTable do
                            setColumn g.UpdateStartedAt (Some DateTime.Now)
                            setColumn g.TotalPlays 0
                            where (g.Id = id)
                        } |> conn.UpdateAsync
                    ()
                
                return (FetchNextPage, acc)
            | FetchNextPage ->
                let! xmlResult = downloadXmlAsync baseUrl acc.page acc.delay
                
                match xmlResult with
                | Some xmlDoc ->
                    return ((GotPage xmlDoc), acc)
                | None ->
                    return (FetchNextPage, { acc with delay = acc.delay * 2 })
            | GotPage xmlDoc ->
                let plays = extractDataFromXml xmlDoc
                
                if not acc.entityInitialized then
                    let title =
                        match Seq.tryHead plays with
                        | Some (t, _) -> t
                        | None -> "<unknown>"
                    let total = getPlayTotal xmlDoc
                    
                    let! _ =
                        update {
                            for g in gameTable do
                            setColumn g.TotalPlays total
                            setColumn g.Title (Some title)
                            where (g.Id = id)
                        }
                        |> conn.UpdateAsync
                    
                    ()
                    
                let plays =
                    plays
                    |> Seq.map snd
                    |> Seq.toList
                
                let! _ =
                    insert {
                        into playTable
                        values plays
                    } |> conn.InsertOrReplaceAsync
                
                let pageCount = List.length plays
                
                if pageCount = 0 then
                    return (FetchDone, acc)
                else
                    return (FetchNextPage, {
                        acc with
                            page = acc.page + 1
                            playsCounted = acc.playsCounted + pageCount
                            entityInitialized = true
                    })
            | FetchDone -> return (FetchDone, acc)
        }
    
    task {
        let mutable state = FetchStart
        let mutable acc = { playsCounted = 0; delay = 1000; entityInitialized = false; page = 0 }
        
        while state <> FetchDone do
            let! (newState, newAcc) = go state acc
            state <- newState
            acc <- newAcc
    }
    