module GameTime.Data.Migrations.Migrate8

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
                where (m.Id = 8L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create table Report
                (
                    Id text primary key not null,
                    CreatedAt text not null,
                    UpdatedAt text not null,
                    Status text not null,
                    Query text not null,
                    Result text null
                )
                """
                |> conn.ExecuteAsync
                
            let! _ =
                """
                create index report_created_at
                on Report (Status, CreatedAt)
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 8L }
                } |> conn.InsertAsync
            ()

        return ()
    }