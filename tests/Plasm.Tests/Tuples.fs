/// A native binary makes a tuple a query selects by setting its fields
/// (docs/aot.md), whose names are the framework's own: this builds every arity
/// that way under the runtime, with a value type in every position, and
/// requires the value F# builds.
module Plasm.Tests.Tuples

open System
open Microsoft.FSharp.Reflection
open Xunit
open Plasm

let private items (types: Type[]) = types |> Array.mapi (fun i t -> if t = typeof<int> then box (i + 1) else box (string (i + 1)))

let private same (t: Type) =
    let values = items (if FSharpType.IsTuple t then FSharpType.GetTupleElements t else [||])
    let expected = FSharpValue.MakeTuple(values, t)
    Assert.Equal(expected, Shape.tupleByFields t values)

let arities: obj[] seq =
    [ for n in 1..15 do
          for kind in [ "ints"; "mixed" ] do
              yield [| box n; box kind |] ]

let private elements n kind =
    Array.init n (fun i -> if kind = "ints" || i % 2 = 0 then typeof<int> else typeof<string>)

[<Theory; MemberData(nameof arities)>]
let ``a tuple made by its fields is the one F# makes`` (n: int, kind: string) = same (FSharpType.MakeTupleType(elements n kind))

[<Theory; MemberData(nameof arities)>]
let ``a struct tuple made by its fields is the one F# makes`` (n: int, kind: string) = same (FSharpType.MakeStructTupleType(elements n kind))
