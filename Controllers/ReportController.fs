namespace GameTime.Controllers

open System.Linq
open System.Threading.Tasks
open GameTime.Data
open GameTime.Services
open GameTime.Views
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Giraffe.ViewEngine
open Microsoft.Data.Sqlite

type MutList<'T> = System.Collections.Generic.List<'T>

type ReportController(
    dbContext: DbContext,
    config: AppConfig,
    antiforgeryService: IAntiforgery
) =
    member this.RunReport(context: HttpContext) =
        let tokens = antiforgeryService.GetAndStoreTokens(context)
        
        match context.Request.Form.TryGetValue("query") with
        | true, query when query.Count = 1 ->
            task {
                let command = dbContext.GetConnection().CreateCommand()
                command.CommandText <- query
                    
                let rows = MutList<MutList<string>>()
                
                try
                    use reader = command.ExecuteReader()
                    
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
                
                let seqRows = rows.AsEnumerable() |> Seq.map _.AsEnumerable()
                    
                return
                    Results.Content(
                        statusCode = 200,
                        contentType = "text/html",
                        content = (
                            ReportView.Render(
                                pathBase = config.PathBase,
                                antiforgeryToken = tokens.RequestToken,
                                antiforgeryFormField = tokens.FormFieldName,
                                lastReportQuery = query,
                                report = seqRows)
                            |> RenderView.AsString.htmlDocument)
                    )
            }        
        | _ -> Task.FromResult(Results.BadRequest())
        
    member this.ReportForm(context: HttpContext) =
        let tokens = antiforgeryService.GetAndStoreTokens(context)
        
        Results.Content(
            statusCode = 200,
            contentType = "text/html",
            content = (
                ReportView.Render(
                    pathBase = config.PathBase,
                    antiforgeryToken = tokens.RequestToken,
                    antiforgeryFormField = tokens.FormFieldName,
                    lastReportQuery = "",
                    report = Seq.empty
                )
                |> RenderView.AsString.htmlDocument)
        )
