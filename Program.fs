namespace GameTime

#nowarn "20"

open System
open System.Threading.Tasks
open GameTime.Controllers
open GameTime.DataAccess
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateSlimBuilder(args)

        builder.Services.AddScoped<DbContext>()
        builder.Services.AddScoped<IGameController, GameController>()

        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseExceptionHandler("/Home/Error")
            app.UseHsts() |> ignore
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.

        app.UseHttpsRedirection()

        app.UseStaticFiles()

        app.MapGet("/", Func<IResult>(fun () -> HomeController().Index()))

        app.MapGet("/game/{id}", Func<int, IGameController, Task<IResult>>(fun id controller -> controller.Listing id))

        using (app.Services.CreateScope()) (fun scope ->
            use db = scope.ServiceProvider.GetRequiredService<DbContext>()
            use conn = db.GetConnection()
            safeInit conn)

        app.Run()

        exitCode
