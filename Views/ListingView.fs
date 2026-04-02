namespace GameTime.Views

open System
open Giraffe.ViewEngine
open Giraffe.ViewEngine.Accessibility
open Humanizer

type ListingView =
    static let renderTable (cells: string seq seq) =
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
        
    static member Render
        (
            id: int,
            pathBase: string,
            status: string,
            title: string,
            year: int option,
            minPlayers: int option,
            maxPlayers: int option,
            updatedAt: string,
            playCount: int,
            totalPlays: int,
            averagePlayTime: float,
            percentileTable: string seq seq,
            monthlyPlayTable: string seq seq,
            playerCountRatingTable: string seq seq,
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
                       h2 [] [ str "Player counts" ]
                       div [ _class "overflow-auto" ] (renderTable playerCountRatingTable)
                       h2 [] [ str "Percentiles for play time (minutes)" ]
                       div [ _class "overflow-auto" ] (renderTable percentileTable)
                       h2 [] [ str "Plays per month" ]
                       div [ _class "overflow-auto" ] (renderTable monthlyPlayTable)
                       p [] [ str $"Updated at: {updatedAt}" ] ]
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
        BaseView.Render pathBase $"{title} \u2014 GameTime" statusBody
