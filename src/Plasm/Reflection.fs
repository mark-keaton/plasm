/// Cached reflection helpers for constructing immutable F# values.
module internal Plasm.Reflection

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Reflection

let private cache (f: Type -> 'V) =
    let d = ConcurrentDictionary<Type, 'V>()
    fun (t: Type) -> d.GetOrAdd(t, f)

let isOption (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

let isList (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>


let elementType (t: Type) = t.GetGenericArguments().[0]

/// Some(value) for a given option type.
let makeSome (optType: Type) : obj -> obj = (Shape.option (elementType optType)).Some

/// Reads the payload of an option value: None (null) -> None.
let getOptionValue (optType: Type) : obj -> obj option = (Shape.option (elementType optType)).Value

/// Builds an F# list of the given element type from boxed items.
let makeList (elemType: Type) : obj seq -> obj = (Shape.list elemType).Make

let listItems (elemType: Type) : obj -> obj seq = (Shape.list elemType).Items

/// How an owned or linked field holds what is read: `Empty` before its load
/// fills it.
type AssocCtors =
    { Empty: obj
      Loaded: obj -> obj
      /// Returns Some payload when loaded.
      Read: obj -> obj option }

/// An owned list (a plain list, always loaded): its "constructors" are the
/// list itself.
let listCtors: Type -> AssocCtors =
    cache (fun listType ->
        { Empty = makeList (elementType listType) Seq.empty
          Loaded = id
          Read = Some })

/// An owned record (`Profile option` or `Settings`): the field is its own
/// payload, and there is no unloaded state.
let ownedOneCtors: AssocCtors =
    { Empty = null
      Loaded = id
      Read = Some }

let recordShape (t: Type) : RecordShape = Shape.record t

/// What a function type returns, if `t` is one (a function that builds a query).
let functionResult: Type -> Type option =
    cache (fun t -> if FSharpType.IsFunction t then Some(snd (FSharpType.GetFunctionElements t)) else None)

let typeName (t: Type) =
    if t.IsGenericType then
        let n = t.Name.Substring(0, t.Name.IndexOf('`'))
        let args = t.GetGenericArguments() |> Array.map (fun a -> a.Name) |> String.concat ", "
        $"{n}<{args}>"
    else
        t.Name

/// A type as F# code spells it: int64 for Int64, string for String.
let fsharpName (t: Type) =
    match t with
    | t when t = typeof<int16> -> "int16"
    | t when t = typeof<int> -> "int"
    | t when t = typeof<int64> -> "int64"
    | t when t = typeof<string> -> "string"
    | t when t = typeof<bool> -> "bool"
    | t when t = typeof<decimal> -> "decimal"
    | t when t = typeof<float> -> "float"
    | t when t = typeof<float32> -> "float32"
    | t -> typeName t

/// A type as F# code writes it, lists, options and tuples included:
/// `Payment list`, `Customer option`, `(decimal * string) list`.
let rec fsharpTypeName (t: Type) : string =
    if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
        $"{inner (t.GetGenericArguments().[0])} list"
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
        $"{inner (t.GetGenericArguments().[0])} option"
    elif FSharpType.IsTuple t then
        FSharpType.GetTupleElements t |> Array.map inner |> String.concat " * "
    else
        fsharpName t

and private inner (t: Type) =
    if FSharpType.IsTuple t then $"({fsharpTypeName t})" else fsharpTypeName t

/// A value as a message shows it, the same under the runtime and in a native
/// binary: no printf, no %A, nothing that depends on making code at run time
/// (docs/aot.md). Numbers, characters, text and booleans in F#'s literal form
/// (1uy, 2L, 1.0, 1.0f, 15M, 'x', "s", true); dates, times, Guids and enums in
/// one invariant form each; records, unions (options among them), tuples,
/// lists and arrays as F# writes them, on one line, read through Shape; any
/// other type, its own ToString where it has one, else its F# type name.
/// Bounded: three levels deep, ten items a
/// list or record, and 200 characters, each cut marked "...".
module Shown =
    open System.Globalization

    let private depth = 3
    let private items = 10
    let private length = 200
    let private inv = CultureInfo.InvariantCulture

    let private floating (text: string) =
        if text.Contains "." || text.Contains "E" || text.Contains "e" || text = "NaN" || text.Contains "Infinity" then text
        else text + ".0"

    let private literal (v: obj) : string option =
        match v with
        | :? string as s -> Some("\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
        | :? char as c -> Some("'" + string c + "'")
        | :? bool as b -> Some(if b then "true" else "false")
        | :? byte as n -> Some(n.ToString inv + "uy")
        | :? sbyte as n -> Some(n.ToString inv + "y")
        | :? int16 as n -> Some(n.ToString inv + "s")
        | :? uint16 as n -> Some(n.ToString inv + "us")
        | :? int as n -> Some(n.ToString inv)
        | :? uint32 as n -> Some(n.ToString inv + "u")
        | :? int64 as n -> Some(n.ToString inv + "L")
        | :? uint64 as n -> Some(n.ToString inv + "UL")
        | :? float as n -> Some(if Double.IsNaN n then "nan" elif Double.IsPositiveInfinity n then "infinity" elif Double.IsNegativeInfinity n then "-infinity" else floating (n.ToString("R", inv)))
        | :? float32 as n -> Some((if Single.IsNaN n then "nan" else floating (n.ToString("R", inv))) + "f")
        | :? decimal as n -> Some(n.ToString inv + "M")
        | :? DateTimeOffset as d -> Some(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", inv))
        | :? DateTime as d -> Some(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", inv))
        | :? DateOnly as d -> Some(d.ToString("yyyy-MM-dd", inv))
        | :? TimeOnly as t -> Some(t.ToString("HH:mm:ss.FFFFFFF", inv))
        | :? TimeSpan as t -> Some(t.ToString("c", inv))
        | :? Guid as g -> Some(g.ToString "D")
        | :? Enum as e -> Some(e.ToString())
        | _ -> None

    let private cut (all: string seq) =
        let shown = all |> Seq.truncate (items + 1) |> List.ofSeq
        if shown.Length > items then List.truncate items shown @ [ "..." ] else shown

    /// An item of a list or array: its element type, from its declared type.
    let private element (t: Type) =
        if t.IsArray then t.GetElementType()
        elif t.IsGenericType then t.GetGenericArguments().[0]
        else typeof<obj>

    /// `v`, declared as `declared` (which tells a None, null at run time).
    let rec private shown (level: int) (declared: Type) (v: obj) : string =
        match v with
        | null when declared.IsGenericType && declared.GetGenericTypeDefinition() = typedefof<option<_>> -> "None"
        | null when declared = typeof<unit> -> "()"
        | null -> "null"
        | _ ->
            match literal v with
            | Some text -> text
            | None when level >= depth -> "..."
            | None ->
                let t = v.GetType()
                let inner = shown (level + 1)
                let each (items: obj seq) = cut (Seq.map (inner (element t)) items)

                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
                    "[" + String.concat "; " (each (v :?> Collections.IEnumerable |> Seq.cast<obj>)) + "]"
                elif t.IsArray then
                    "[|" + String.concat "; " (each (v :?> Collections.IEnumerable |> Seq.cast<obj>)) + "|]"
                elif FSharpType.IsTuple t then
                    let parts = [ for p in t.GetProperties() do if p.Name.StartsWith "Item" then yield inner p.PropertyType (p.GetValue v) ]
                    "(" + String.concat ", " parts + ")"
                elif Shape.isRecord t then
                    let r = Shape.record t
                    let values = r.Deconstruct v
                    let fields = Seq.map2 (fun (p: PropertyInfo) x -> p.Name + " = " + inner p.PropertyType x) r.Fields values
                    "{ " + String.concat "; " (cut fields) + " }"
                elif Shape.isUnion t then
                    let u = Shape.union t
                    let case = u.Cases |> Array.find (fun c -> c.Tag = u.Tag v)
                    let fields = Array.map2 (fun (_, ft) x -> inner ft x) case.Fields (case.Read v)

                    match fields with
                    | [||] -> case.Name
                    | [| one |] when one.Contains " " && not (one.StartsWith "(" || one.StartsWith "[" || one.StartsWith "{" || one.StartsWith "\"") ->
                        case.Name + " (" + one + ")"
                    | [| one |] -> case.Name + " " + one
                    | many -> case.Name + " (" + String.concat ", " many + ")"
                elif t.GetMethod("ToString", Type.EmptyTypes).DeclaringType <> typeof<obj> then
                    v.ToString()
                else
                    fsharpTypeName t

    /// `v`, declared as `declared`, as a message shows it.
    let typed (declared: Type) (v: obj) : string =
        let text = shown 0 declared v
        if text.Length > length then text.Substring(0, length) + "..." else text

    /// `v` as a message shows it (a null as null: use `typed` to show a None).
    let value (v: obj) : string = typed typeof<obj> v

let private numeric =
    HashSet<Type> [ typeof<byte>; typeof<sbyte>; typeof<int16>; typeof<uint16>; typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<decimal>; typeof<float32>; typeof<float> ]

/// The provider types a value is read as by the type's own reader where the
/// database may keep it as another (SQLite: a decimal and a time as text, a
/// bool as an integer): a computed value, and hand-written SQL's.
let keptAsAnother =
    HashSet<Type> [ typeof<decimal>; typeof<bool>; typeof<DateTime>; typeof<DateTimeOffset>; typeof<Guid>; typeof<DateOnly>; typeof<TimeOnly>; typeof<TimeSpan> ]

/// Converts a value read from the database to `target` only when `target`
/// holds it exactly: a number into another number type when converting back
/// gives the same value (a bigint 4 or a numeric 3 is an int; a numeric 2.5
/// is not). Text is never parsed and nothing is rounded.
let exactly (target: Type) (v: obj) : Result<obj, string> =
    if target.IsInstanceOfType v then
        Ok v
    else
        let source = v.GetType()
        let name = fsharpName target
        let article = if "AEIOUaeiou".Contains name.[0] then "an" else "a"
        let refuse () = Error $"a {source.Name} {v} is not {article} {name}: Plasm reads a value only as a type that holds it exactly"

        if numeric.Contains source && numeric.Contains target then
            try
                let converted = Convert.ChangeType(v, target, Globalization.CultureInfo.InvariantCulture)
                let back = Convert.ChangeType(converted, source, Globalization.CultureInfo.InvariantCulture)
                if back.Equals v then Ok converted else refuse ()
            with
            | :? OverflowException -> refuse ()
        else
            refuse ()

/// A property read or a method call on an instance, as `Expr.PropertyGet` and
/// `Expr.Call` make it. Each of those asks FSharp.Reflection whether the
/// member's type is a union, which reads the type's attributes anew, and a
/// native binary instantiates an attribute from metadata on every read
/// (docs/aot.md). So each member's expression is made once, over variables,
/// and made again by putting the instance and arguments in their place, which
/// checks nothing. An instance or argument the member does not take goes to
/// FSharp.Core, which refuses it as before.
///
/// That `Expr.Substitute` does not check again is FSharp.Core's behaviour, not
/// its contract: were it to check, this would be as slow as before, not wrong.
/// Each dictionary holds one entry per member a query names, which the
/// compiled program bounds (F# Interactive adds those of its redefinitions).
module Quoted =
    open Microsoft.FSharp.Quotations

    let private gets = ConcurrentDictionary<PropertyInfo, Var * Expr>()
    let private calls = ConcurrentDictionary<MethodInfo, Var[] * Type[] * Expr>()

    let private made =
        Func<PropertyInfo, Var * Expr>(fun p ->
            let v = Var("x", p.DeclaringType)
            v, Expr.PropertyGet(Expr.Var v, p))

    let private madeCall =
        Func<MethodInfo, Var[] * Type[] * Expr>(fun m ->
            let types = Array.append [| m.DeclaringType |] (m.GetParameters() |> Array.map (fun p -> p.ParameterType))
            let vars = types |> Array.mapi (fun i t -> Var("a" + string i, t))
            vars, types, Expr.Call(Expr.Var vars.[0], m, [ for v in vars.[1..] -> Expr.Var v ]))

    /// `target.P`, for a property without arguments.
    let propertyGet (target: Expr) (p: PropertyInfo) : Expr =
        if not (p.DeclaringType.IsAssignableFrom target.Type) then
            Expr.PropertyGet(target, p)
        else
            let v, body = gets.GetOrAdd(p, made)
            body.Substitute(fun x -> if x = v then Some target else None)

    /// `target.M(args)`.
    let call (target: Expr) (m: MethodInfo) (args: Expr list) : Expr =
        let vars, types, body = calls.GetOrAdd(m, madeCall)
        let given = Array.ofList (target :: args)

        if given.Length <> types.Length || not (Array.forall2 (fun (t: Type) (e: Expr) -> t.IsAssignableFrom e.Type) types given) then
            Expr.Call(target, m, args)
        else
            body.Substitute(fun x ->
                match Array.IndexOf(vars, x) with
                | -1 -> None
                | i -> Some given.[i])

    /// `ExprShape.RebuildShapeCombination`, for the node `e` it was taken from.
    let rebuild (e: Expr) (shape: obj) (args: Expr list) : Expr =
        match e, args with
        | Patterns.PropertyGet(Some _, p, []), [ target ] -> propertyGet target p
        | Patterns.Call(Some _, m, _), target :: rest -> call target m rest
        | _ -> ExprShape.RebuildShapeCombination(shape, args)

/// Types defined in F# Interactive live in one generated module per submission
/// (FSI_0004.Domain+Customer). Re-evaluating a definition creates a new,
/// unrelated type with the same name.
module Fsi =
    let private pattern =
        System.Text.RegularExpressions.Regex(@"^FSI_(\d+)[.+](.+)$", System.Text.RegularExpressions.RegexOptions.Compiled)

    /// The submission number and the name within it, for a type defined in F# Interactive.
    let submission (t: Type) =
        match t.FullName with
        | null -> None
        | name ->
            let m = pattern.Match name

            if m.Success then
                Some(int m.Groups.[1].Value, m.Groups.[2].Value)
            else
                None

    let redefined (name: string) (used: int) (mapped: int) =
        if used < mapped then
            $"{name} in this query comes from an earlier F# Interactive submission (FSI_{used:D4}) than the {name} this repository's model maps (FSI_{mapped:D4}); the type was redefined after the query was built. Re-evaluate the query, and anything it is built from, after redefining a type."
        else
            $"{name} in this query comes from a later F# Interactive submission (FSI_{used:D4}) than the {name} this repository's model maps (FSI_{mapped:D4}); the type was redefined after the mappings were built. Re-evaluate the mappings and the repository after redefining a type."
