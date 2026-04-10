namespace GameTime.Controllers

open System
open System.Text.Json
open System.Threading.Tasks
open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open GameTime.Views
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Giraffe.ViewEngine

type MutList<'T> = System.Collections.Generic.List<'T>

type ReportController(
    dbContext: DbContext,
    config: AppConfig,
    antiforgeryService: IAntiforgery,
    reportManager: ReportManager
) =
    member this.RunReport(context: HttpContext) =
        match context.Request.Form.TryGetValue("query") with
        | true, query when query.Count = 1 ->
            task {
                let id = Guid.CreateVersion7()
                do! reportManager.StartReport(dbContext, id, query)
                
                let url =
                    if config.PathBase = "/" then
                        $"/report/{id}"
                    else
                        $"{config.PathBase}/report/{id}"
                        
                context.Response.Headers.Add("Location", url)
                return Results.StatusCode(303)
            }
        | _ -> Task.FromResult(Results.BadRequest())
        
    member this.WaitForReport(context: HttpContext, id: Guid): Task<IResult> =
        let renderReport (report: Report) =
            let tokens = antiforgeryService.GetAndStoreTokens(context)
            
            let deserializedReportResult =
                try
                    report.Result
                    |> Option.map JsonSerializer.Deserialize<string seq seq>
                    |> Option.defaultValue Seq.empty
                with _ -> Seq.empty
            
            Results.Content(
                statusCode = 200,
                contentType = "text/html",
                content = (
                    ReportView.Render(
                        pathBase = config.PathBase,
                        antiforgeryToken = tokens.RequestToken,
                        antiforgeryFormField = tokens.FormFieldName,
                        lastReportQuery = report.Query,
                        report = deserializedReportResult
                    )
                    |> RenderView.AsString.htmlDocument))
        
        
        task {
            let! report1 = reportManager.GetReport(dbContext, id)
            
            let result1 =
                match report1 with
                | None ->
                    Some (Results.NotFound())
                | Some r when r.Status = "done" || r.Status = "error" ->
                    Some (renderReport r)
                | _ -> None
            
            // result1 is None when the report is still loading.
            // Wait a bit to see if it wraps up before we time out.
            let! _ =
                if result1.IsNone then
                    Task.WhenAny(reportManager.SubscribeToReport(id), Task.Delay(10000))
                else
                    Task.FromResult(Task.CompletedTask)
            
            let! report2 = reportManager.GetReport(dbContext, id)
            
            match report2 with
            | None ->
                return Results.NotFound()
            | Some r when r.Status = "done" || r.Status = "error" ->
                return renderReport r
            | _ ->
                // If we still haven't loaded, despite waiting, redirect again
                let url =
                    if config.PathBase = "/" then
                        $"/report/{id}"
                    else
                        $"{config.PathBase}/report/{id}"
                        
                return Results.LocalRedirect(url, preserveMethod = true)
        }
        
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
