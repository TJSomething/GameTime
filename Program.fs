namespace GameTime

#nowarn "20"

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives

open GameTime.Controllers
open GameTime.DataAccess
open GameTime.Services

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateSlimBuilder(args)

        builder.Services.AddScoped<DbContext>()
        builder.Services.AddSingleton<GameFetcherService>()
        builder.Services.AddHostedService<GameFetcherService>(_.GetRequiredService<GameFetcherService>())
        builder.Services.AddScoped<GameController>()

        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseExceptionHandler("/Home/Error")
            app.UseHsts() |> ignore
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.

        app.UseHttpsRedirection()

        app.UseStaticFiles()

        app.MapGet("/", Func<IResult>(fun () -> HomeController().Index()))

        app.MapGet("/game/{id}", Func<int, GameController, Task<IResult>>(fun id controller -> controller.Listing id))
        
        app.MapPost("/game/{id}/refresh", Func<int, GameFetcherService, HttpContext, IResult>(fun id fetcher context ->
            fetcher.EnqueueFetch(id)
            context.Response.Headers.Location = StringValues($"/game/{id}")
            Results.StatusCode(303)))

        using (app.Services.CreateScope()) (fun scope ->
            use db = scope.ServiceProvider.GetRequiredService<DbContext>()
            use conn = db.GetConnection()
            safeInit conn)

        app.Run()

        exitCode
