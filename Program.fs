namespace GameTime

#nowarn "20"

open System
open System.Threading.Tasks
open GameTime.Data
open GameTime.Data.Migrations
open GameTime.Services.Identity
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives

open GameTime.Controllers
open GameTime.Services

type MutList<'T> = System.Collections.Generic.List<'T>

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

        builder.Services.AddSingleton<AppConfig>(fun _ ->
            let BggFrontendToken = configurationRoot.GetValue<string>("BggFrontendToken")
            let BggBackendToken = configurationRoot.GetValue<string>("BggBackendToken")
            let PathBase =
                Option.ofObj(configurationRoot.GetValue<string>("PathBase"))
                |> Option.defaultValue ""
            ArgumentNullException.ThrowIfNullOrWhiteSpace(BggFrontendToken, "BggFrontendToken")
            ArgumentNullException.ThrowIfNullOrWhiteSpace(BggFrontendToken, "BggBackendToken")
            {
                PathBase = PathBase
                BggFrontendToken = BggFrontendToken
                BggBackendToken = BggBackendToken
            })
        
        builder.Services.AddScoped<DbContext>()
        
        builder.Services.AddTransient<IEmailSender<AppUser>, FakeEmailSender>()
        
        builder.Services.AddIdentity<AppUser, string>(fun options ->
            options.SignIn.RequireConfirmedEmail <- true
            options.Password.RequireDigit <- false
            options.Password.RequireLowercase <- false
            options.Password.RequireNonAlphanumeric <- false
            options.Password.RequireUppercase <- false
            options.Password.RequiredLength <- 12)
            .AddDefaultTokenProviders()
            
        builder.Services.AddTransient<IUserStore<AppUser>, AppUserStore>()
        builder.Services.AddTransient<IRoleStore<string>, AppRoleStore>()
        
        builder.Services.AddDistributedMemoryCache()
        
        builder.Services.AddAuthorization()

        builder.Services.AddSession(fun options ->
            options.IdleTimeout <- TimeSpan.FromSeconds(3600L)
            options.Cookie.HttpOnly <- true
            options.Cookie.IsEssential <- true)
        
        builder.Services.AddAntiforgery(fun options -> options.HeaderName <- "X-XSRF-TOKEN")
        
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
        builder.Services.AddScoped<LoginController>()
        
        let app = builder.Build()

        if not (builder.Environment.IsDevelopment()) then
            app.UseHsts() |> ignore
        
        app.UseHttpsRedirection()

        app.UseStaticFiles()
        
        app.UseAntiforgery()
        
        app.MapGet("/", Func<HomeController, Task<IResult>>(_.Index()))

        app.MapGet(
            "/game/{id}",
            Func<int, GameController, HttpContext, Task<IResult>>(fun id controller context ->
                controller.Listing(id = id)))
        
        app.MapPost("/game/{id}/refresh", Func<int, GameFetcherService, AppConfig, HttpContext, IResult>(fun id fetcher config context ->
            fetcher.EnqueueFetch(id)
            context.Response.Headers.Location = StringValues($"{config.PathBase}/game/{id}")
            Results.StatusCode(303)))
        
        app.MapGet("/login", Func<LoginController, HttpContext, IResult>(_.Form))
       
        app.MapPost("/report", Func<DbContext, HttpContext, Task<IResult>>(fun db context ->
                match context.Request.Form.TryGetValue("query") with
                | true, query when query.Count = 1 ->
                    task {
                        let command = db.GetConnection().CreateCommand()
                        command.CommandText <- query
                        
                        use reader = command.ExecuteReader()
                        
                        let mutable hasResult = reader.Read()
                        
                        let rows = MutList<MutList<string>>()
                        
                        let header = MutList<string>()
                        for i in 0 .. (reader.FieldCount - 1) do
                             header.Add(reader.GetName(i))
                        rows.Add(header)
                                
                        while hasResult do
                            let row = Array.create<obj> reader.FieldCount null
                            reader.GetValues(row)
                            
                            rows.Add(MutList(row |> Seq.ofArray |> Seq.map _.ToString()))
                            
                            hasResult <- reader.Read()
                        
                        return Results.Json(rows)
                    }        
                | _ -> Task.FromResult(Results.BadRequest())
            ))
            .RequireAuthorization()
        
        app.MapPost("/logout", Func<SignInManager<AppUser>, HttpContext, AppConfig, Task<IResult>>(fun signInManager context config ->
            task {
                do! signInManager.SignOutAsync()
                context.Response.Headers.Location = StringValues($"{config.PathBase}/login?logout=true")
                return Results.StatusCode(303)   
            }))
        
        app.MapIdentityApi<AppUser>()
            .AddEndpointFilterFactory(fun filterFactoryContext next ->
                let antiforgery = filterFactoryContext.ApplicationServices.GetRequiredService<IAntiforgery>()
                
                EndpointFilterDelegate(
                    fun invocationContext ->
                        task {
                            let! isValid = antiforgery.IsRequestValidAsync(invocationContext.HttpContext)
                            
                            if isValid then
                                return! next.Invoke(invocationContext)
                            else
                                return Results.Problem("CSRF token doesn't match", null, 403)
                        } |> ValueTask<obj>))
            
        using (app.Services.CreateScope()) (fun scope ->
            use db = scope.ServiceProvider.GetRequiredService<DbContext>()
            use conn = db.GetConnection()
            safeInit conn)

        app.Run()

        exitCode
