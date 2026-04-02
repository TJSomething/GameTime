namespace GameTime.Views

open Giraffe.ViewEngine

type BaseView () =
    static member Divider = hr [ _class "divider" ]
    
    static member Render(pathBase: string) (titleText: string) (content: XmlNode list) =
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
                                ul [] [
                                    li [] [ a [ _href $"{pathBase}/" ] [ str "Home" ] ]
                                    li [] [ a [ _href $"{pathBase}/login" ] [ str "Admin" ] ]
                                ] ] ]
                    main [ _class "container" ] content
                    footer
                        [ _class "container" ]
                        [ a
                              [ _href "https://boardgamegeek.com/" ]
                              [ img
                                    [ _src
                                          "https://cf.geekdo-images.com/HZy35cmzmmyV9BarSuk6ug__small/img/gbE7sulIurZE_Tx8EQJXnZSKI6w=/fit-in/200x150/filters:strip_icc()/pic7779581.png" ] ] ] ] ]
