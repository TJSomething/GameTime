module GameTime.Data.Migrations.Migrate10

open System.Data
open System.Linq

open Dapper
open Dapper.FSharp.SQLite

open GameTime.Data.Entities

let Run (conn: IDbConnection) =
    task {
        let! results =
            select {
                for m in table<Migration> do
                where (m.Id = 10L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create table PlayTimePercentile
                (
                    GameId int not null,
                    PlayerCount int,
                    Percentile int,
                    PlayTime int
                )
                """
                |> conn.ExecuteAsync
                
            let! _ =
                """
                create index percentile_times_by_game
                on PlayTimePercentile (GameId)
                """
                |> conn.ExecuteAsync
                
            let! _ =
                """
                create index percentile_times_by_player_count_percent_time
                on PlayTimePercentile (PlayerCount, Percentile, PlayTime)
                """
                |> conn.ExecuteAsync
            
            let! _ = """alter table Game add column UsersRated int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column AverageRating double null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column BayesAverageRating double null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column StdDevRating double null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column Owned int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column NumWeights int null""" |> conn.ExecuteAsync
            let! _ = """alter table Game add column AverageWeight double null""" |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 10L }
                } |> conn.InsertAsync
            ()

        return ()
    }
