/// What a query names inside its quotations, marked once in Plasm
/// ([<Trimming.QuerySurface>], docs/aot.md): the descriptor keeps Plasm
/// whole, so nothing marked can be trimmed, and the native check
/// (tests/Plasm.Aot) is what shows each works natively. This fails when
/// a public member of a marked type is named by no query of the check, unless
/// it is listed below with the reason it is not.
module Plasm.Tests.QuerySurface

open System
open System.Collections
open System.Reflection
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.ExprShape
open Microsoft.FSharp.Reflection
open Xunit
open Plasm

let private plasm = typeof<Repo>.Assembly

let private flags =
    BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

/// A member by type and name, the way a quotation's overloads all count once.
let private key (m: MemberInfo) =
    let t = if m.DeclaringType.IsGenericType then m.DeclaringType.GetGenericTypeDefinition() else m.DeclaringType
    t.FullName.Replace('+', '.') + "." + m.Name

let private surface =
    plasm.GetTypes()
    |> Array.filter (fun t -> t.GetCustomAttributes(typeof<Trimming.QuerySurfaceAttribute>, false).Length > 0)
    |> Array.collect (fun t ->
        let methods =
            t.GetMethods flags
            // `sum$W`: the compiler's variant of an inline function taking its
            // witnesses, which compiled code calls; a quotation names `sum`.
            |> Array.filter (fun m -> not m.IsSpecialName && m.DeclaringType = t && not (m.Name.EndsWith "$W"))
            |> Array.filter (fun m -> not (Array.contains m.Name [| "Equals"; "GetHashCode"; "ToString"; "GetType" |]))
            |> Array.map (fun m -> m :> MemberInfo)
        let properties = t.GetProperties flags |> Array.map (fun p -> p :> MemberInfo)
        Array.append methods properties)
    |> Array.map key
    |> Set.ofArray

/// Marked members the check does not name in a quotation, and why.
let private notNamed =
    Map.ofList
        [ "Plasm.QueryBuilder.Zero",
          "the builder's value for a body that yields nothing; a query always yields its rows, so only a body like `do ()` reaches it, read as an empty query" ]

/// Every Plasm member named in an expression, its quoted arguments and the
/// queries it captures included.
let rec private named (seen: Collections.Generic.HashSet<obj>) (e: Expr) : string seq =
    seq {
        let mine (m: MemberInfo) = if m.DeclaringType.Assembly = plasm then Some(key m) else None

        match e with
        | Patterns.Call(_, mi, _) -> yield! Option.toList (mine mi)
        | Patterns.PropertyGet(_, p, _) -> yield! Option.toList (mine p)
        | Patterns.NewObject(c, _) -> yield! Option.toList (mine c)
        | Patterns.Value(v, _) when not (isNull v) -> yield! valueNamed seen v
        | _ -> ()

        match e with
        | ShapeVar _ -> ()
        | ShapeLambda(_, body) -> yield! named seen body
        | ShapeCombination(_, args) -> for a in args do yield! named seen a
    }

/// Every Plasm member named in a value's expressions: a query's definition,
/// read field by field, so a field added to it is read too.
and private valueNamed (seen: Collections.Generic.HashSet<obj>) (v: obj) : string seq =
    seq {
        if not (isNull v) && not (v :? string) && not (v.GetType().IsPrimitive) && seen.Add v then
            match v with
            | :? Expr as e -> yield! named seen e
            | :? IEnumerable as xs -> for x in xs do yield! valueNamed seen x
            | _ ->
                let t = v.GetType()
                if t.Assembly = plasm || FSharpType.IsRecord(t, true) || FSharpType.IsUnion(t, true) || FSharpType.IsTuple t then
                    for f in t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) do
                        yield! valueNamed seen (f.GetValue v)
    }

/// The query values of the native check, as start-up finds them.
let private checkQueries =
    let aot = typeof<Plasm.Aot.App.Ticket>.Assembly
    let isModule (t: Type) =
        t.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
        |> Seq.exists (fun a -> (a :?> CompilationMappingAttribute).SourceConstructFlags = SourceConstructFlags.Module)

    let statics = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic

    [ for t in aot.GetTypes() do
          if isModule t then
              for p in t.GetProperties statics do
                  match (try p.GetValue null with _ -> null) with
                  | :? ICheckable as q -> yield q
                  | _ -> ()
              // A query the check builds by a function of unit (compiled with no
              // parameters), as one Plasm refuses.
              for m in t.GetMethods statics do
                  if not m.IsSpecialName && m.GetParameters().Length = 0 && typeof<ICheckable>.IsAssignableFrom m.ReturnType then
                      match m.Invoke(null, [||]) with
                      | :? ICheckable as q -> yield q
                      | _ -> () ]

let private namedByCheck =
    let seen = Collections.Generic.HashSet<obj>(HashIdentity.Reference)
    checkQueries
    |> Seq.collect (fun q -> Seq.append (valueNamed seen (box q.Def)) (match q.Select with Some s -> named seen s | None -> Seq.empty))
    |> Set.ofSeq

[<Fact>]
let ``the native check reads every query value`` () = Assert.True(checkQueries.Length >= 16, $"{checkQueries.Length} query values")

[<Fact>]
let ``every member a query names in Plasm is marked as the query surface`` () =
    let unmarked = namedByCheck - surface
    Assert.True(unmarked.IsEmpty, $"""the check's quotations name Plasm members no [<QuerySurface>] type declares: {String.Join(", ", unmarked)}""")

[<Fact>]
let ``every member of the query surface is named natively by the check, or says why not`` () =
    let missing = surface - namedByCheck - (notNamed |> Map.keys |> Set.ofSeq)
    let stale = (notNamed |> Map.keys |> Set.ofSeq) |> Set.filter namedByCheck.Contains
    let unknown = (notNamed |> Map.keys |> Set.ofSeq) - surface
    Assert.True(missing.IsEmpty, $"""query-surface members no query of tests/Plasm.Aot names: {String.Join(", ", missing)}""")
    Assert.True(stale.IsEmpty, $"""listed as not named, but the check names them: {String.Join(", ", stale)}""")
    Assert.True(unknown.IsEmpty, $"""listed but not in the query surface: {String.Join(", ", unknown)}""")
