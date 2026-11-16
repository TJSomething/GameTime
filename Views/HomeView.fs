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
            recentTopByPlayers: Map<int, Game seq>,
            bggToken: string) =
        let recentTopSection =
            seq {
                let sectioned =
                    recentTopByPlayers
                    |> Map.toList
                    |> List.chunkBySize 2
                    
                for sectionData in sectioned do
                    yield
                        sectionData
                        |> List.map (fun (count, games) ->
                            let playerHeader =
                                if count = 1 then
                                    "1 player"
                                else
                                    $"{count} players"
                            
                            let list = games |> Seq.take 10
                                
                            div [] [
                                h3 [] [ str playerHeader ]
                                ul [] (renderGameList list)
                            ])
                        |> div [ _class "grid" ]
                    
            }
            |> Seq.toList
        
        BaseView.Render
            pathBase
            "GameTime"
            ([
              p [] [ str "Search for a board game:" ]
              input [ _type "search"; _id "search" ]
              ul [ _id "results" ] []
              div [ _class "grid" ] [
                  div [] [
                      h2 [] [ str "Recently loaded games" ]
                      ul [] (renderGameList recentGames)
                  ]
                  div [] [
                      h2 [] [ str "Random most played" ]
                      ul [] (renderGameList randomGames)]]
              h2 [] [ str "Recent most played games" ]
              ] @ recentTopSection @ [
              h2 [] [ str "Statistics" ]
              p [] [ str $"Games loaded: {gameCount:N0}" ]
              script [ _data "token" bggToken; _src $"{pathBase}/js/index.js" ] [] ])
