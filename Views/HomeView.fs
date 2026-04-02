namespace GameTime.Views

open GameTime.Data.Entities
open Giraffe.ViewEngine

type HomeView =
    static let renderGameList (games: Game seq) =
        games
        |> Seq.map (fun g ->
            li
                []
                [ a
                      [ _href $"game/{g.Id}" ]
                      [ match g.Title, g.YearPublished with
                        | Some t, Some 0
                        | Some t, None -> str t
                        | Some t, Some y -> str $"{t} ({y})"
                        | None, _ -> str $"Game #{g.Id}" ] ])
        |> Seq.toList
        
    static member Render
        (
            pathBase: string,
            gameCount: int64,
            recentGames: Game seq,
            randomGames: Game seq,
            bggToken: string) =
        BaseView.Render
            pathBase
            "GameTime"
            [ p [] [ str "Search for a board game:" ]
              input [ _type "search"; _id "search" ]
              ul [ _id "results" ] []
              h2 [] [ str "Recently loaded games" ]
              ul [] (renderGameList recentGames)
              h2 [] [ str "Random most played" ]
              ul [] (renderGameList randomGames)
              h2 [] [ str "Statistics" ]
              p [] [ str $"Games loaded: {gameCount}" ]
              script [ _data "token" bggToken; _src $"{pathBase}/js/index.js" ] [] ]
