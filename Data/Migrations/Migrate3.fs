module GameTime.Data.Migrations.Migrate3

open System.Data

open Dapper

let Run (conn: IDbConnection) =
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