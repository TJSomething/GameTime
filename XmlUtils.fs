module GameTime.XmlUtils

open System
open System.Xml.Linq
open System.Xml.XPath

let (|?>) opt f =
    match opt with
    | Some x ->
        match f x with
        | null -> None
        | fX -> Some fX
    | None -> None

let attrStr path attr (doc: XNode)  =
    doc
    |> Option.ofObj
    |?> _.XPathSelectElement(path)
    |?> _.Attribute(XName.Get(attr))
    |?> _.Value

let attrInt path attr (doc: XNode)  =
    doc
    |> attrStr path attr
    |> Option.bind (fun str ->
        match Int32.TryParse str with
        | true, n -> Some n
        | false, _ -> None)
