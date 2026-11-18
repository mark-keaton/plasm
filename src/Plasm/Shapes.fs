namespace Plasm

open System
open System.Collections.Concurrent
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.FSharp.Reflection

/// A record as Plasm reads and builds it (docs/aot.md).
type RecordShape =
    { Type: Type
      Fields: PropertyInfo[]
      Construct: obj[] -> obj
      Deconstruct: obj -> obj[] }

/// One case of a union as Plasm reads and builds it.
type UnionCaseShape =
    { Name: string
      Tag: int
      /// Its fields' names and types, in order.
      Fields: (string * Type)[]
      Make: obj[] -> obj
      Read: obj -> obj[]
      /// For a case of one field (a typed id), the case from its field and
      /// its field from it, without an array per value.
      Wrap: obj -> obj
      Unwrap: obj -> obj }

/// A union as Plasm reads and builds it.
type UnionShape =
    { Type: Type
      /// A private representation: its cases are the domain's own, built
      /// through its create function, never by Plasm.
      Private: bool
      Cases: UnionCaseShape[]
      Tag: obj -> int }

/// `'T option` for one element type.
type OptionShape =
    { Element: Type
      Some: obj -> obj
      Value: obj -> obj option }

/// `'T list` for one element type.
type ListShape =
    { Element: Type
      Make: obj seq -> obj
      Items: obj -> obj seq }

/// The shapes Plasm's build step generated (docs/aot.md), registered when
/// Plasm first needs a shape.
type IShapes =
    abstract Register: unit -> unit

/// Names, in an application, the shapes Plasm's build step generated into it
/// (docs/aot.md).
[<AttributeUsage(AttributeTargets.Assembly); Sealed>]
type ShapesAttribute
    ([<System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)>] registration: Type) =
    inherit Attribute()
    member _.Registration = registration

