module GameTime.ViewFns

open System
open GameTime.Data.Entities
open Giraffe.ViewEngine
open Giraffe.ViewEngine.Accessibility
open Humanizer

// Components
let divider = hr [ _class "divider" ]

// Template
let master (pathBase: string) (titleText: string) (content: XmlNode list) =
    html
        [ _lang "en" ]
        [ head
              []
              [ meta [ _charset "utf-8" ]
                meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
                meta [ _name "color-schema"; _content "light dark" ]
                title [] [ str titleText ]
                link [ _rel "stylesheet"; _href $"{pathBase}/css/pico.indigo.min.css" ] ]
          body
              []
              [ header
                    [ _class "container" ]
                    [ nav
                          []
                          [ ul [] [ li [] [ strong [] [ str "GameTime" ] ] ]
                            ul [] [ li [] [ a [ _href $"{pathBase}/" ] [ str "Home" ] ] ] ] ]
                main [ _class "container" ] content
                footer
                    [ _class "container" ]
                    [ a
                          [ _href "https://boardgamegeek.com/" ]
                          [ img
                                [ _src
                                      "https://cf.geekdo-images.com/HZy35cmzmmyV9BarSuk6ug__small/img/gbE7sulIurZE_Tx8EQJXnZSKI6w=/fit-in/200x150/filters:strip_icc()/pic7779581.png" ] ] ] ] ]

// Views
let homeView (pathBase: string) (gameCount: int64) (playCount: int64) (recentGames: Game seq) (bggToken: string) =
    master
        pathBase
        "GameTime"
        [ p [] [ str "Search for a board game:" ]
          input [ _type "search"; _id "search" ]
          ul [ _id "results" ] []
          h2 [] [ str "Recently loaded games" ]
          ul
              []
              (recentGames
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
               |> Seq.toList)
          h2 [] [ str "Stats" ]
          p [] [ str $"Games loaded: {gameCount}" ]
          p [] [ str $"Plays loaded: {playCount}" ]
          script [ _data "token" bggToken; _src $"{pathBase}/js/index.js" ] [] ]

let renderTable (cells: string seq seq) =
    [ table
          [ _class "striped" ]
          [ thead
                []
                [ tr
                      []
                      (seq {
                          for header in Seq.head cells do
                              yield (th [] [ str header ])
                       }
                       |> Seq.toList) ]
            tbody
                []
                (seq {
                    for row in Seq.tail cells do
                        yield
                            tr
                                []
                                (seq {
                                    for cell in row do
                                        yield td [] [ str cell ]
                                 }
                                 |> Seq.toList)
                 }
                 |> Seq.toList) ] ]

type Listing =
    static member Render
        (
            id: int,
            pathBase: string,
            status: string,
            title: string,
            year: int option,
            minPlayers: int option,
            maxPlayers: int option,
            playCount: int,
            totalPlays: int,
            averagePlayTime: float,
            percentileTable: string seq seq,
            monthlyPlayTable: string seq seq,
            timeLeft: TimeSpan option,
            otherGamesAheadOfThisOne: int option
        ) =
        let statusBody =
            (match status with
             | "Loaded" ->
                 [ h1
                       []
                       [ match year with
                         | Some y when y <> 0 -> str $"{title} ({y})"
                         | _ -> str title ] ]
                 @ (match minPlayers, maxPlayers with
                    | Some min, Some max ->
                        let countStr =
                            match min, max with
                            | 1, 1 -> "1 player"
                            | x, y when x = y -> $"{x} players"
                            | x, y -> $"{x} to {y} players"

                        [ p [] [ str $"Official player count: {countStr}" ] ]
                    | _ -> [])
                   @ [ p [] [ a [ _href $"https://boardgamegeek.com/boardgame/{id}/" ] [ str "BGG page" ] ]
                       p [] [ str $"Plays: {playCount}" ]
                       p [] [ str $"Average play time: %.0f{averagePlayTime}" ]
                       h2 [] [ str "Percentiles for play time (minutes)" ]
                       div [ _class "overflow-auto" ] (renderTable percentileTable)
                       h2 [] [ str "Plays per month" ]
                       div [ _class "overflow-auto" ] (renderTable monthlyPlayTable) ]
             | "Loading" ->
                 List.concat
                     [ [ h1 [] [ str title ] ]
                       (if totalPlays = 0 then
                            [ p [] [ str "Waiting for game to start loading plays..." ] ]
                        else
                            [ p [] [ str $"Loading plays: %d{playCount} / %d{totalPlays}" ] ])
                       (match otherGamesAheadOfThisOne with
                        | Some 1 -> [ p [] [ str "There is 1 game ahead of this one." ] ]
                        | None
                        | Some 0 -> []
                        | Some count -> [ p [] [ str $"There are {count} games ahead of this one." ] ])
                       (match timeLeft with
                        | Some t -> [ p [] [ str $"Time left: {t.Humanize(precision = 2)}" ] ]
                        | None -> [])
                       [ progress [ _value $"{playCount}"; _max $"{totalPlays}" ] [] ]
                       [ script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ] ]
             | "Initial" ->
                 [ p [] [ str "Loading..." ]
                   article [ _ariaBusy "true" ] []
                   script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ]
             | _ -> [])

        // Em dash
        master pathBase $"{title} \u2014 GameTime" statusBody
