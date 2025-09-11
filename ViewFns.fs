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
    master "Homepage" [ h1 [] [ str "Homepage" ]; divider; p [] [ str "Welcome!" ] ]

type Listing =
    static member View
        (
            id: int,
            status: string,
            title: string,
            playCount: int,
            totalPlays: int,
            averagePlayTime: float,
            percentileTable: string,
            eta: DateTime option
        ) =
        let statusBody =
            (match status with
             | "Loaded" ->
                 [ h1 [] [ str title ]
                   p [] [ str $"Plays: {playCount}" ]
                   p [] [ str $"Average play time: %f{averagePlayTime}" ]
                   pre [] [ str percentileTable ] ]
             | "Loading" ->
                 List.concat
                     [ [ h1 [] [ str title ]; p [] [ str $"Plays: %d{playCount} / %d{totalPlays}" ] ]
                       (match eta with
                        | Some t -> [ p [] [ str $"ETA: {t}" ]; article [ _ariaBusy "true" ] [] ]
                        | None -> [])
                       [ script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ] ]
             | "Initial" ->
                 [ p [] [ str "Loading..." ]
                   article [ _ariaBusy "true" ] []
                   script [] [ rawText "setTimeout(() => location.reload(), 10000);" ] ]
             | _ -> [])

        let forceRefresh =
            [ form
                  [ _method "POST"; _action $"/game/{id}/refresh" ]
                  [ button [ _type "submit"; _class "outline" ] [ str "Force refresh" ] ] ]

        master title (statusBody @ forceRefresh)
