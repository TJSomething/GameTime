namespace GameTime.Views

open GameTime.Data.Entities
open Giraffe.ViewEngine

type LoginView =
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
        
    static member RenderLoginForm
        (
            pathBase: string,
            message: string,
            antiforgeryToken: string
        ) =
        BaseView.Render
            pathBase
            "GameTime"
            ([
                (if message <> "" then
                     [span [_id "message"] [article [_class "card"] [ str message ]]]
                 else
                     [span [_id "message"] []])
                [
                    template [_id "message-template"] [
                        article [_class "card"] []
                    ]
                    h2 [] [ str "Login" ]
                    form [_class "form-group"; _id "login-form"] [
                        label [_class "form-label"; _for "username"] [
                            str "Email"
                        ]
                        input [_class "form-input"; _type "text"; _name "username"; _id "username"]
                        label [_class "form-label"; _for "password"] [
                            str "Password"
                        ]
                        input [_class "form-input"; _type "password"; _name "password"; _id "password"]
                        input [
                            _type "submit"
                            _id "login"
                            _value "Login"
                        ]
                        input [
                            _class "secondary"
                            _type "submit"
                            _id "create-account"
                            _value "Create account"
                        ]
                    ]
                    script [
                        _src $"{pathBase}/js/login.js"
                        _data "csrf-token" antiforgeryToken
                        _data "path-base" pathBase
                    ] []
                ]
            ]
            |> List.concat)

    static member RenderAccount(
        pathBase: string,
        email: string,
        antiforgeryToken: string
    ) =
        BaseView.Render
            pathBase
            "GameTime"
            [
                p [] [ str $"You're logged in! Your email address is {email}." ]
                button [_id "logout"] [ str "Log out" ]
                form [_action $"{pathBase}/report"; _method "post"] [
                    textarea [_name "query"] []
                    input [_type "submit"]
                ]
                script [] [
                    // language=javascript
                    rawText $$"""
(() => {
    const logoutEl = document.getElementById("logout");
    if (!logoutEl) return;
    logoutEl.addEventListener(
        "click",
        () => {
            (async () => {
                await fetch("{{pathBase}}/logout", {
                    method: "post",
                    headers: {
                        "X-XSRF-TOKEN": "{{antiforgeryToken}}"
                    }
                });
                document.location.reload();
            })();
        }
    );
})();
"""
                ]
            ]
            
