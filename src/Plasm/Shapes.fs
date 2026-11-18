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

/// How Plasm reads an F# union here (docs/aot.md).
[<RequireQualifiedAccess>]
type internal UnionReader =
    /// FSharp.Reflection: the runtime, F# Interactive, the browser.
    | FSharpReflection
    /// The union's compiled structure (its Tags class, its NewX methods, its
    /// case properties): a native binary, which keeps no attributes on a
    /// union's members, so FSharp.Reflection finds no fields there.
    | CompiledStructure

/// The shapes of F# types, read by reflection (docs/aot.md). A union is read
/// through FSharp.Reflection where its attributes are kept, else by its
/// compiled structure; a record through FSharp.Reflection everywhere.
module internal Shape =
    let private all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance
    let private dynamicCode = RuntimeFeature.IsDynamicCodeSupported

    type private Probe = Probe of int

    /// Whether FSharp.Reflection sees a union's fields here: decided once.
    let private probed =
        lazy
            (if (FSharpType.GetUnionCases(typeof<Probe>, true)).[0].GetFields().Length = 1 then
                 UnionReader.FSharpReflection
             else
                 UnionReader.CompiledStructure)

    /// For tests: PLASM_SHAPES=structure reads unions by their compiled
    /// structure under the runtime too.
    let mutable internal forced =
        match Environment.GetEnvironmentVariable "PLASM_SHAPES" with
        | "structure" -> Some UnionReader.CompiledStructure
        | _ -> None

    /// The reader in use (the native check asserts it).
    let reader () =
        match forced with
        | Some r -> r
        | None -> probed.Value

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

    /// A union through FSharp.Reflection.
    let internal reflectedUnion (t: Type) : UnionShape =
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

    /// A union by its compiled structure: its cases and tags from its Tags
    /// class (or, for one case, from its one NewX method or static property);
    /// each case built by its NewX method (with fields) or read from its
    /// static property (without); its fields from NewX's parameters, read
    /// through the properties of that name on the case's class (or on the
    /// union, for one case or a struct). Anything else is an error naming the
    /// type, never a shape guessed.
    let internal structuralUnion (t: Type) : UnionShape =
        let fail (why: string) : 'a =
            invalidOp $"{t.FullName} is a union whose compiled layout Plasm cannot read ({why}), and a native binary reads a union only by its layout (docs/aot.md)"

        let isUnionValue (ty: Type) = ty = t

        let named =
            match t.GetNestedType("Tags", all) with
            | null ->
                let news = t.GetMethods all |> Array.filter (fun m -> m.IsStatic && m.Name.StartsWith "New" && isUnionValue m.ReturnType)

                let statics =
                    t.GetProperties all
                    |> Array.filter (fun p ->
                        let g = p.GetGetMethod true
                        not (isNull g) && g.IsStatic && isUnionValue p.PropertyType)

                // One NewX is the case; without one, one static property is (a
                // union's own static members may return it too).
                match news, statics with
                | [| m |], _ -> [ m.Name.Substring 3, 0 ]
                | [||], [| p |] -> [ p.Name, 0 ]
                | _ -> fail "it has no Tags class, and not one case"
            | tags ->
                [ for f in tags.GetFields all do
                      if f.IsLiteral && f.FieldType = typeof<int> then
                          f.Name, unbox<int> (f.GetRawConstantValue()) ]
                |> List.sortBy snd

        if named.IsEmpty then fail "it has no cases"

        // A generic union's case classes are generic definitions: closed over
        // the union's own type arguments.
        let caseClasses =
            t.GetNestedTypes all
            |> Array.map (fun n -> if n.IsGenericTypeDefinition && t.IsGenericType then n.MakeGenericType(t.GetGenericArguments()) else n)
            |> Array.filter (fun n -> n.IsSubclassOf t)

        // NewX builds a case with fields (FSharp.Core's own: X).
        let builder (name: string) =
            [ "New" + name; name ]
            |> List.tryPick (fun n ->
                match t.GetMethod(n, all) with
                | m when not (isNull m) && m.IsStatic && isUnionValue m.ReturnType && m.GetParameters().Length > 0 -> Some m
                | _ -> None)

        let case (name: string, tag: int) : UnionCaseShape =
            match builder name with
            | Some m ->
                let ps = m.GetParameters()

                // A named field is compiled _name; an unnamed one item, item1.
                let property (p: ParameterInfo) =
                    if p.Name.StartsWith "_" then p.Name.Substring 1
                    elif p.Name.StartsWith "item" then "I" + p.Name.Substring 1
                    else p.Name

                let holder =
                    caseClasses
                    |> Array.tryFind (fun n -> n.Name = name || n.Name = "_" + name)
                    |> Option.defaultValue t

                // FSharp.Core's own fields are named as their parameters, capitalised.
                let props =
                    ps
                    |> Array.map (fun p ->
                        match holder.GetProperty(property p, all) with
                        | null ->
                            match holder.GetProperties all |> Array.filter (fun q -> String.Equals(q.Name, property p, StringComparison.OrdinalIgnoreCase)) with
                            | [| q |] -> q
                            | _ -> fail $"no property {property p} for the case {name}"
                        | prop -> prop)

                let getters = props |> Array.map (fun p -> MethodInvoker.Create(p.GetGetMethod true))
                let read (v: obj) = getters |> Array.map (fun g -> g.Invoke v)
                let builds = MethodInvoker.Create m
                let make (vs: obj[]) = m.Invoke(null, vs)

                { Name = name
                  Tag = tag
                  Fields = Array.map2 (fun (p: ParameterInfo) (prop: PropertyInfo) -> prop.Name, p.ParameterType) ps props
                  Make = make
                  Read = read
                  // A typed id's: no array per value.
                  Wrap = fun f -> builds.Invoke(null, f)
                  Unwrap = fun v -> getters.[0].Invoke v }
            | _ ->
                match t.GetProperty(name, all) with
                | p when not (isNull p) && not (isNull (p.GetGetMethod true)) && (p.GetGetMethod true).IsStatic && isUnionValue p.PropertyType ->
                    let value = lazy (p.GetValue null)

                    { Name = name
                      Tag = tag
                      Fields = [||]
                      Make = fun _ -> value.Value
                      Read = fun _ -> [||]
                      Wrap = fun _ -> invalidOp $"{t.Name}.{name} has no field"
                      Unwrap = fun _ -> invalidOp $"{t.Name}.{name} has no field" }
                | _ -> fail $"no NewX method or static property for the case {name}"

        let cases = named |> List.map case |> Array.ofList

        // With UseNullAsTrueValue, one case is null and the tag is GetTag's.
        let tag =
            match t.GetMethod("GetTag", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic, [| t |]) with
            | null ->
                match t.GetProperty("Tag", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
                | null when cases.Length = 1 -> fun (_: obj) -> 0
                | null -> fail "it has no Tag"
                | p -> fun (v: obj) -> unbox<int> (p.GetValue v)
            | getTag -> fun (v: obj) -> unbox<int> (getTag.Invoke(null, [| v |]))

        { Type = t
          Private = not (FSharpType.IsUnion(t, false))
          Cases = cases
          Tag = tag }

    let private isFSharpCore (t: Type) = t.Assembly = typeof<option<int>>.Assembly

    /// An F# record.
    let isRecord (t: Type) = FSharpType.IsRecord(t, true)

    /// An F# union, FSharp.Core's own (option, list, Result) included.
    let isUnion (t: Type) = FSharpType.IsUnion(t, true)

    let record (t: Type) : RecordShape = records.GetOrAdd(t, reflectedRecord)

    let union (t: Type) : UnionShape =
        unions.GetOrAdd(
            t,
            fun t ->
                // FSharp.Core's unions keep what FSharp.Reflection reads.
                if reader () = UnionReader.FSharpReflection || isFSharpCore t then reflectedUnion t else structuralUnion t
        )

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

