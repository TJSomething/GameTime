namespace GameTime

#nowarn "20"

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.HttpResults
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open GameTime.Models

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        builder.Services.AddControllersWithViews().AddRazorRuntimeCompilation()

        builder.Services.AddRazorPages()

        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseExceptionHandler("/Home/Error")
            app.UseHsts() |> ignore
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.

        app.UseHttpsRedirection()

        app.UseStaticFiles()
        app.UseRouting()
        app.UseAuthorization()

        app.MapControllerRoute(
            name = "default",
            pattern = "",
            defaults =
                struct {| controller = "Home"
                          action = "Index" |}
        )

        app.MapControllerRoute(
            name = "game listing",
            pattern = "/game/{id}",
            defaults =
                struct {| controller = "Game"
                          action = "Listing" |}
        )

        app.MapRazorPages()

        let conn = DbModel.GetConnection()
        DbModel.safeInit conn

        app.Run()

        exitCode
