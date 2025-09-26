namespace GameTime

#nowarn "20"

open System
open System.Threading.Tasks
open GameTime.Data
open GameTime.Data.Migrations
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives

open GameTime.Controllers
open GameTime.Services

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateSlimBuilder(args)
        
        let configurationRoot =
            ConfigurationBuilder()
                .AddJsonFile("settings.json", optional = true)
                .AddEnvironmentVariables("GAMETIME_")
                .Build()

        builder.Services.AddScoped<DbContext>()
        builder.Services.AddMemoryCache(fun opt ->
            opt.SizeLimit <-
                (configurationRoot["CacheSizeBytes"]
                    |> Option.ofObj
                    |> Option.map int64
                    |> Option.defaultValue 100_000_000L
                    |> Nullable))
        builder.Services.AddSingleton<GameFetcherService>()
        builder.Services.AddHostedService<GameFetcherService>(_.GetRequiredService<GameFetcherService>())
        builder.Services.AddScoped<HomeController>()
        builder.Services.AddScoped<GameController>()

        let pathBase =
            configurationRoot["PathBase"]
            |> Option.ofObj
            |> Option.defaultValue ""

        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseHsts() |> ignore
        
        app.UseHttpsRedirection()

        app.UseStaticFiles()
        
        app.MapGet("/", Func<HomeController, Task<IResult>>(_.Index(pathBase)))

        app.MapGet(
            "/game/{id}",
            Func<int, GameController, HttpContext, Task<IResult>>(fun id controller context ->
                controller.Listing(id = id, pathBase = pathBase)))
        
        app.MapPost("/game/{id}/refresh", Func<int, GameFetcherService, HttpContext, IResult>(fun id fetcher context ->
            fetcher.EnqueueFetch(id)
            context.Response.Headers.Location = StringValues($"{pathBase}/game/{id}")
            Results.StatusCode(303)))

        using (app.Services.CreateScope()) (fun scope ->
            use db = scope.ServiceProvider.GetRequiredService<DbContext>()
            use conn = db.GetConnection()
            safeInit conn)

        app.Run()

        exitCode