/// The shapes of F# types: those Plasm's build step generated into the
/// application (docs/aot.md), else, where code can be generated at run time
/// (the runtime, F# Interactive), read by reflection. A native binary has no
/// reflection over F# unions, so there a type without a generated shape is an
/// error naming the step.
module internal Shape =
    let internal records = ConcurrentDictionary<Type, RecordShape>()
    let internal unions = ConcurrentDictionary<Type, UnionShape>()
    let internal options = ConcurrentDictionary<Type, OptionShape>()
    let internal lists = ConcurrentDictionary<Type, ListShape>()

    /// Whether shapes must come from the build: false under the runtime and in
    /// F# Interactive, true in a native binary (and in a program published as
    /// one, whose runtime runs it as the binary would).
    let generatedOnly = not RuntimeFeature.IsDynamicCodeSupported

    /// The application's generated shapes, registered once, before the first
    /// lookup.
    let private registered =
        lazy
            (match Assembly.GetEntryAssembly() with
             | null -> ()
             | entry ->
                 match entry.GetCustomAttribute(typeof<ShapesAttribute>) with
                 | :? ShapesAttribute as a -> (Activator.CreateInstance a.Registration :?> IShapes).Register()
                 | _ -> ())

    let private ready () = registered.Force()

    let private missing (what: string) (t: Type) =
        invalidOp
            $"{t.FullName} is {what} without a shape: a native binary reads F# types only through the shapes Plasm's build step generates. Build the application with Plasm's build step (docs/aot.md), with {t.Name} public in an assembly the application references"

    type private X = System.Linq.Expressions.Expression

    /// `values -> new T(values[0], ...)`, compiled once (no reflection invoke
    /// per record).
    let private compileConstructor (t: Type) : obj[] -> obj =
        let ctor = FSharpValue.PreComputeRecordConstructorInfo(t, true)
        let values = X.Parameter(typeof<obj[]>, "values")

        let args =
            ctor.GetParameters()
            |> Array.mapi (fun i p -> X.Convert(X.ArrayIndex(values, X.Constant i), p.ParameterType) :> X)

        let f = X.Lambda<Func<obj[], obj>>(X.Convert(X.New(ctor, args), typeof<obj>), values).Compile()
        fun vs -> f.Invoke vs

    /// `record -> [| box record.F1; ... |]`, compiled once.
    let private compileReader (t: Type) (fields: PropertyInfo[]) : obj -> obj[] =
        let o = X.Parameter(typeof<obj>, "o")
        let typed = X.Convert(o, t)
        let items = fields |> Array.map (fun p -> X.Convert(X.Property(typed, p), typeof<obj>) :> X)
        let f = X.Lambda<Func<obj, obj[]>>(X.NewArrayInit(typeof<obj>, items), o).Compile()
        fun v -> f.Invoke v

    let private reflectedRecord (t: Type) : RecordShape =
        let fields = FSharpType.GetRecordFields(t, true)

        { Type = t
          Fields = fields
          Construct = compileConstructor t
          Deconstruct = compileReader t fields }

    /// A one-field case's constructor and field reader, compiled when first
    /// used (by a typed id). A case of several has its field on its own class.
    let private wrapping (t: Type) (c: UnionCaseInfo) : (obj -> obj) * (obj -> obj) =
        match c.GetFields() with
        | [| field |] ->
            let compiled =
                lazy
                    (let v = X.Parameter(typeof<obj>, "v")
                     let ctor = FSharpValue.PreComputeUnionConstructorInfo(c, true)
                     let wrap = X.Lambda<Func<obj, obj>>(X.Convert(X.Call(ctor, X.Convert(v, field.PropertyType)), typeof<obj>), v).Compile()
                     let unwrap = X.Lambda<Func<obj, obj>>(X.Convert(X.Property(X.Convert(v, field.DeclaringType), field), typeof<obj>), v).Compile()
                     wrap, unwrap)

            (fun f -> (fst compiled.Value).Invoke f), (fun v -> (snd compiled.Value).Invoke v)
        | _ -> (fun _ -> invalidOp $"{t.Name}.{c.Name} has no one field to wrap"), (fun _ -> invalidOp $"{t.Name}.{c.Name} has no one field")

    let private reflectedUnion (t: Type) : UnionShape =
        let tag = FSharpValue.PreComputeUnionTagReader(t, true)

        { Type = t
          Private = not (FSharpType.IsUnion(t, false))
          Cases =
            FSharpType.GetUnionCases(t, true)
            |> Array.map (fun c ->
                let wrap, unwrap = wrapping t c

                { Name = c.Name
                  Tag = c.Tag
                  Fields = c.GetFields() |> Array.map (fun p -> p.Name, p.PropertyType)
                  Make = FSharpValue.PreComputeUnionConstructor(c, true)
                  Read = FSharpValue.PreComputeUnionReader(c, true)
                  Wrap = wrap
                  Unwrap = unwrap })
          Tag = tag }

    let private isFSharpCore (t: Type) = t.Assembly = typeof<option<int>>.Assembly

    /// An F# record: a generated shape, or a type F# compiled as one.
    let isRecord (t: Type) = ready (); records.ContainsKey t || FSharpType.IsRecord(t, true)

    /// An F# union, FSharp.Core's own (option, list, Result) included.
    let isUnion (t: Type) = ready (); unions.ContainsKey t || FSharpType.IsUnion(t, true)

    let record (t: Type) : RecordShape =
        ready ()

        match records.TryGetValue t with
        | true, s -> s
        | _ when generatedOnly && not (isFSharpCore t) -> missing "a record" t
        | _ -> records.GetOrAdd(t, reflectedRecord)

    let union (t: Type) : UnionShape =
        ready ()

        match unions.TryGetValue t with
        | true, s -> s
        | _ when generatedOnly && not (isFSharpCore t) -> missing "a union" t
        | _ -> unions.GetOrAdd(t, reflectedUnion)

    let private reflectedOption (element: Type) : OptionShape =
        let t = typedefof<option<_>>.MakeGenericType element
        let some = FSharpType.GetUnionCases(t) |> Array.find (fun c -> c.Name = "Some")
        let make = FSharpValue.PreComputeUnionConstructor some
        let value = t.GetProperty("Value")

        { Element = element
          Some = fun v -> make [| v |]
          Value = fun v -> if isNull v then None else Some(value.GetValue v) }

    type private ListMaker<'T>() =
        static member Make(items: obj seq) : obj =
            let mutable acc = []
            let arr = Seq.toArray items

            for i in arr.Length - 1 .. -1 .. 0 do
                acc <- (unbox<'T> arr.[i]) :: acc

            box acc

        static member Items(list: obj) : obj seq = (unbox<'T list> list) |> Seq.map box

    let private reflectedList (element: Type) : ListShape =
        let maker = typedefof<ListMaker<_>>.MakeGenericType element
        let flags = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
        let make = maker.GetMethod("Make", flags).CreateDelegate<Func<obj seq, obj>>()
        let items = maker.GetMethod("Items", flags).CreateDelegate<Func<obj, obj seq>>()

        { Element = element
          Make = make.Invoke
          Items = items.Invoke }

    /// What a native binary cannot build without generated code: an instance
    /// of a generic type over a value type, or over a type it never compiled.
    let private orMissing (what: string) (element: Type) (f: Type -> 'S) =
        try
            f element
        with :? NotSupportedException ->
            missing what element

    /// `element option`.
    let option (element: Type) : OptionShape =
        ready ()

        match options.TryGetValue element with
        | true, s -> s
        | _ -> options.GetOrAdd(element, orMissing "held in an option" element reflectedOption)

    /// `element list`.
    let list (element: Type) : ListShape =
        ready ()

        match lists.TryGetValue element with
        | true, s -> s
        | _ -> lists.GetOrAdd(element, orMissing "held in a list" element reflectedList)

/// The [<ReflectedDefinition>] bodies of an assembly, found by the member's
/// declaring type, name and parameter types (docs/aot.md). FSharp.Core finds
/// them by metadata token, and a native binary has none; this decodes the same
/// resource with FSharp.Core's own decoder, and keys it without tokens.
module internal Definitions =
    open Microsoft.FSharp.Quotations

    let private flags = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let private core = typeof<Expr>.Assembly

    /// FSharp.Core's versions this decoder is verified against (docs/aot.md).
    let verified = [ "8.0.0.0"; "11.0.0.0" ]

    /// A part of FSharp.Core's decoder, or an error naming FSharp.Core's
    /// version: it is FSharp.Core's own, not public, and may change.
    let private part (what: string) (found: 'a) : 'a =
        if isNull (box found) then
            let versions = String.concat ", " verified

            invalidOp
                $"FSharp.Core {core.GetName().Version} has no {what}, which Plasm uses to read a [<ReflectedDefinition>] in a native binary; it is verified with FSharp.Core {versions} (docs/aot.md)"

        found

    type private Decoder =
        { Reader: obj
          Unpickle: MethodInfo
          Env: Type
          Subst: MethodInfo }

    let private decoder =
        lazy
            (let patterns = part "PatternsModule" (core.GetType "Microsoft.FSharp.Quotations.PatternsModule")

             { Reader = (part "u_ReflectedDefinitions" (patterns.GetMethod("get_u_ReflectedDefinitions", flags))).Invoke(null, [||])
               Unpickle =
                 part
                     "SimpleUnpickle.unpickleObj"
                     ((part "SimpleUnpickle" (core.GetType "Microsoft.FSharp.Quotations.PatternsModule+SimpleUnpickle")).GetMethod("unpickleObj", flags))
               Env = part "BindingEnv" (core.GetType "Microsoft.FSharp.Quotations.PatternsModule+BindingEnv")
               Subst = part "mkTyparSubst" (patterns.GetMethod("mkTyparSubst", flags)) })

    /// Whether this FSharp.Core's decoder is all there (a test pins it).
    let check () = decoder.Force() |> ignore

    let private key (m: MethodBase) =
        m.DeclaringType.FullName + "::" + m.Name + "(" + String.Join(",", m.GetParameters() |> Array.map (fun p -> p.ParameterType.FullName)) + ")"

    /// Each definition of an assembly, by key: its builder, taking FSharp.Core's
    /// binding environment.
    let private decode (a: Assembly) : Collections.Generic.Dictionary<string, obj> =
        let d = decoder.Value
        let found = Collections.Generic.Dictionary<string, obj>()

        let mappings =
            a.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
            |> Array.choose (function
                | :? CompilationMappingAttribute as m when not (isNull m.ResourceName) -> Some(m.ResourceName, m.TypeDefinitions)
                | _ -> None)

        let listType = (part "u_ReflectedDefinitions's type" (d.Reader.GetType().BaseType)).GetGenericArguments().[1]

        for name in a.GetManifestResourceNames() do
            if name.StartsWith("ReflectedDefinitions", StringComparison.Ordinal) then
                use stream = part ("the resource " + name) (a.GetManifestResourceStream name)
                use bytes = new IO.MemoryStream()
                stream.CopyTo bytes

                let referenced =
                    mappings |> Array.tryPick (fun (n, ts) -> if n = name then Some ts else None) |> Option.defaultValue [||]

                let entries = d.Unpickle.MakeGenericMethod(listType).Invoke(null, [| a; referenced; d.Reader; bytes.ToArray() |])

                for entry in entries :?> Collections.IEnumerable do
                    let pair = entry :?> ITuple
                    found.[key (pair.[0] :?> MethodBase)] <- pair.[1]

        found

    let private assemblies = ConcurrentDictionary<Assembly, Collections.Generic.Dictionary<string, obj>>()

    let private carries (a: Assembly) =
        a.GetManifestResourceNames() |> Array.exists (fun n -> n.StartsWith("ReflectedDefinitions", StringComparison.Ordinal))

    /// The body of `m`, if it has one.
    let tryFind (m: MethodBase) : Expr option =
        if isNull m.DeclaringType || not (carries m.DeclaringType.Assembly) then None else

        match assemblies.GetOrAdd(m.DeclaringType.Assembly, decode).TryGetValue(key m) with
        | true, builder ->
            let d = decoder.Value
            let typeArgs = Array.append (m.DeclaringType.GetGenericArguments()) (if m.IsGenericMethod then m.GetGenericArguments() else [||])
            let subst = d.Subst.Invoke(null, [| typeArgs |])
            let vars = (part "Map.Empty" (typeof<Map<int, Var>>.GetProperty("Empty", flags))).GetValue null
            let ctor = part "BindingEnv's constructor" (d.Env.GetConstructors(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.tryHead |> Option.toObj)
            let binding = ctor.Invoke [| vars; 0; subst |]
            let invoke = part "FSharpFunc.Invoke" (typedefof<FSharpFunc<_, _>>.MakeGenericType(d.Env, typeof<Expr>).GetMethod "Invoke")
            Some(invoke.Invoke(builder, [| binding |]) :?> Expr)
        | _ -> None

/// Where the code Plasm's build step generates (docs/aot.md) registers the
/// shapes of the application's F# types, before a model is built. Not for
/// hand-written code: the record is the only place a shape is stated.
[<AbstractClass; Sealed>]
type Shapes =
    static member Record<'T>(fields: PropertyInfo[], construct: obj[] -> 'T, deconstruct: 'T -> obj[]) : unit =
        Shape.records.[typeof<'T>] <-
            { Type = typeof<'T>
              Fields = fields
              Construct = fun vs -> box (construct vs)
              Deconstruct = fun v -> deconstruct (unbox<'T> v) }

    static member Case<'T>(name: string, tag: int, fields: (string * Type)[], make: obj[] -> 'T, read: 'T -> obj[]) : UnionCaseShape =
        { Name = name
          Tag = tag
          Fields = fields
          Make = fun vs -> box (make vs)
          Read = fun v -> read (unbox<'T> v)
          Wrap = fun f -> box (make [| f |])
          Unwrap = fun v -> (read (unbox<'T> v)).[0] }

    /// A case of one field: its constructor and reader take the field itself.
    static member Case<'T, 'F>(name: string, tag: int, field: string, wrap: 'F -> 'T, unwrap: 'T -> 'F) : UnionCaseShape =
        { Name = name
          Tag = tag
          Fields = [| field, typeof<'F> |]
          Make = fun vs -> box (wrap (unbox<'F> vs.[0]))
          Read = fun v -> [| box (unwrap (unbox<'T> v)) |]
          Wrap = fun f -> box (wrap (unbox<'F> f))
          Unwrap = fun v -> box (unwrap (unbox<'T> v)) }

    static member Union<'T>(isPrivate: bool, tag: 'T -> int, cases: UnionCaseShape[]) : unit =
        Shape.unions.[typeof<'T>] <-
            { Type = typeof<'T>
              Private = isPrivate
              Cases = cases
              Tag = fun v -> tag (unbox<'T> v) }

    static member Option<'T>() : unit =
        Shape.options.[typeof<'T>] <-
            { Element = typeof<'T>
              Some = fun v -> box (Some(unbox<'T> v))
              Value =
                fun v ->
                    match unbox<'T option> v with
                    | Some x -> Some(box x)
                    | None -> None }

    static member List<'T>() : unit =
        Shape.lists.[typeof<'T>] <-
            { Element = typeof<'T>
              Make = fun items -> box [ for i in items -> unbox<'T> i ]
              Items = fun l -> (unbox<'T list> l) |> Seq.map box }
