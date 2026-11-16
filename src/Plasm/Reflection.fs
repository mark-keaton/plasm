/// Cached reflection helpers for constructing immutable F# values.
module internal Plasm.Reflection

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Linq.Expressions
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
let makeSome: Type -> (obj -> obj) =
    cache (fun optType ->
        let case = FSharpType.GetUnionCases(optType) |> Array.find (fun c -> c.Name = "Some")
        let ctor = FSharpValue.PreComputeUnionConstructor(case)
        fun v -> ctor [| v |])

/// Reads the payload of an option value: None (null) -> None.
let getOptionValue: Type -> (obj -> obj option) =
    cache (fun optType ->
        let prop = optType.GetProperty("Value")
        fun v -> if isNull v then None else Some(prop.GetValue v))

type private ListMaker<'T>() =
    static member Make(items: obj seq) : obj =
        let mutable acc = []
        let arr = Seq.toArray items
        for i in arr.Length - 1 .. -1 .. 0 do
            acc <- (unbox<'T> arr.[i]) :: acc
        box acc

    static member Items(list: obj) : obj seq = (unbox<'T list> list) |> Seq.map box

/// Builds an F# list of the given element type from boxed items.
let makeList: Type -> (obj seq -> obj) =
    cache (fun elemType ->
        let m = typedefof<ListMaker<_>>.MakeGenericType(elemType).GetMethod("Make", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        let f = m.CreateDelegate<Func<obj seq, obj>>()
        fun items -> f.Invoke items)

let listItems: Type -> (obj -> obj seq) =
    cache (fun elemType ->
        let m = typedefof<ListMaker<_>>.MakeGenericType(elemType).GetMethod("Items", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        let f = m.CreateDelegate<Func<obj, obj seq>>()
        fun l -> f.Invoke l)

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

type RecordShape =
    { Fields: PropertyInfo[]
      Construct: obj[] -> obj
      Deconstruct: obj -> obj[] }

/// Compiles `values -> new T(values[0], ...)` (avoids reflection invoke per record).
let private compileConstructor (t: Type) : obj[] -> obj =
    let ctor = FSharpValue.PreComputeRecordConstructorInfo(t, true)
    let values = Expression.Parameter(typeof<obj[]>, "values")

    let args =
        ctor.GetParameters()
        |> Array.mapi (fun i p ->
            Expression.Convert(Expression.ArrayIndex(values, Expression.Constant i), p.ParameterType) :> Expression)

    let body = Expression.Convert(Expression.New(ctor, args), typeof<obj>)
    let f = Expression.Lambda<Func<obj[], obj>>(body, values).Compile()
    fun vs -> f.Invoke vs

/// Compiles `record -> [| box record.F1; ... |]`.
let private compileReader (t: Type) (fields: PropertyInfo[]) : obj -> obj[] =
    let o = Expression.Parameter(typeof<obj>, "o")
    let typed = Expression.Convert(o, t)

    let items =
        fields
        |> Array.map (fun p -> Expression.Convert(Expression.Property(typed, p), typeof<obj>) :> Expression)

    let f = Expression.Lambda<Func<obj, obj[]>>(Expression.NewArrayInit(typeof<obj>, items), o).Compile()
    fun v -> f.Invoke v

let recordShape: Type -> RecordShape =
    cache (fun t ->
        let fields = FSharpType.GetRecordFields(t, true)

        { Fields = fields
          Construct = compileConstructor t
          Deconstruct = compileReader t fields })

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

let private numeric =
    HashSet<Type> [ typeof<byte>; typeof<sbyte>; typeof<int16>; typeof<uint16>; typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<decimal>; typeof<float32>; typeof<float> ]

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
