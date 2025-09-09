module gametime.Models.DbModel

open System
open System.Data
open Dapper
open Dapper.FSharp.SQLite
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration

type Game =
    { Id: int
      Title: string option
      AddedAt: DateTime
      UpdateStartedAt: DateTime option
      UpdateTouchedAt: DateTime
      UpdateFinishedAt: DateTime option
      FetchedPlays : int
      TotalPlays: int }

    member this.IsAbandoned() =
        this.UpdateFinishedAt.IsNone && TimeSpan.FromSeconds(60L) < DateTime.Now - this.UpdateTouchedAt

type Play =
    { Id: int
      GameId: int
      Length: int
      PlayerCount: int
      FetchedAt: DateTime }

let GetConnection () =
    let conf = ConfigurationBuilder().AddJsonFile("settings.json").Build()
    new SqliteConnection(conf.["sqliteConnectionString"])

let gameTable = table<Game>
let playTable = table<Play>

module private Internal =
    let mutable isAlreadyInitialized = false

    let initGame (conn: IDbConnection) =
        task {
            let! _ =
                """
                create table if not exists Game
                (
                    Id int identity
                        constraint Game_pk
                            primary key,
                    Title text null,
                    AddedAt text not null,
                    UpdateStartedAt text null,
                    UpdateTouchedAt text null,
                    UpdateFinishedAt text null,
                    FetchedPlays int not null,
                    TotalPlays int not null
                )
                """
                |> conn.ExecuteAsync

            return ()
        }

    let initPlay (conn: IDbConnection) =
        task {
            let! _ =
                """
                create table if not exists Play
                (
                    Id int identity
                        constraint Play_pk
                            primary key,
                    GameId int not null,
                    Length int not null,
                    PlayerCount int not null,
                    FetchedAt text not null,
                    foreign key(GameId) References Game(Id)
                )
                """
                |> conn.ExecuteAsync

            return ()
        }

let safeInit (conn: IDbConnection) =
    task {
        if Internal.isAlreadyInitialized |> not then
            conn.Open()
            let! _ = "PRAGMA foreign_keys = ON;" |> conn.ExecuteAsync
            do! Internal.initGame conn
            do! Internal.initPlay conn
            Internal.isAlreadyInitialized <- true
            OptionTypes.register ()
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
