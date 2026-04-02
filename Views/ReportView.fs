namespace GameTime.Views

open System.Linq
open Giraffe.ViewEngine

type ReportView() =
    static member Render(
        pathBase: string,
        antiforgeryFormField: string,
        antiforgeryToken: string,
        lastReportQuery: string,
        report: string seq seq
    ) =
        BaseView.Render
            pathBase
            "Reports \u2014 GameTime"
            [
                form [_action $"{pathBase}/report"; _method "post"] [
                    textarea [_name "query"] [ str lastReportQuery ]
                    input [_type "hidden"; _name antiforgeryFormField; _value antiforgeryToken]
                    input [_type "submit"]
                ]
                if report.Count() > 0 then
                    table
                        []
                        (report
                        |> List.ofSeq
                        |> List.mapi (fun index row ->
                            let el = if index = 0 then th else td
                            let cells =
                                row
                                |> Seq.map (fun s -> el [] [ str s ])
                                |> List.ofSeq
                            
                            tr [] cells))
            ]
