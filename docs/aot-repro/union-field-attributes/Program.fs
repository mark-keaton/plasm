/// A native binary (PublishAot) loses the attributes of an F# union case's
/// field properties, and with them FSharp.Reflection's view of the fields and
/// FSharp.Core's decoding of a quotation that reads one. No library involved:
/// an F# union, a record, and a quotation. See README.md.
module Repro

open System
open System.Reflection
open Microsoft.FSharp.Reflection

type Shape =
    | Circle of radius: float
    | Rect of w: float * h: float

type Id = Id of int64

type Rec = { Name: string }

let all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

let say (s: string) = Console.WriteLine s

let attempt (name: string) (f: unit -> string) =
    try
        say (name + ": " + f ())
    with e ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        say (name + ": " + inner(e).GetType().Name + ": " + inner(e).Message)

let property (name: string) (p: PropertyInfo) =
    attempt name (fun () ->
        let g = p.GetGetMethod true
        string (p.GetCustomAttributesData().Count) + " attributes, getter SpecialName " + string g.IsSpecialName)

[<EntryPoint>]
let main _ =
    say ("dynamic code: " + string Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    property "Rect.h" (typeof<Shape>.GetNestedType("Rect", all ||| BindingFlags.Static).GetProperty("h", all))
    property "Id.Item" (typeof<Id>.GetProperty("Item", all))
    property "Rec.Name" (typeof<Rec>.GetProperty("Name", all))
    attempt "FSharpType.GetUnionCases(Shape) fields" (fun () ->
        String.Join(", ", FSharpType.GetUnionCases(typeof<Shape>) |> Array.map (fun c -> c.Name + "/" + string (c.GetFields().Length))))
    attempt "FSharpType.GetRecordFields(Rec)" (fun () -> string (FSharpType.GetRecordFields(typeof<Rec>).Length))
    attempt "a quotation reading a case's field" (fun () ->
        let q = <@ fun (s: Shape) -> match s with Rect(w, _) -> w | _ -> 0.0 @>
        "decoded, " + q.Type.Name)
    0
