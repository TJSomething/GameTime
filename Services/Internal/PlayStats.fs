module GameTime.Services.Internal.PlayStats

open System

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
    
let calcMonthlyStats (gameId: int) (plays: Play seq) =
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