module GameTime.Data.Entities

open System
open System.Diagnostics.CodeAnalysis
open System.Reflection

[<CLIMutable>]
type Game =
    { Id: int
      Title: string option
      YearPublished: int option
      BoxMinPlayTime: int option
      BoxPlayTime: int option
      BoxMaxPlayTime: int option
      BoxMinPlayers: int option
      BoxMaxPlayers: int option
      UsersRated: int option
      AverageRating: double option
      BayesAverageRating: double option
      StdDevRating: double option
      Owned: int option
      NumWeights: int option
      AverageWeight: double option
      AddedAt: DateTime
      UpdateStartedAt: DateTime option
      UpdateTouchedAt: DateTime
      UpdateFinishedAt: DateTime option
      UpdateVersion: int option
      FetchedPlays: int
      TotalPlays: int }
    with
    static member CURRENT_VERSION = 3

type PlayerCountVote =
    { GameId: int
      PlayerCount: int option
      Best: int
      Recommended: int
      NotRecommended: int }

[<CLIMutable>]
type Play =
    { Id: int
      GameId: int
      UserId: int option
      Length: int
      PlayerCount: int
      PlayedGregorianDay: int option }

type PlayAmountStats =
    { GameId: int
      Month: int option
      PlayerCount: int option
      UniquePlayers: int
      MinutesPlayed: int
      PlayCount: int }

[<CLIMutable>]
[<DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)>]
type Migration =
    { Id: int64 }

type CacheItem =
    { Id: string
      Version: int
      Value: string }

type Report =
    { Id: Guid
      CreatedAt: DateTime
      UpdatedAt: DateTime
      Status: string
      Query: string
      Result: string option }

type PlayTimePercentile =
    { GameId: int
      PlayerCount: int option
      Percentile: int
      PlayTime: int }

/// Reference record fields to ensure that entities property names can be reflected for queries
let inline PreserveRecordFields<'T> =
    FSharp.Reflection.FSharpType.GetRecordFields(typeof<'T>, BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.NonPublic)
    |> ignore
 
