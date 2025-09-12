module GameTime.ViewFns

open System
open Giraffe.ViewEngine
open Giraffe.ViewEngine.Accessibility

// Components
let divider = hr [ _class "divider" ]

// Template
let master (titleText: string) (content: XmlNode list) =
    html
        [ _lang "en" ]
        [ head
              []
              [ meta [ _charset "utf-8" ]
                title [] [ str titleText ]
                link [ _rel "stylesheet"; _href "/css/pico.indigo.min.css" ] ]
          body [] [ main [ _class "container" ] content ] ]

// Views
let homeView =
    master
        "GameTime"
        [ h1 [] [ str "GameTime" ]
          divider
          p [] [ str "Search for a board game:" ]
          input [ _type "search"; _id "search" ]
          ul [ _id "results" ] []
          script [ _src "/js/index.js" ] [] ]

type Listing =
    static member Render
        (
            id: int,
            status: string,
            title: string,
            playCount: int,
            totalPlays: int,
            averagePlayTime: float,
            percentileTable: string list list,
            eta: DateTime option,
            otherGamesAheadOfThisOne: int option
        ) =
        let statusBody =
            (match status with
             | "Loaded" ->
                 [ h1 [] [ str title ]
                   p [] [ str $"Plays: {playCount}" ]
                   p [] [ str $"Average play time: %.0f{averagePlayTime}" ]
                   h2 [] [ str "Percentiles for play time (minutes)" ]
                   div
                       [ _class "overflow-auto" ]
                       [ table
                             [ _class "striped" ]
                             [ thead
                                   []
                                   [ tr
                                         []
                                         (seq {
                                             for header in Seq.head percentileTable do
                                                 yield (th [] [ str header ])
                                          }
                                          |> Seq.toList) ]
                               tbody
                                   []
                                   (seq {
                                       for row in Seq.tail percentileTable do
                                           yield
                                               tr
                                                   []
                                                   (seq {
                                                       for cell in row do
                                                           yield td [] [ str cell ]
                                                    }
                                                    |> Seq.toList)
                                    }
                                    |> Seq.toList) ] ] ]
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
                       (match eta with
                        | Some t -> [ p [] [ str $"ETA: {t}" ] ]
                        | None -> [])
                       [ article [ _ariaBusy "true" ] [] ]
                       [ script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ] ]
             | "Initial" ->
                 [ p [] [ str "Loading..." ]
                   article [ _ariaBusy "true" ] []
                   script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ]
             | _ -> [])

        // Em dash
        master $"{title} \u2014 GameTime" statusBody
