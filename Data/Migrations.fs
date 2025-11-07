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

let migrate3 (conn: IDbConnection) =
    task {
        let! results = """select name from pragma_table_xinfo('Migration')""" |> conn.QueryAsync

        let count = results.AsList().Count

        if count = 0 then
            let! _ =
                """
                create table Migration
                (
                    Id int identity
                        constraint Migration_pk
                            primary key
                )
                """
                |> conn.ExecuteAsync

            let! _ = """alter table Game add column YearPublished int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BoxMinPlayTime int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BoxPlayTime int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BoxMaxPlayTime int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BoxMinPlayers int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BoxMaxPlayers int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column UpdateVersion int null""" |> conn.ExecuteAsync

            let! _ = """alter table Play add column UserId int null""" |> conn.ExecuteAsync

            let! _ =
                """alter table Play add column PlayedGregorianDay int null"""
                |> conn.ExecuteAsync

            let! _ =
                """
                create table PlayerCountVote
                (
                    GameId int not null,
                    PlayerCount int null,
                    Best int not null,
                    Recommended int not null,
                    NotRecommended int not null
                )
                """
                |> conn.ExecuteAsync

            let! _ =
                """
                create table GameRating
                (
                    GameId int not null,
                    Username text not null,
                    Rating int not null
                )
                """
                |> conn.ExecuteAsync

            let! _ =
                """
                create table PlayAmountStats
                (
                    GameId int not null,
                    Month int null,
                    PlayerCount int null,
                    UniquePlayers int not null,
                    MinutesPlayed int not null,
                    PlayCount int not null
                )
                """
                |> conn.ExecuteAsync

            ()

        return ()
    }

let safeInit (conn: IDbConnection) =
    task {
        if isAlreadyInitialized |> not then
            conn.Open()
            do! migrate0 conn
            do! migrate1 conn
            do! migrate2 conn
            do! migrate3 conn
            let! _ = "PRAGMA foreign_keys = ON;" |> conn.ExecuteAsync
            isAlreadyInitialized <- true
            OptionTypes.register ()
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
