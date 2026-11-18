/// A union read by its compiled structure (a native binary, docs/aot.md) reads
/// as FSharp.Reflection reads it, for every layout the F# compiler produces and
/// every union of the test domain, the koan shop and Kanban's domain: the same
/// cases, tags, fields and privacy, and the same values built and read.
module Plasm.Tests.Shapes

open System
open Microsoft.FSharp.Reflection
open Xunit
open Plasm

module Layouts =
    type One = One of int64
    type OneOfMany = OneOfMany of a: int * b: string
    type OneWithout = OneWithout
    type Two = A2 | B2
    type Three = A3 | B3 | C3
    type Four = A4 | B4 | C4 | D4
    type Many = M1 | M2 | M3 | M4 | M5 | M6 | M7 | M8 | M9
    type Mixed = Empty | Single of int | Pair of int * string
    type Named = Named of first: string * second: int
    type Unnamed = UA of int * string | UB of int

    [<Struct>]
    type StructOne = StructOne of sv: int64

    [<Struct>]
    type StructMany =
        | SA of sa: int
        | SB of sb: string
        | SC

    [<RequireQualifiedAccess>]
    type Qualified =
        | Q1
        | Q2 of int

    type Generic<'T> =
        | GNone
        | GSome of 'T

    type Private = private Private of int

    type Spelled =
        | ``Not started``
        | ``In progress`` of int

    [<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue)>]
    type Nullish =
        | Nothing
        | Something of int

    /// A case named after its union (one named Tags the compiler refuses).
    type Clash =
        | Clash of int
        | Other of string

let layouts =
    [ typeof<Layouts.One>; typeof<Layouts.OneOfMany>; typeof<Layouts.OneWithout>; typeof<Layouts.Two>; typeof<Layouts.Three>
      typeof<Layouts.Four>; typeof<Layouts.Many>; typeof<Layouts.Mixed>; typeof<Layouts.Named>; typeof<Layouts.Unnamed>
      typeof<Layouts.StructOne>; typeof<Layouts.StructMany>; typeof<Layouts.Qualified>; typeof<Layouts.Generic<int>>
      typeof<Layouts.Generic<string>>; typeof<Layouts.Private>; typeof<Layouts.Spelled>; typeof<Layouts.Nullish>
      typeof<Layouts.Clash>; typeof<int option>; typeof<Result<int, string>> ]

/// Every union an assembly defines, closed.
let private unionsOf (a: Reflection.Assembly) =
    a.GetTypes()
    |> Array.filter (fun t -> not t.IsGenericTypeDefinition && FSharpType.IsUnion(t, true) && not (FSharpType.IsUnion(t.BaseType, true)))
    |> List.ofArray

let domains =
    [ typeof<Plasm.TestDomain.Domain.Customer>.Assembly
      typeof<Plasm.Koans.Shop.Order>.Assembly
      typeof<Kanban.Card>.Assembly ]
    |> List.collect unionsOf

/// A value of a field's type to build a case with.
let private sample (t: Type) : obj =
    match t with
    | t when t = typeof<int> -> box 7
    | t when t = typeof<int64> -> box 7L
    | t when t = typeof<string> -> box "s"
    | t when t.IsValueType -> Activator.CreateInstance t
    | _ -> null

let private describe (u: UnionShape) =
    [ for c in u.Cases ->
          let fields = c.Fields |> Array.map (fun (n, t) -> n + ": " + t.FullName)
          $"""{c.Tag} {c.Name}({String.Join(", ", fields)})""" ]

let private same (t: Type) =
    let reflected = Shape.reflectedUnion t
    let structural = Shape.structuralUnion t
    Assert.Equal(reflected.Private, structural.Private)
    Assert.Equal<string list>(describe reflected, describe structural)

    for r, s in Array.zip reflected.Cases structural.Cases do
        let fields = r.Fields |> Array.map (snd >> sample)
        let built = s.Make fields
        Assert.Equal(r.Tag, structural.Tag built)
        Assert.Equal(reflected.Tag built, structural.Tag built)
        Assert.Equal<obj[]>(r.Read(r.Make fields), s.Read built)
        Assert.Equal<obj[]>(r.Read built, s.Read built)

let layoutCases: obj[] seq = layouts |> Seq.map (fun t -> [| box t.FullName; box t |])
let domainCases: obj[] seq = domains |> Seq.map (fun t -> [| box t.FullName; box t |])

[<Theory; MemberData(nameof layoutCases)>]
let ``a union of each compiled layout reads by its structure as FSharp.Reflection reads it`` (_name: string, t: Type) = same t

[<Theory; MemberData(nameof domainCases)>]
let ``every union of the test domain, the koan shop and Kanban reads by its structure as FSharp.Reflection reads it`` (_name: string, t: Type) =
    same t

[<Fact>]
let ``the three domains have unions to read`` () =
    Assert.True(domains.Length >= 10, $"only {domains.Length} unions")

[<Fact>]
let ``a type the structural reader cannot read is an error naming it`` () =
    let e = Assert.ThrowsAny<InvalidOperationException>(fun () -> Shape.structuralUnion typeof<string> |> ignore)
    Assert.Contains("System.String", e.Message)

[<Fact>]
let ``the runtime reads unions through FSharp.Reflection unless told otherwise`` () =
    let expected =
        if Environment.GetEnvironmentVariable "PLASM_SHAPES" = "structure" then
            UnionReader.CompiledStructure
        else
            UnionReader.FSharpReflection

    Assert.Equal(expected, Shape.reader ())
