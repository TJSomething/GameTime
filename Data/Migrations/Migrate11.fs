module GameTime.Data.Migrations.Migrate11

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
                where (m.Id = 11L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                    create index play_amount_stats_player_month
                    on playAmountStats(PlayerCount, Month, GameId, PlayCount);
                """
                |> conn.ExecuteAsync
                
            let! _ =
                """
                    create index play_amount_stats_game_player_month
                    on playAmountStats(GameId, PlayerCount, Month, PlayCount);
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 11L }
                } |> conn.InsertAsync
            ()

        return ()
    }
