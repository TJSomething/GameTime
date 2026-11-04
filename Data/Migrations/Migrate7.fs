module GameTime.Data.Migrations.Migrate7

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
                where (m.Id = 7L)
            }
            |> conn.SelectAsync<Migration>

        if results.Count() = 0 then
            let! _ =
                """
                create table AppUser
                (
                    Id text primary key not null,
                    Email text not null,
                    PasswordHash text not null,
                    EmailConfirmed boolean not null,
                    Role text not null,
                    CreatedAt text not null,
                    UpdatedAt text not null
                )
                """
                |> conn.ExecuteAsync
                
            let! _ =
                """
                create index user_by_email
                on AppUser (Email)
                """
                |> conn.ExecuteAsync

            let! _ =
                insert {
                    into table<Migration>
                    value { Id = 7L }
                } |> conn.InsertAsync
            ()

        return ()
    }