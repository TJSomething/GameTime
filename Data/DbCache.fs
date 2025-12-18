module GameTime.Data.DbCache

open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization

open Dapper.FSharp.SQLite

open GameTime.Data.Entities
        
let DbCacheJsonSerializerOptions =
    JsonFSharpOptions.Default()
        .ToJsonSerializerOptions()

let inline addToCache<'T> (dbContext: DbContext) key version (newItem: 'T)=
    task {
        PreserveRecordFields<'T>
        
        let newItemRow = { Id = key
                           Version = version
                           Value = JsonSerializer.Serialize(newItem, typeof<'T>, DbCacheJsonSerializerOptions) }
        
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
                    Some (JsonSerializer.Deserialize(row.Value, typeof<'T>, DbCacheJsonSerializerOptions))
                with
                | :? JsonException -> None)
            |> Option.bind (fun value ->
                match value with
                | :? 'T as v -> Some v
                | _ -> None)
            
        match foundItem with
        | Some item ->
            return item
        | None ->
            let! newItem = value()
            do! addToCache dbContext key version newItem
            
            return newItem
    }

