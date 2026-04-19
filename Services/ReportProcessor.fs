namespace GameTime.Services

open System
open System.Collections.Generic
open System.Linq
open System.Text.Json
open System.Threading
open Microsoft.Extensions.DependencyInjection
open System.Threading.Tasks
open Dapper.FSharp.SQLite
open GameTime.Data
open GameTime.Data.Entities
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type MutList<'T> = List<'T>

type ReportProcessor(logger: ILogger<ReportProcessor>, serviceProvider: IServiceProvider, reportManager: ReportManager) =
    inherit BackgroundService()
    
    let lockJob (dbContext: DbContext) =
        task {
            let connection = dbContext.GetConnection()
            use txn = connection.BeginTransaction()
            
            let! jobResult =
                let q =
                    select {
                        for r in dbContext.Report do
                            where (r.Status = "new")
                            orderBy r.CreatedAt
                            take 0 1
                    }
                
                connection.SelectAsync<Report>(q, txn)
            
            let jobOpt = jobResult |> Seq.tryHead
            
            let! jobsLockedCount =
                match jobOpt with
                | None ->
                    Task.FromResult(0)
                | Some job ->
                    let q =
                        update {
                            for r in dbContext.Report do
                                setColumn r.Status "pending"
                                where (r.Id = job.Id && r.Status = "new")
                        }
                    
                    connection.UpdateAsync(q, txn)
            
            if jobsLockedCount > 0 then
                txn.Commit()
                return jobOpt
            else
                txn.Rollback()
                return None
        }
    
    let runJob (dbContext: DbContext) (query: string) (cancellationToken: CancellationToken) =
        task {
            let connection = dbContext.GetConnection()
            let command = connection.CreateCommand()
            command.CommandText <- query
                
            let rows = MutList<MutList<string>>()
            
            try
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                
                let mutable hasResult = reader.Read()
                
                let header = MutList<string>()
                for i in 0 .. (reader.FieldCount - 1) do
                     header.Add(reader.GetName(i))
                rows.Add(header)
                        
                while hasResult do
                    let row = Array.create<obj> reader.FieldCount null
                    reader.GetValues(row) |> ignore
                    
                    rows.Add(MutList(row |> Seq.ofArray |> Seq.map _.ToString()))
                    
                    hasResult <- reader.Read()
            with
            | :? SqliteException as e ->
                rows.Add(MutList(["SQLite Error"]))
                rows.Add(MutList([e.Message]))
            | :? TaskCanceledException
            | :? OperationCanceledException ->
                rows.Add(MutList(["Error"]))
                rows.Add(MutList(["Timeout"]))
            
            return rows.AsEnumerable() |> Seq.map _.AsEnumerable()
        }
    
    override this.ExecuteAsync(cancellationToken: CancellationToken) =
        task {
            let mutable lastJob = None
            
            while (not cancellationToken.IsCancellationRequested) do
                use scope = serviceProvider.CreateAsyncScope()
                use dbContext = scope.ServiceProvider.GetRequiredService<DbContext>()

                try
                    let! hasJob = reportManager.WaitToReadJob(cancellationToken)
                    
                    use conn = dbContext.GetConnection()
                            
                    let! jobOpt =
                        if hasJob then
                            lockJob dbContext
                        else
                            Task.FromResult(None)
                    
                    lastJob <- jobOpt
                        
                    let! resultOpt =
                        match jobOpt with
                        | Some job ->
                            task {
                                let! r = runJob dbContext job.Query cancellationToken
                                return Some r
                            }
                        | None ->
                            Task.FromResult(None)
                            
                    match jobOpt, resultOpt with
                    | Some job, Some result ->
                        let serialized = JsonSerializer.Serialize(result)
                        
                        let! _ =
                            update {
                                for r in dbContext.Report do
                                    setColumn r.UpdatedAt DateTime.UtcNow
                                    setColumn r.Result (Some serialized)
                                    setColumn r.Status "done"
                                    where (r.Id = job.Id)
                            } |> conn.UpdateAsync
                            
                        reportManager.FulfillSubscription(job.Id) |> ignore
                    | Some job, None ->
                        let! _ =
                            update {
                                for r in dbContext.Report do
                                    setColumn r.UpdatedAt DateTime.UtcNow
                                    setColumn r.Status "error"
                                    where (r.Id = job.Id)
                            } |> conn.UpdateAsync
                            
                        reportManager.FulfillSubscription(job.Id) |> ignore
                    | None, _ ->
                        ()
                    
                    lastJob <- None
                with ex ->
                    let currentTaskDescription =
                        lastJob
                        |> Option.map (fun j -> $"handling report {j.Id.ToString()}")
                        |> Option.defaultValue "with ReportProcessor without report in context"
                        
                    logger.LogError(ex, $"Error {currentTaskDescription}")
        }
        
