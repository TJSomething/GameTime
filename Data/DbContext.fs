namespace GameTime.Data

open System
open System.Data
open Dapper
open Dapper.FSharp.SQLite
open GameTime.Data.Entities
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration

type DbContext() =
    let conf =
        ConfigurationBuilder()
            .AddJsonFile("settings.json", optional = true)
            .AddEnvironmentVariables("GAMETIME_")
            .Build()

    let mutable connection: IDbConnection option = None
    
    member this.Game = table<Game>
    member this.Play = table<Play>
    member this.PlayerCountVote = table<PlayerCountVote>
    member this.PlayAmountStats = table<PlayAmountStats>
    member this.CacheItem = table<CacheItem>

    member this.GetConnection() =
        match connection with
        | Some conn when conn.State = ConnectionState.Open -> conn
        | _ ->
            let connStr = conf["sqliteConnectionString"]
            let newConn = new SqliteConnection(connStr)
            newConn.Open()
            newConn.Execute("PRAGMA journal_mode = WAL;") |> ignore
            connection <- Some newConn
            newConn

    interface IDisposable with
        member this.Dispose() =
            match connection with
            | Some conn when conn.State = ConnectionState.Open ->
                conn.Close()
                conn.Dispose()
            | _ -> ()
