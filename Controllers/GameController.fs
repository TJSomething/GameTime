namespace gametime.Controllers

open System
open Dapper.FSharp.SQLite

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

open gametime.FetchGame
open gametime.Models.DbModel

type GameController(
    logger: ILogger<GameController>,
    serviceProvider: IServiceProvider
) =
    inherit Controller()
    
    let startFetch (id: int) =
        task {
            use _ = serviceProvider.CreateAsyncScope()
            
            do! startFetchGameTask (GetConnection()) id
            
            ()
        }
        
    member this.Listing(id: int) =
        task {
            let conn = GetConnection ()
            
            let! gameResult =
                select {
                    for g in gameTable do
                    where (g.Id = id)
                }
                |> conn.SelectAsync<Game>
            let game = Seq.tryHead gameResult
            
            let! plays =
                task {
                    match game with
                    | None ->
                        // Start in the background
                        startFetch id
                        return List.empty
                    | Some _ ->
                        let! ps =
                            select {
                                for p in playTable do
                                where (p.GameId = id)
                            } |> conn.SelectAsync<Play>
                            
                        return Seq.toList ps
                }
            
            let count = List.length plays
            let average =
                if count > 0 then
                    plays
                    |> List.averageBy (fun p -> p.Length |> float)
                else
                    0.0
            
            let title =
                match game with
                | Some g ->
                    match g.Title with
                    | Some t -> t
                    | None -> $"Loading game #{id} ..."
                | None -> $"Loading game #{id} ..."
                
            this.ViewData["Status"] <-
                match game with
                | Some g ->
                    match g.UpdateFinishedAt with
                    | Some t -> "Loaded"
                    | None -> "Loading"
                | None -> "Initial"
            this.ViewData["GameId"] <- id
            this.ViewData["Title"] <- title
            this.ViewData["PlayCount"] <- count
            this.ViewData["Total"] <- 
                match game with
                | Some g -> g.TotalPlays
                | None -> 0
            this.ViewData["LengthAverage"] <- average

            return this.View()
        }
