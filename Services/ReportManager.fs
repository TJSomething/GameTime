namespace GameTime.Services

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Dapper.FSharp.SQLite
open GameTime.Data
open GameTime.Data.Entities

type ReportManager() =
    let jobQueue: Guid Channel = Channel.CreateUnbounded()
    let subscriptions = Dictionary<Guid, TaskCompletionSource>()
    
    member this.WaitToReadJob (token: CancellationToken) =
        jobQueue.Reader.WaitToReadAsync(token)
        
    member this.FulfillSubscription (id: Guid) =
        match subscriptions.TryGetValue(id) with
        | true, taskSource ->
            taskSource.SetResult()
            subscriptions.Remove(id) |> ignore
            true
        | false, _ ->
            false
            
    member this.StartReport(dbContext: DbContext, id: Guid, query: string) =
        task {
            let now = DateTime.UtcNow
            
            let _ =
                insert {
                    into dbContext.Report
                    value {
                        Id = id
                        CreatedAt = now
                        UpdatedAt = now
                        Status = "new"
                        Query = query
                        Result = None }
                } |> dbContext.GetConnection().InsertAsync
            
            let _ = jobQueue.Writer.TryWrite(id)
            
            let subscriptionSource = TaskCompletionSource()
            let _ = subscriptions.Add(id, subscriptionSource)
            
            return ()
        }
    
    member this.SubscribeToReport(id: Guid) =
        match subscriptions.TryGetValue(id) with
        | true, taskSource ->
            taskSource.Task
        | false, _ ->
            Task.CompletedTask
    
    member this.GetReport(dbContext: DbContext, id: Guid) =
        task {
            use conn = dbContext.GetConnection()
            
            let! result =
                select {
                    for r in dbContext.Report do
                        where (r.Id = id)
                } |> conn.SelectAsync<Report>
            
            return result |> Seq.tryHead
        }
