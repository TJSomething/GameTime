module GameTime.Data.Migrations.Migrate9

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
                where (m.Id = 9L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create index player_count_vote_by_game
                on PlayerCountVote (GameId)
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 9L }
                } |> conn.InsertAsync
            ()

        return ()
    }