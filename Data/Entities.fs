module GameTime.Data.Entities

open System

type Game =
    { Id: int
      Title: string option
      YearPublished: int option
      BoxMinPlayTime: int option
      BoxPlayTime: int option
      BoxMaxPlayTime: int option
      BoxMinPlayers: int option
      BoxMaxPlayers: int option
      AddedAt: DateTime
      UpdateStartedAt: DateTime option
      UpdateTouchedAt: DateTime
      UpdateFinishedAt: DateTime option
      UpdateVersion: int option
      FetchedPlays: int
      TotalPlays: int }

type PlayCountVotes =
    { GameId: int
      PlayerCount: int option
      Best: int
      Recommended: int
      NotRecommended: int }

type Play =
    { Id: int
      GameId: int
      UserId: int option
      Length: int
      PlayerCount: int }

type User =
    { Id: int
      Username: string }
    
type TagType =
    | Category = 0
    | Mechanic = 1
    | Family = 2

type GameTag =
    { TagType: TagType
      Id: int
      Name: string }

type Migration =
    { Id: int }