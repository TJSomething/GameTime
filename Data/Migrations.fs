module GameTime.Data.Migrations

open System.Data

open Dapper
open Dapper.FSharp.SQLite

let mutable isAlreadyInitialized = false

let migrate0 (conn: IDbConnection) =
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

let migrate1 (conn: IDbConnection) =
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

let migrate2 (conn: IDbConnection) =
    task {
        let! results =
            """select name from pragma_table_xinfo('Play') where name = 'FetchedAt'"""
            |> conn.QueryAsync

        let count = results.AsList().Count

        if count > 0 then
            let! _ = """alter table Play drop column FetchedAt""" |> conn.ExecuteAsync
            ()

        return ()
    }

let safeInit (conn: IDbConnection) =
    task {
        if isAlreadyInitialized |> not then
            conn.Open()
            let! _ = "PRAGMA foreign_keys = ON;" |> conn.ExecuteAsync
            do! migrate0 conn
            do! migrate1 conn
            do! migrate2 conn
            isAlreadyInitialized <- true
            OptionTypes.register ()
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously

