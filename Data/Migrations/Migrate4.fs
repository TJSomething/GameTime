module GameTime.Data.Migrations.Migrate4

open System.Data
open System.Diagnostics.CodeAnalysis
open System.Linq

open Dapper
open Dapper.FSharp.SQLite

open GameTime.Data.Entities

[<DynamicDependency("Id", typedefof<Migration>)>]
let Run (conn: IDbConnection) =
    task {
         let! results =
            select {
                for m in table<Migration> do
                where (m.Id = 4L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create table CacheItem
                (
                    Id text primary key not null,
                    Version int not null,
                    Value text not null
                )
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 4L }
                } |> conn.InsertAsync
            ()
    }