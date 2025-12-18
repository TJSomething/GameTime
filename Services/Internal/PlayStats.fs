module GameTime.Services.Internal.PlayStats

open System
open System.Collections.Generic
open System.Linq

open Dapper.FSharp.SQLite
open FSharp.Stats

open GameTime.Data
open GameTime.Data.Entities

type PlayAmountStatsDraft =
    { UniquePlayerIds: int Set
      MinutesPlayed: int
      PlayCount: int }
    
    member this.ToStats(gameId: int, month: int option, playerCount: int option) =
        { GameId = gameId
          Month = month
          PlayerCount = playerCount
          UniquePlayers = this.UniquePlayerIds.Count
          MinutesPlayed = this.MinutesPlayed
          PlayCount = this.PlayCount }

type GamePlayAmountTables =
    {
        Overall: PlayAmountStats option
        ByMonth: Map<int, PlayAmountStats>
        ByPlayerCount: Map<int, PlayAmountStats>
        ByPlayerCountAndMonth: Map<int * int, PlayAmountStats>
    }
    
let dateToMonth (date: DateOnly) =
    date.Year * 12 + date.Month - 1
    
let dayToMonth (day: int) =
    dateToMonth (DateOnly.FromDayNumber(day))
    
let monthToFirstDate (month: int) =
    let year = month / 12 |> int
    let month = month % 12 + 1
    DateOnly(year, month, 1)

/// Handles paginated play stats
type MonthlyPlayStatsJob(gameId: int) =
    let mutable playerCountToDraft = Map.empty<int, PlayAmountStatsDraft>
    let mutable monthToDraft = Map.empty<int, PlayAmountStatsDraft>
    let mutable playerMonthToDraft = Map.empty<int * int, PlayAmountStatsDraft>
    let mutable overallDraft = None
    
    let updateDraft play draftOpt =
        match draftOpt with
        | Some draft ->
            let newUniquePlayers =
                match play.UserId with
                | Some userId -> Set.add userId draft.UniquePlayerIds
                | None -> draft.UniquePlayerIds
            
            Some { PlayCount = draft.PlayCount + 1
                   UniquePlayerIds = newUniquePlayers
                   MinutesPlayed = draft.MinutesPlayed + play.Length }
        | None ->
            Some { PlayCount = 1
                   UniquePlayerIds =
                      match play.UserId with
                      | Some id ->
                          Set.singleton id
                      | None -> Set.empty
                   MinutesPlayed = play.Length }
    member this.ProcessPlays (plays: Play seq) =
        for play in plays do
            let month =
                play.PlayedGregorianDay
                |> Option.map dayToMonth
                |> Option.defaultValue -1
                
            playerCountToDraft <-
                playerCountToDraft
                |> Map.change play.PlayerCount (updateDraft play)
                
            monthToDraft <-
                monthToDraft
                |> Map.change month (updateDraft play)
                
            playerMonthToDraft <-
                playerMonthToDraft
                |> Map.change (play.PlayerCount, month) (updateDraft play)
            
            overallDraft <- updateDraft play overallDraft
    
    member this.GetStats() =
        let overallStats =
            match overallDraft with
            | Some draft -> draft.ToStats(
                gameId = gameId,
                month = None,
                playerCount = None)
            | None ->
                { GameId = gameId
                  Month = None
                  PlayerCount = None
                  UniquePlayers = 0
                  MinutesPlayed = 0
                  PlayCount = 0 }
        let byMonthStats =
             monthToDraft
             |> Map.map (fun month draft -> draft.ToStats(gameId, Some month, None))
        let byPlayerCountStats =
             playerCountToDraft
             |> Map.map (fun playerCount draft -> draft.ToStats(gameId, None, Some playerCount))
        let byPlayerCountAndMonthStats =
             playerMonthToDraft
             |> Map.map (fun (playerCount, month) draft -> draft.ToStats(gameId, Some month, Some playerCount))
        
        Seq.concat [
            Seq.singleton overallStats
            byPlayerCountStats.Values
            byMonthStats.Values
            byPlayerCountAndMonthStats.Values
        ]

