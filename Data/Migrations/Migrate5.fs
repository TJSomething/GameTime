module GameTime.Data.Migrations.Migrate5

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
                where (m.Id = 5L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create index play_amount_stats_game_id_month
                on PlayAmountStats (GameId, Month)
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 5L }
                } |> conn.InsertAsync
            ()
    }