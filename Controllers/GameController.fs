namespace gametime.Controllers

open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open System.Diagnostics

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging

open gametime.Models

type GameController(logger: ILogger<GameController>) =
    inherit Controller()
        
    member this.Listing(id: int) =
        this.ViewData["GameId"] <- id

        this.View()