let splitStats (stats: PlayAmountStats seq) =
    let mutable playerCountToStat = Map.empty<int, PlayAmountStats>
    let mutable monthToStat = Map.empty<int, PlayAmountStats>
    let mutable playerMonthToStat = Map.empty<int * int, PlayAmountStats>
    let mutable overallStat = None
    
    for stat in stats do
        match (stat.PlayerCount, stat.Month) with
        | Some c, Some m ->
            playerMonthToStat <-
                playerMonthToStat
                |> Map.add (c, m) stat
        | Some c, None ->
            playerCountToStat <-
                playerCountToStat
                |> Map.add c stat
        | None, Some m ->
            monthToStat <-
                monthToStat
                |> Map.add m stat
        | None, None ->
            overallStat <- Some stat

    { Overall = overallStat
      ByMonth = monthToStat
      ByPlayerCount = playerCountToStat
      ByPlayerCountAndMonth = playerMonthToStat }

type PlayForTimeStats =
    { PlayerCount: int
      Length: int }
    
type PlayCountByPlayerCount =
    { Count: int64
      PlayerCount: int64 }

type CachedGamePlayTimeStats =
    { ModifiedAt: DateTime
      PercentileTable: string seq seq
      Average: float }
            
PreserveRecordFields<PlayCountByPlayerCount>
 
type PlayTimePercentileTableJob
    (db: DbContext, id: int) =
    
    let mutable playsProcessed = 0
    
    let mutable playerCountToCurrentIndex = Array.empty<int>

    let mutable playerCountToTimes = Array.empty<float[]>
    
    let mutable average = 0.0
    
    static member Run (db: DbContext, id: int, gameModifiedDateTime) =
        task {
            let job = PlayTimePercentileTableJob(db, id)
            
            do! job.InitializeFromDb()
            
            while! job.FetchAndProcessPlayPage() do
                ()
            
            return
                { ModifiedAt = gameModifiedDateTime
                  PercentileTable = job.BuildTable()
                  Average = job.GetAverage() }
        }
    
    static member STAT_VERSION = 1
    static member GetCacheKey (id: int) = $"game-stats-{id}"
    
    member this.InitializeFromDb() =
        task {
            let! results =
                select {
                    for p in db.Play do
                    count "*" "Count"
                    where (p.GameId = id)
                    groupBy p.PlayerCount
                } |> db.GetConnection().SelectAsync<PlayCountByPlayerCount>
            
            if results |> Seq.length > 0 then
                let playerCountToPlayCount =
                    results
                    |> Seq.map (fun row -> (row.PlayerCount, row.Count))
                    |> Map.ofSeq
                
                playerCountToCurrentIndex <- Array.create (int (playerCountToPlayCount.Keys.Max() + 1L)) 0
                
                playerCountToTimes <-
                    Array.init
                        (int (playerCountToPlayCount.Keys.Max() + 1L))
                        (fun i ->
                            let l = playerCountToPlayCount.GetValueOrDefault(i, 0)
                            Array.create (int l) 0.0)
        }
    
    member this.FetchAndProcessPlayPage () =
        task {
            let! plays =
                select {
                    for p in db.Play do
                        where (p.GameId = id)
                        take playsProcessed 10000
                }
                |> db.GetConnection().SelectAsync<Play>
             
            if plays |> Seq.length > 0 then
                for play in plays do
                    let count = play.PlayerCount
                    let index = playerCountToCurrentIndex[count]
                    playerCountToTimes[count][index] <- play.Length |> float
                    playerCountToCurrentIndex[count] <- index + 1
                    
                    // Cumulative average
                    average <- ((float play.Length) + (float playsProcessed) * average) / ((float playsProcessed) + 1.0)
                    playsProcessed <- playsProcessed + 1
                    
                return true
            else
                return false
        }
    
    member this.GetAverage () = average
    
    member this.BuildTable () =
        let ps = [ 0.1..0.1..0.9 ]

        seq {
            // Header row
            yield
                seq {
                    yield "Players"
                    yield "Plays"

                    for p in ps do
                        yield (sprintf "%d%%" (int (p * 100.0)))
                }

            // Row for each player count
            for playerCount, times in playerCountToTimes.Index() do
                Array.Sort(times)
                let playCount = times.Length
                if playCount > 0 then
                    let qs = Seq.map (fun p -> Quantile.OfSorted.compute p times) ps

                    yield
                        seq {
                            yield $"%d{playerCount}"
                            yield $"%d{playCount}"

                            for q in qs do
                                yield $"%.0f{q}"
                        }
        }