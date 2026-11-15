/// Cached reflection helpers for constructing immutable F# values.
module internal Plasm.Reflection

open System
open System.Collections.Concurrent
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

let isAssoc (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Assoc<_>>

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

type AssocCtors =
    { NotLoaded: obj
      Loaded: obj -> obj
      /// Returns Some payload when loaded.
      Read: obj -> obj option }

let assocCtors: Type -> AssocCtors =
    cache (fun assocType ->
        let cases = FSharpType.GetUnionCases(assocType)
        let notLoaded = cases |> Array.find (fun c -> c.Name = "NotLoaded")
        let loaded = cases |> Array.find (fun c -> c.Name = "Loaded")
        let loadedCtor = FSharpValue.PreComputeUnionConstructor(loaded)
        let tagReader = FSharpValue.PreComputeUnionTagReader(assocType)
        let fieldReader = FSharpValue.PreComputeUnionReader(loaded)
        { NotLoaded = FSharpValue.MakeUnion(notLoaded, [||])
          Loaded = fun v -> loadedCtor [| v |]
          Read = fun v -> if tagReader v = loaded.Tag then Some((fieldReader v).[0]) else None })

/// An owned list (a plain list, always loaded): its "constructors" are the
/// list itself.
let listCtors: Type -> AssocCtors =
    cache (fun listType ->
        { NotLoaded = makeList (elementType listType) Seq.empty
          Loaded = id
          Read = Some })

/// An owned record (`Profile option` or `Settings`): the field is its own
/// payload, and there is no unloaded state.
let ownedOneCtors: AssocCtors =
    { NotLoaded = null
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
