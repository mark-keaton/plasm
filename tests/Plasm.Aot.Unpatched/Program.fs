/// Published without Plasm's step for F# union fields (docs/aot.md): building
/// the model must refuse the typed id, naming it, its assembly and the step.
module Plasm.Aot.Unpatched.Program

open System
open Plasm

type UnpatchedId = UnpatchedId of int64

[<Table "unpatched">]
type Unpatched = { Id: UnpatchedId; Name: string }

[<EntryPoint>]
let main _ =
    try
        Model.buildOrFail [] [ typeof<Unpatched> ] |> ignore
        Console.WriteLine "model: built"
        0
    with ex ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        Console.WriteLine("model: refused: " + inner(ex).Message)
        1
