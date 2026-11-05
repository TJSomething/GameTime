module GameTime.Data.MigrationRunner

open System.Data

open Dapper
open Dapper.FSharp.SQLite

open GameTime.Data.Migrations

let mutable isAlreadyInitialized = false
        
let safeInit (conn: IDbConnection) =
    task {
        if isAlreadyInitialized |> not then
            conn.Open()
            do! Migrate0.Run conn
            do! Migrate1.Run conn
            do! Migrate2.Run conn
            do! Migrate3.Run conn
            do! Migrate4.Run conn
            do! Migrate5.Run conn
            do! Migrate6.Run conn
            do! Migrate7.Run conn
            do! Migrate8.Run conn
            do! Migrate9.Run conn
            do! Migrate10.Run conn
            let! _ = "PRAGMA foreign_keys = ON;" |> conn.ExecuteAsync
            isAlreadyInitialized <- true
            OptionTypes.register ()
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
