namespace GameTime.Middleware

open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

type AntiforgeryFilterFactory() =
    static member Invoke (filterFactoryContext: EndpointFilterFactoryContext) (next: EndpointFilterDelegate) =
        let antiforgery = filterFactoryContext.ApplicationServices.GetRequiredService<IAntiforgery>()
        
        EndpointFilterDelegate(
            fun invocationContext ->
                task {
                    let! isValid = antiforgery.IsRequestValidAsync(invocationContext.HttpContext)
                    let isNotPost = invocationContext.HttpContext.Request.Method.ToUpper() <> "POST"
                    
                    if isValid || isNotPost then
                        return! next.Invoke(invocationContext)
                    else
                        return Results.Problem("CSRF token doesn't match", null, 403)
                } |> ValueTask<obj>)