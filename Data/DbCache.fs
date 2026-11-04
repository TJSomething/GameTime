module GameTime.Data.DbCache

open System.Threading.Tasks
open System.Text.Json
open Thoth.Json.Core.Auto
open Thoth.Json.System.Text.Json

open Dapper.FSharp.SQLite

open GameTime.Data.Entities

let extraJsonCoders =
    Extra.empty
    |> Extra.withInt64
    |> Extra.withUInt64
    |> Extra.withDecimal
    |> Extra.withBigInt
        
let inline addToCache<'T> (dbContext: DbContext) key version (newItem: 'T)=
    task {
        let value =
            newItem
            |> Encode.Auto.generateEncoderCached<'T> (extra = extraJsonCoders)
            |> Encode.toString 0
        
        let newItemRow = { Id = key
                           Version = version
                           Value = value }
        
        let! _ =
            insert {
                into dbContext.CacheItem
                value newItemRow
            } |> dbContext.GetConnection().InsertOrReplaceAsync
        ()
    }

let inline getOrCreateFromCache<'T> (dbContext: DbContext) (key: string) (version: int) (value: unit -> Task<'T>) =
    task {
        let conn = dbContext.GetConnection()
        
        let! existingItemResult =
            select {
                for item in dbContext.CacheItem do
                where (item.Id = key && item.Version = version)
            } |> conn.SelectAsync<CacheItem>
        
        let foundItem =
            existingItemResult
            |> Seq.tryHead
            |> Option.bind (fun row ->
                try
                    row.Value
                    |> Decode.fromString (Decode.Auto.generateDecoderCached<'T> (extra = extraJsonCoders))
                    |> Result.toOption
                with
                | :? JsonException -> None)
            
        match foundItem with
        | Some item ->
            return item
        | None ->
            let! newItem = value()
            do! addToCache dbContext key version newItem
            
            return newItem
    }

