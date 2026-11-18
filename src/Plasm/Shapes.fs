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

/// The shapes of F# types, read through FSharp.Reflection (docs/aot.md), in a
/// native binary as under the runtime: there Plasm's build step keeps the
/// attributes FSharp.Reflection reads of a union's fields.
module internal Shape =
    let private all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance
    let private dynamicCode = RuntimeFeature.IsDynamicCodeSupported

    let private records = ConcurrentDictionary<Type, RecordShape>()
    let private unions = ConcurrentDictionary<Type, UnionShape>()
    let private options = ConcurrentDictionary<Type, OptionShape>()
    let private lists = ConcurrentDictionary<Type, ListShape>()

    type private X = System.Linq.Expressions.Expression

    /// `values -> new T(values[0], ...)`, compiled once where code can be
    /// generated (no reflection invoke per record).
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

    let internal reflectedRecord (t: Type) : RecordShape =
        let fields = FSharpType.GetRecordFields(t, true)

        { Type = t
          Fields = fields
          Construct = if dynamicCode then compileConstructor t else FSharpValue.PreComputeRecordConstructor(t, true)
          Deconstruct = if dynamicCode then compileReader t fields else FSharpValue.PreComputeRecordReader(t, true) }

    /// A one-field case's constructor and field reader (a typed id's), with
    /// no array per value: compiled when first used where code can be made at
    /// run time, else through method invokers. A case of several has its field
    /// on its own class.
    let private wrapping (t: Type) (c: UnionCaseInfo) : (obj -> obj) * (obj -> obj) =
        match c.GetFields() with
        | [| field |] ->
            let ctor = lazy (FSharpValue.PreComputeUnionConstructorInfo(c, true))

            if dynamicCode then
                let compiled =
                    lazy
                        (let v = X.Parameter(typeof<obj>, "v")
                         let wrap = X.Lambda<Func<obj, obj>>(X.Convert(X.Call(ctor.Value, X.Convert(v, field.PropertyType)), typeof<obj>), v).Compile()
                         let unwrap = X.Lambda<Func<obj, obj>>(X.Convert(X.Property(X.Convert(v, field.DeclaringType), field), typeof<obj>), v).Compile()
                         wrap, unwrap)

                (fun f -> (fst compiled.Value).Invoke f), (fun v -> (snd compiled.Value).Invoke v)
            else
                let invokers = lazy (MethodInvoker.Create ctor.Value, MethodInvoker.Create(field.GetGetMethod true))
                (fun f -> (fst invokers.Value).Invoke(null, f)), (fun v -> (snd invokers.Value).Invoke v)
        | _ -> (fun _ -> invalidOp $"{t.Name}.{c.Name} has no one field to wrap"), (fun _ -> invalidOp $"{t.Name}.{c.Name} has no one field")

    /// A union through FSharp.Reflection. A case built from fields (its NewX
    /// takes them) that FSharp.Reflection sees without any is a native binary
    /// that lost their attributes: its assembly was not given Plasm's build
    /// step (docs/aot.md), and reading on would build the case wrong.
    let internal reflectedUnion (t: Type) : UnionShape =
        let tag = FSharpValue.PreComputeUnionTagReader(t, true)

        for c in FSharpType.GetUnionCases(t, true) do
            if c.GetFields().Length = 0 then
                match t.GetMethod("New" + c.Name, BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
                | m when not (isNull m) && m.GetParameters().Length > 0 ->
                    invalidOp
                        $"{t.FullName}.{c.Name} has fields that this native binary cannot see: its assembly {t.Assembly.GetName().Name} was compiled without Plasm's build step for F# union fields. Publish with Plasm's targets (Plasm.targets, which a reference to Plasm brings), see docs/aot.md"
                | _ -> ()

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

    /// An F# record.
    let isRecord (t: Type) = FSharpType.IsRecord(t, true)

    /// An F# union, FSharp.Core's own (option, list, Result) included.
    let isUnion (t: Type) = FSharpType.IsUnion(t, true)

    let record (t: Type) : RecordShape = records.GetOrAdd(t, reflectedRecord)

    let union (t: Type) : UnionShape = unions.GetOrAdd(t, reflectedUnion)

    let private optionOf<'T> () : Type * OptionShape =
        typeof<'T>,
        { Element = typeof<'T>
          Some = fun v -> box (Some(unbox<'T> v))
          Value =
            fun v ->
                match unbox<'T option> v with
                | Some x -> Some(box x)
                | None -> None }

    let private listOf<'T> () : Type * ListShape =
        typeof<'T>,
        { Element = typeof<'T>
          Make = fun items -> box [ for i in items -> unbox<'T> i ]
          Items = fun l -> (unbox<'T list> l) |> Seq.map box }

    // Written out for the provider's value types: a native binary has no code
    // for an option or a list of a value type made at run time.
    do
        for t, s in
            [ optionOf<int16> (); optionOf<int> (); optionOf<int64> (); optionOf<bool> (); optionOf<decimal> (); optionOf<float> ()
              optionOf<float32> (); optionOf<Guid> (); optionOf<DateTime> (); optionOf<DateTimeOffset> (); optionOf<DateOnly> ()
              optionOf<TimeOnly> (); optionOf<TimeSpan> (); optionOf<byte> (); optionOf<char> () ] do
            options.[t] <- s

        for t, s in
            [ listOf<int16> (); listOf<int> (); listOf<int64> (); listOf<bool> (); listOf<decimal> (); listOf<float> ()
              listOf<float32> (); listOf<Guid> (); listOf<DateTime> (); listOf<DateTimeOffset> (); listOf<DateOnly> ()
              listOf<TimeOnly> (); listOf<TimeSpan> (); listOf<byte> (); listOf<char> () ] do
            lists.[t] <- s

    /// A value type whose options and lists are written out above.
    let writtenOut (t: Type) = options.ContainsKey t

    /// Where code cannot be made at run time, an option or a list only of a
    /// reference type or of a value type written out above.
    let private refuse (what: string) (element: Type) =
        invalidOp
            $"{element.FullName} is held in {what}: a native binary holds an option or a list only of a reference type or of one of the provider's own value types (docs/aot.md)"

    let private reflectedOption (element: Type) : OptionShape =
        if element.IsValueType && not dynamicCode then refuse "an option" element

        let t = typedefof<option<_>>.MakeGenericType element
        let some = t.GetMethod("Some", BindingFlags.Static ||| BindingFlags.Public)
        let value = t.GetProperty("Value")

        { Element = element
          Some = fun v -> some.Invoke(null, [| v |])
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
        if element.IsValueType && not dynamicCode then refuse "a list" element

        if dynamicCode then
            let maker = typedefof<ListMaker<_>>.MakeGenericType element
            let flags = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
            let make = maker.GetMethod("Make", flags).CreateDelegate<Func<obj seq, obj>>()
            let items = maker.GetMethod("Items", flags).CreateDelegate<Func<obj, obj seq>>()

            { Element = element
              Make = make.Invoke
              Items = items.Invoke }
        else
            // A list of a reference type: its own Empty and Cons, by reflection.
            let t = typedefof<list<_>>.MakeGenericType element
            let empty = t.GetProperty("Empty", BindingFlags.Static ||| BindingFlags.Public).GetValue null
            let cons = t.GetMethod("Cons", BindingFlags.Static ||| BindingFlags.Public)

            { Element = element
              Make = fun items -> Seq.foldBack (fun item acc -> cons.Invoke(null, [| item; acc |])) items empty
              Items = fun l -> (l :?> Collections.IEnumerable) |> Seq.cast<obj> }

    /// A tuple of `t`'s type from its items. Where code can be made at run
    /// time, FSharp.Reflection's constructor; in a native binary, which has no
    /// code for the constructor of a tuple only a quotation builds (one with a
    /// value type in it), the tuple made uninitialised and its fields set,
    /// which needs no code of that instantiation (docs/aot.md).
    let private tuples = ConcurrentDictionary<Type, obj[] -> obj>()

    /// A tuple made by setting its fields (a native binary's way, used under
    /// the runtime by a test): System.Tuple's m_Item1..m_Item7 and m_Rest, a
    /// struct tuple's Item1..Item7 and Rest. These are the framework's private
    /// names, verified per .NET version (docs/aot.md).
    let rec internal tupleByFields (t: Type) : obj[] -> obj =
        let args = t.GetGenericArguments()
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

        let name (i: int) =
            match t.IsValueType, i with
            | false, 7 -> "m_Rest"
            | false, i -> "m_Item" + string (i + 1)
            | true, 7 -> "Rest"
            | true, i -> "Item" + string (i + 1)

        let fields =
            Array.init args.Length (fun i ->
                match t.GetField(name i, flags) with
                | null ->
                    invalidOp
                        $"{t.FullName} has no field {name i} in {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}: a native binary makes a tuple by setting its fields, verified with .NET 11.0 (docs/aot.md)"
                | f -> f)

        // Eight items and more nest their rest in an eighth.
        let rest = if args.Length = 8 then Some(tupleByFields args.[7]) else None

        fun items ->
            let tuple = RuntimeHelpers.GetUninitializedObject t

            for i in 0 .. min 6 (args.Length - 1) do
                fields.[i].SetValue(tuple, items.[i])

            match rest with
            | Some make -> fields.[7].SetValue(tuple, make (Array.sub items 7 (items.Length - 7)))
            | None -> ()

            tuple

    let private tupleOf (t: Type) : obj[] -> obj =
        if dynamicCode then FSharpValue.PreComputeTupleConstructor t else tupleByFields t

    let tuple (t: Type) : obj[] -> obj = tuples.GetOrAdd(t, Func<Type, obj[] -> obj>(tupleOf))

    /// `element option`.
    let option (element: Type) : OptionShape = options.GetOrAdd(element, reflectedOption)

    /// `element list`.
    let list (element: Type) : ListShape = lists.GetOrAdd(element, reflectedList)

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

