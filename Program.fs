namespace GameTime

#nowarn "20"

open System
open System.Threading.Tasks
open GameTime.Controllers
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open GameTime.Models

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateSlimBuilder(args)
        
        builder.Services.AddSingleton<IGameController, GameController>()
        
        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseExceptionHandler("/Home/Error")
            app.UseHsts() |> ignore
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.

        app.UseHttpsRedirection()

        app.UseStaticFiles()

        app.MapGet(
            "/",
            Func<IResult>(fun () -> HomeController().Index())
        )
        
        app.MapGet("/game/{id}", Func<int, Task<IResult>>(fun id ->
            let controller = app.Services.GetRequiredService<IGameController>()
            controller.Listing id))
        
        let conn = DbModel.GetConnection()
        DbModel.safeInit conn

        app.Run()

        exitCode
