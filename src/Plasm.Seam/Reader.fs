/// Reads the data assembly's metadata (nothing in it is loaded to run) and
/// works out how each type crosses to C#: what its view is called, and how a
/// domain expression becomes a view expression and back.
namespace Plasm.Seam

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Plasm.Seam.Config

/// A type with each position's nullability, as the compilers write it:
/// 0 unknown, 1 not null, 2 may be null.
type private Node = { Type: Type; Null: byte; Arguments: Node[] }

type private Use =
    | AsField
    | AsParameter
    | AsResult

type private UnionCase =
    { Name: string
      Tag: int
      Holder: Type
      Fields: PropertyInfo list
      Public: bool }

module private Meta =
    let core = "Microsoft.FSharp.Core."
    let collections = "Microsoft.FSharp.Collections."

    let scalars =
        dict
            [ "System.String", "string"
              "System.Boolean", "bool"
              "System.Byte", "byte"
              "System.SByte", "sbyte"
              "System.Int16", "short"
              "System.UInt16", "ushort"
              "System.Int32", "int"
              "System.UInt32", "uint"
              "System.Int64", "long"
              "System.UInt64", "ulong"
              "System.Single", "float"
              "System.Double", "double"
              "System.Decimal", "decimal"
              "System.Char", "char"
              "System.Guid", "global::System.Guid"
              "System.DateTime", "global::System.DateTime"
              "System.DateTimeOffset", "global::System.DateTimeOffset"
              "System.DateOnly", "global::System.DateOnly"
              "System.TimeOnly", "global::System.TimeOnly"
              "System.TimeSpan", "global::System.TimeSpan"
              "System.Uri", "global::System.Uri" ]

    /// Names the seam uses itself, which no generated type may take.
    let reserved =
        set
            [ "FindResult"; "AddResult"; "ChangeResult"; "RequestResult"; "Missing"; "Invalid"; "Refused"; "Stale"; "FieldProblem"
              "SeamMap"; "SeamJson"; "SeamHttp"; "Task"; "Guid" ]

    let declared =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

    /// CompilationMappingAttribute's flags and sequence numbers.
    let mapping (attributes: IList<CustomAttributeData>) : (int * int list) option =
        attributes
        |> Seq.tryPick (fun a ->
            if a.AttributeType.FullName <> core + "CompilationMappingAttribute" || a.ConstructorArguments.Count = 0 then
                None
            else
                match a.ConstructorArguments[0].Value with
                | :? int as flags ->
                    let numbers =
                        a.ConstructorArguments
                        |> Seq.skip 1
                        |> Seq.choose (fun x ->
                            match x.Value with
                            | :? int as n -> Some n
                            | _ -> None)
                        |> List.ofSeq

                    Some(flags, numbers)
                | _ -> None)

    let kind (t: Type) = mapping (t.GetCustomAttributesData()) |> Option.map (fun (f, _) -> f &&& 31)
    let hidden (t: Type) = mapping (t.GetCustomAttributesData()) |> Option.exists (fun (f, _) -> f &&& 32 <> 0)
    let isRecord t = kind t = Some 2
    let isUnion t = kind t = Some 1

    let rec visible (t: Type) =
        t.IsPublic || (t.IsNestedPublic && visible (nonNull t.DeclaringType))

    let dotted (t: Type) = (match t.FullName with null -> t.Name | f -> f).Replace('+', '.')

    let full (t: Type) =
        match (if t.IsGenericType then t.GetGenericTypeDefinition() else t).FullName with
        | null -> ""
        | f -> f

    let is (t: Type) definition = t.IsGenericType && t.GetGenericTypeDefinition().FullName = definition

    let framework (t: Type) =
        match t.Assembly.GetName().Name with
        | null -> false
        | n -> (n.StartsWith "System" || n.StartsWith "Microsoft." || n = "netstandard" || n = "mscorlib") && n <> "FSharp.Core"

    let private tickless (name: string) =
        match name.IndexOf '`' with
        | -1 -> name
        | i -> name.Substring(0, i)

    /// A domain type as C# spells it.
    let rec csName (t: Type) : string =
        if t.IsGenericParameter then
            t.Name
        elif t.IsArray then
            csName (nonNull (t.GetElementType())) + "[]"
        else
            match t.FullName with
            | f when not (isNull f) && scalars.ContainsKey f -> scalars[f]
            | _ ->
                let name = tickless t.Name

                let arguments =
                    if t.IsGenericType then
                        "<" + (t.GetGenericArguments() |> Seq.map csName |> String.concat ", ") + ">"
                    else
                        ""

                match t.DeclaringType with
                | null ->
                    let ns = if String.IsNullOrEmpty t.Namespace then "" else t.Namespace + "."
                    "global::" + ns + name + arguments
                | outer -> csName outer + "." + name + arguments

    let private fsharpNames =
        dict
            [ "System.String", "string"
              "System.Boolean", "bool"
              "System.Byte", "byte"
              "System.SByte", "sbyte"
              "System.Int16", "int16"
              "System.UInt16", "uint16"
              "System.Int32", "int"
              "System.UInt32", "uint"
              "System.Int64", "int64"
              "System.UInt64", "uint64"
              "System.Single", "float32"
              "System.Double", "float"
              "System.Decimal", "decimal"
              "System.Char", "char"
              "System.Object", "obj" ]

    /// A domain type as F# writes it (`int option`, `Result<int, string>`,
    /// `int * string`), for messages and the lock.
    let rec display (t: Type) : string =
        if t.IsGenericParameter then
            "'" + t.Name
        elif t.IsArray then
            display (nonNull (t.GetElementType())) + "[]"
        elif not (isNull t.FullName) && fsharpNames.ContainsKey t.FullName then
            fsharpNames[t.FullName]
        else
            let ns = if String.IsNullOrEmpty t.Namespace then "" else t.Namespace + "."
            let outer = match t.DeclaringType with null -> "" | o -> o.Name + "."
            let name = tickless (ns + outer + t.Name)
            let args = if t.IsGenericType then t.GetGenericArguments() |> Array.map display else [||]
            let definition = if t.IsGenericType then (match t.GetGenericTypeDefinition().FullName with null -> "" | f -> f) else ""

            match definition with
            | "Microsoft.FSharp.Core.FSharpOption`1" -> $"{args[0]} option"
            | "Microsoft.FSharp.Core.FSharpValueOption`1" -> $"{args[0]} voption"
            | "Microsoft.FSharp.Collections.FSharpList`1" -> $"{args[0]} list"
            | "Microsoft.FSharp.Collections.FSharpSet`1" -> $"Set<{args[0]}>"
            | "Microsoft.FSharp.Collections.FSharpMap`2" -> $"Map<{args[0]}, {args[1]}>"
            | "Microsoft.FSharp.Core.FSharpResult`2" -> $"Result<{args[0]}, {args[1]}>"
            | "Microsoft.FSharp.Core.FSharpFunc`2" -> $"{args[0]} -> {args[1]}"
            | d when d.StartsWith "System.Tuple`" -> String.concat " * " args
            | d when d.StartsWith "System.ValueTuple`" -> "struct (" + String.concat " * " args + ")"
            | _ when t.IsGenericType -> name + "<" + String.concat ", " args + ">"
            | _ -> name

    let pascal (name: string) =
        if name.Length = 0 || Char.IsUpper name[0] then name else string (Char.ToUpperInvariant name[0]) + name.Substring 1

    let camel (name: string) =
        if name.Length = 0 || Char.IsLower name[0] then name else string (Char.ToLowerInvariant name[0]) + name.Substring 1

    let unionCases (union: Type) : UnionCase list =
        let found = List<string * int * bool>()

        for m in union.GetMethods declared do
            match mapping (m.GetCustomAttributesData()) with
            | Some(f, [ n ]) when m.IsStatic && f &&& 31 = 8 ->
                let name =
                    if m.Name.StartsWith "New" then m.Name.Substring 3
                    elif m.Name.StartsWith "get_" then m.Name.Substring 4
                    else m.Name

                found.Add((name, n, m.IsPublic))
            | _ -> ()

        for p in union.GetProperties declared do
            match mapping (p.GetCustomAttributesData()) with
            | Some(f, [ n ]) when f &&& 31 = 8 && found |> Seq.forall (fun (_, t, _) -> t <> n) ->
                found.Add((p.Name, n, (match p.GetMethod with null -> false | g -> g.IsPublic)))
            | _ -> ()

        [ for name, tag, isPublic in found |> Seq.sortBy (fun (_, t, _) -> t) do
              let fields = List<int * PropertyInfo * Type>()
              // A case's fields are on a nested class of its name, or on the union itself.
              for holder in Seq.append (union.GetNestedTypes(BindingFlags.Public ||| BindingFlags.NonPublic) |> Seq.filter (fun n -> n.Name = name)) [ union ] do
                  for p in holder.GetProperties declared do
                      match mapping (p.GetCustomAttributesData()) with
                      | Some(f, [ c; order ]) when f &&& 31 = 4 && c = tag -> fields.Add((order, p, holder))
                      | _ -> ()

              let held = if fields.Count > 0 then (let _, _, h = fields[0] in h) else union

              let isOpen =
                  isPublic
                  && fields |> Seq.forall (fun (_, p, _) -> match p.GetMethod with null -> false | g -> g.IsPublic)
                  && (held = union || held.IsNestedPublic)

              { Name = name
                Tag = tag
                Holder = held
                Fields = fields |> Seq.sortBy (fun (o, _, _) -> o) |> Seq.map (fun (_, p, _) -> p) |> List.ofSeq
                Public = isOpen } ]

    // ---- nullability ----

    let nullBytes (attributes: IList<CustomAttributeData>) (name: string) : byte[] option =
        attributes
        |> Seq.tryPick (fun a ->
            if a.AttributeType.FullName <> "System.Runtime.CompilerServices." + name || a.ConstructorArguments.Count <> 1 then
                None
            else
                match a.ConstructorArguments[0].Value with
                | :? byte as b -> Some [| b |]
                | :? IEnumerable<CustomAttributeTypedArgument> as many -> Some(many |> Seq.map (fun x -> x.Value :?> byte) |> Array.ofSeq)
                | _ -> None)

    let rec contextOf (m: MemberInfo | null) : byte[] =
        match m with
        | null -> [||]
        | m ->
            match nullBytes (m.GetCustomAttributesData()) "NullableContextAttribute" with
            | Some b -> b
            | None -> contextOf m.DeclaringType

type private Nulls(bytes: byte[]) =
    let mutable next = 0
    member val Short = false with get, set
    member _.Spare = bytes.Length > 1 && next <> bytes.Length

    member this.Next() : byte =
        if bytes.Length = 0 then 0uy
        elif bytes.Length = 1 then bytes[0]
        elif next < bytes.Length then
            next <- next + 1
            bytes[next - 1]
        else
            this.Short <- true
            0uy

type Reader(config: Config, references: string[], dataPaths: string[], ns: string) =
    let paths =
        Seq.append references dataPaths
        |> Seq.filter File.Exists
        |> Seq.groupBy Path.GetFileName
        |> Seq.map (fun (_, g) -> Seq.head g)

    let context = new MetadataLoadContext(PathAssemblyResolver(paths))

    let data =
        [ for p in dataPaths do
              if File.Exists p then context.LoadFromAssemblyPath p ]

    do
        if data.IsEmpty then
            Seam.fail "SEAM000" "the host references no project, so there is no data assembly to read: add a ProjectReference to the F# data project"

    let seam =
        { Namespace = ns
          Records = List()
          Unions = List()
          Outcomes = List()
          Shapes = List()
          Data = List()
          Refusals = List()
          Docs = Dictionary()
          Json = List()
          HostJson = List() }

    // The F# doc comments, from the XML doc beside each data assembly (a data
    // project that writes one: GenerateDocumentationFile).
    do
        for p in dataPaths do
            let xml = Path.ChangeExtension(p, ".xml")

            if File.Exists xml then
                for m in Xml.Linq.XDocument.Load(xml).Descendants(Xml.Linq.XName.Get "member") do
                    match m.Attribute(Xml.Linq.XName.Get "name"), m.Element(Xml.Linq.XName.Get "summary") with
                    | null, _
                    | _, null -> ()
                    | name, summary ->
                        let text =
                            summary.Nodes() |> Seq.map string |> String.concat "" |> fun t -> Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim()

                        if text <> "" then
                            seam.Docs[name.Value] <- text

    let known = Dictionary<Type, Cross>()
    let names = Dictionary<string, string>()
    let wrappers = Dictionary<Type, string>()
    let json = HashSet<string>()
    let views = Dictionary<Type, ViewConfig>()
    let cases = Dictionary<Type * string, CaseConfig>()
    let fieldsFor = Dictionary<Type, List<FieldConfig>>()
    let mutable fresh = 0

    let nextName () =
        fresh <- fresh + 1
        $"x{fresh}"

    let cannot where (t: Type) why =
        SeamError("SEAM010", $"{where}: {Meta.display t} cannot cross the seam: {why}", None)

    let find (written: string) (at: At) : Type =
        let found =
            data
            |> Seq.collect _.GetTypes()
            |> Seq.filter (fun t -> Meta.dotted t = written || (Meta.dotted t).EndsWith("." + written))
            |> Array.ofSeq

        match found with
        | [| one |] -> one
        | [||] ->
            let dlls = data |> Seq.map (fun a -> a.GetName().Name + ".dll") |> String.concat ", "
            Seam.failAt "SEAM002" at.Origin $"typeof({written}) names no type in {dlls}"
        | many ->
            let all = many |> Seq.map Meta.dotted |> String.concat " or "
            Seam.failAt "SEAM002" at.Origin $"typeof({written}) could be {all}: write its namespace"

    let claim (name: string) (forType: string) (at: At option) =
        let origin = at |> Option.bind _.Origin

        if Meta.reserved.Contains name then
            Seam.failAt "SEAM005" origin $"{forType} would be called {name} in C#, a name the seam itself uses: give it another with Name = \"...\" in Seam.cs"

        match names.TryGetValue name with
        | true, other when other <> forType ->
            Seam.failAt "SEAM005" origin $"{forType} and {other} would both be called {name} in C#: give one another name in Seam.cs (SeamView or SeamCase, Name = \"...\")"
        | _ -> names[name] <- forType

    let rec walk (t: Type) (nulls: Nulls) : Node =
        if t.IsGenericParameter then
            { Type = t; Null = nulls.Next(); Arguments = [||] }
        elif t.IsArray then
            let mark = nulls.Next()
            { Type = t; Null = mark; Arguments = [| walk (nonNull (t.GetElementType())) nulls |] }
        elif Meta.is t "System.Nullable`1" then
            { Type = t; Null = 0uy; Arguments = [| walk (t.GetGenericArguments().[0]) nulls |] }
        else
            let own = if not t.IsValueType || t.IsGenericType then nulls.Next() else 0uy
            let arguments = if t.IsGenericType then t.GetGenericArguments() |> Array.map (fun a -> walk a nulls) else [||]
            { Type = t; Null = own; Arguments = arguments }

    let tree (t: Type) (bytes: byte[]) (where: string) =
        let nulls = Nulls bytes
        let node = walk t nulls

        if nulls.Short || nulls.Spare then
            Seam.fail
                "SEAM012"
                $"{where}: the nullability the F# compiler wrote for {Meta.display t} does not fit the type, so the seam cannot tell which parts may be null; use an option"

        node

    let treeOfParameter (p: ParameterInfo) (m: MethodBase) where =
        tree p.ParameterType (Meta.nullBytes (p.GetCustomAttributesData()) "NullableAttribute" |> Option.defaultWith (fun () -> Meta.contextOf m)) where

    let treeOfProperty (p: PropertyInfo) where =
        let bytes =
            Meta.nullBytes (p.GetCustomAttributesData()) "NullableAttribute"
            |> Option.orElseWith (fun () ->
                match p.GetMethod with
                | null -> None
                | g -> Meta.nullBytes (g.ReturnParameter.GetCustomAttributesData()) "NullableAttribute")
            |> Option.defaultWith (fun () ->
                match p.GetMethod with
                | null -> Meta.contextOf p.DeclaringType
                | g -> Meta.contextOf g)

        tree p.PropertyType bytes where

    let same cs valueType =
        { Cs = cs
          Domain = cs
          Nullable = false
          ValueType = valueType
          Identity = true
          Out = id
          In = Some id
          NoIn = None }

    let rec go (node: Node) (use': Use) (where: string) : Cross =
        let c = core node use' where

        if node.Null <> 2uy || c.Nullable || c.ValueType || node.Type.IsGenericParameter then
            c
        else
            let a = nextName ()

            { c with
                Nullable = true
                Out = if c.Identity then id else fun e -> $"({e} is {{ }} {a} ? {c.Out a} : null)"
                In =
                    c.In
                    |> Option.map (fun into -> if c.Identity then id else fun e -> $"({e} is {{ }} {a} ? {into a} : null)") }

    and core (node: Node) (use': Use) (where: string) : Cross =
        let t = node.Type
        let full = Meta.full t

        if t.IsGenericParameter then
            same t.Name false
        elif t.IsByRef || t.IsPointer then
            raise (cannot where t "a byref or a pointer has no view")
        elif not (isNull t.FullName) && Meta.scalars.ContainsKey t.FullName then
            same Meta.scalars[t.FullName] t.IsValueType
        elif t.IsEnum then
            same (Meta.csName t) true
        elif t.IsArray then
            many node node.Arguments[0] use' where "array"
        elif Meta.is t "System.Nullable`1" then
            let c = go node.Arguments[0] use' where
            let v = nextName ()

            { Cs = c.Cs
              Domain = Meta.csName t
              Nullable = true
              ValueType = true
              Identity = c.Identity
              Out = if c.Identity then id else fun e -> $"({e} is {{ }} {v} ? ({c.Cs}?){c.Out v} : null)"
              In = c.In |> Option.map (fun into -> if c.Identity then id else fun e -> $"({e} is {{ }} {v} ? ({c.Domain}?){into v} : null)")
              NoIn = c.NoIn }
        elif Meta.is t (Meta.core + "FSharpOption`1") || Meta.is t (Meta.core + "FSharpValueOption`1") then
            let c = go node.Arguments[0] use' where

            if c.Nullable then
                raise (
                    cannot where t "an option of something that may itself be null (an option, a nullable) would be one null in C#, and none could not be told from some of none"
                )

            let v = nextName ()
            let domain = Meta.csName t
            let isValue = Meta.is t (Meta.core + "FSharpValueOption`1")

            { Cs = c.Cs
              Domain = domain
              Nullable = true
              ValueType = c.ValueType
              Identity = false
              Out =
                if isValue then
                    fun e -> $"""({e}.IsSome ? ({c.Cs}?){c.Out(e + ".Value")} : null)"""
                else
                    fun e -> $"""({e} is {{ }} {v} ? ({c.Cs}?){c.Out(v + ".Value")} : null)"""
              In = c.In |> Option.map (fun into e -> $"({e} is {{ }} {v} ? {domain}.Some({into v}) : {domain}.None)")
              NoIn = c.NoIn }
        elif Meta.is t (Meta.collections + "FSharpList`1") then
            many node node.Arguments[0] use' where "list"
        elif Meta.is t (Meta.collections + "FSharpSet`1") then
            many node node.Arguments[0] use' where "set"
        elif
            Meta.is t "System.Collections.Generic.IEnumerable`1"
            || Meta.is t "System.Collections.Generic.IReadOnlyList`1"
            || Meta.is t "System.Collections.Generic.IReadOnlyCollection`1"
            || Meta.is t "System.Collections.Generic.List`1"
        then
            many node node.Arguments[0] use' where (if t.Name.StartsWith "List" then "List" else "seq")
        elif full.StartsWith(Meta.core + "FSharpResult`") then
            raise (
                cannot where t "a Result has no C# shape; return one of Plasm.CSharp's outcomes (FindResult, AddResult, ChangeResult, RequestResult), which Outcome.found, added, changed and answered make from a Result"
            )
        elif full.StartsWith(Meta.collections + "FSharpMap`") then
            raise (cannot where t "a Map has no view; use a list of records")
        elif full.StartsWith "System.Tuple`" || full.StartsWith "System.ValueTuple`" then
            raise (cannot where t "a tuple's parts have no names; use a record")
        elif full.StartsWith(Meta.core + "FSharpFunc`") then
            raise (cannot where t "an F# function is not something C# passes; take a System.Func or System.Action")
        elif full.StartsWith "System.Threading.Tasks." && use' <> AsParameter && t.Name.Contains "Task" then
            raise (cannot where t "a Task is awaited only as a method's whole result")
        else
            match wrappers.TryGetValue t with
            | true, wrapper ->
                if use' = AsField then
                    raise (cannot where t "a data type is not part of a view")

                { Cs = wrapper
                  Domain = Meta.csName t
                  Nullable = false
                  ValueType = false
                  Identity = false
                  Out = fun e -> $"new {wrapper}({e})"
                  In = Some(fun e -> $"{e}.Inner")
                  NoIn = None }
            | _ ->
                match known.TryGetValue t with
                | true, seen -> seen
                | _ ->
                    if (match t.BaseType with null -> false | b -> b.FullName = "System.MulticastDelegate") then
                        delegateOf node use' where
                    elif Meta.isRecord t then
                        record t where (use' = AsParameter)
                    elif Meta.isUnion t then
                        union t where
                    elif Meta.full t = "Plasm.CSharp.ChangeResult`2" then
                        refusable node use' where
                    elif t.GetCustomAttributesData() |> Seq.exists (fun a -> a.AttributeType.FullName = "System.Runtime.CompilerServices.UnionAttribute") then
                        outcome node use' where
                    elif Meta.framework t then
                        if use' = AsField then
                            raise (
                                cannot where t "the seam knows no JSON for it; a view's field is a string, a number, a bool, a Guid, a date or time, an enum, a record, a union, or an option, list or array of those"
                            )

                        let arguments = node.Arguments |> Array.map (fun a -> go a use' where)

                        if arguments |> Array.forall _.Identity then
                            same (Meta.csName t) t.IsValueType
                        else
                            raise (cannot where t "it holds a domain type the seam would have to map inside a type it does not know")
                    elif full.StartsWith "Microsoft.FSharp." then
                        raise (cannot where t "this FSharp.Core type has no view; the seam maps option, voption, list, Set, arrays, records and unions")
                    else
                        raise (
                            cannot
                                where
                                t
                                (if Meta.kind t |> Option.isNone then
                                     "it is neither an F# record nor an F# union, so the seam cannot see its shape"
                                 else
                                     "it is an F# class, module or exception, not a record or a union")
                        )

    and many (node: Node) (element: Node) (use': Use) (where: string) (shape: string) : Cross =
        let c = go element use' where
        let x = nextName ()
        let domain = Meta.csName node.Type
        let isArray = shape = "array"
        let items e each = $"SeamMap.Each({e}, {x} => {each x})"

        let into: (string -> string) option =
            match c.In with
            | None -> None
            | Some cin ->
                match shape with
                | "array" -> Some(if c.Identity then id else fun e -> $"SeamMap.Each<{c.CsFull}, {c.Domain}>({e}, {x} => {cin x})")
                | "list" ->
                    Some(
                        if c.Identity then
                            fun e -> $"global::Microsoft.FSharp.Collections.ListModule.OfSeq<{c.Domain}>({e})"
                        else
                            fun e -> $"global::Microsoft.FSharp.Collections.ListModule.OfSeq<{c.Domain}>(SeamMap.Each<{c.CsFull}, {c.Domain}>({e}, {x} => {cin x}))"
                    )
                | "set" -> if c.Identity then Some(fun e -> $"global::Microsoft.FSharp.Collections.SetModule.OfSeq<{c.Domain}>({e})") else None
                | "List" -> if c.Identity then Some(fun e -> $"new {domain}({e})") else None
                | _ -> Some(if c.Identity then id else fun e -> $"SeamMap.Each<{c.CsFull}, {c.Domain}>({e}, {x} => {cin x})")

        { Cs = c.CsFull + "[]"
          Domain = domain
          Nullable = false
          ValueType = false
          Identity = isArray && c.Identity
          Out =
            if isArray && c.Identity then id
            elif c.Identity then fun e -> $"SeamMap.Each({e}, {x} => {x})"
            else fun e -> items e c.Out
          In = into
          NoIn =
            if into.IsNone then
                Some(c.NoIn |> Option.defaultValue $"a {shape} of {c.Cs} is only returned")
            else
                None }

    and delegateOf (node: Node) (use': Use) (where: string) : Cross =
        let t = node.Type
        let arguments = node.Arguments

        if use' <> AsParameter then
            raise (cannot where t "a delegate is only a parameter")

        let full = Meta.full t
        let isFunc = full.StartsWith "System.Func`"

        if not isFunc && not (full.StartsWith "System.Action") then
            if arguments.Length = 0 then
                same (Meta.csName t) false
            else
                raise (cannot where t "of delegates the seam maps System.Func and System.Action")
        else
            // What the delegate is given goes out to C#; what it answers comes in.
            let given = (if isFunc then arguments[.. arguments.Length - 2] else arguments) |> Array.map (fun a -> go a AsResult where)
            let answer = if isFunc then Some(answerOf arguments[arguments.Length - 1] where) else None

            if given |> Array.forall _.Identity && answer |> Option.forall _.Identity then
                same (Meta.csName t) false
            elif answer |> Option.exists (fun a -> not a.Identity) then
                raise (
                    cannot where t "what the delegate answers would need mapping back into the domain; let it answer plain values or a type parameter"
                )
            else
                let names = given |> Array.map (fun _ -> nextName ())

                let cs =
                    (if isFunc then "global::System.Func<" else "global::System.Action<")
                    + (Seq.append (given |> Seq.map _.CsFull) (answer |> Option.map _.CsFull |> Option.toList) |> String.concat ", ")
                    + ">"

                let call = Array.zip given names |> Array.map (fun (g, n) -> g.Out n) |> String.concat ", "

                { Cs = cs
                  Domain = Meta.csName t
                  Nullable = false
                  ValueType = false
                  Identity = false
                  Out = fun _ -> raise (cannot where t "a delegate is only a parameter")
                  In = Some(fun e -> $"""({String.concat ", " names}) => {e}({call})""")
                  NoIn = None }

    /// What a delegate answers: a plain type, or a Task of one.
    and answerOf (node: Node) (where: string) : Cross =
        if Meta.is node.Type "System.Threading.Tasks.Task`1" || Meta.is node.Type "System.Threading.Tasks.ValueTask`1" then
            let inner = go node.Arguments[0] AsParameter where
            let name = Meta.csName (node.Type.GetGenericTypeDefinition())
            let name = name.Substring(0, name.IndexOf '<')

            { Cs = $"{name}<{inner.CsFull}>"
              Domain = Meta.csName node.Type
              Nullable = false
              ValueType = false
              Identity = inner.Identity
              Out = id
              In = Some id
              NoIn = None }
        else
            go node AsParameter where

    /// A record crosses as one C# record both ways: answered as its view, and,
    /// when C# can give every field, taken as a parameter. One met first as a
    /// parameter keeps its F# name (NewBooking); one met first as an answer
    /// is called a view (EquipmentView).
    and record (t: Type) (where: string) (asInput: bool) : Cross =
        if not (Meta.visible t) then
            raise (cannot where t "the record is not public, so C# cannot read it")

        if Meta.hidden t then
            Seam.fail
                "SEAM011"
                $"{where}: {Meta.display t} keeps its fields private, so the seam cannot read them to write a view: make the record's fields public, or return another record made from it"

        if t.IsGenericType then
            raise (cannot where t "a generic record has no view yet; return a record written for the purpose")

        let view =
            match views.TryGetValue t with
            | true, v ->
                v.Used <- true
                Some v
            | _ -> None

        let name =
            view
            |> Option.bind _.Name
            |> Option.defaultValue (if asInput then t.Name else t.Name + "View")

        claim name (Meta.display t) (view |> Option.map _.At)
        let fields = List<Field>()

        let cross =
            { Cs = name
              Domain = Meta.csName t
              Nullable = false
              ValueType = false
              Identity = false
              Out = fun e -> $"SeamMap.View({e})"
              In = None
              NoIn =
                Some
                    $"{name} is a view of the record {Meta.display t}, and a view is only returned; take the record's parts as plain arguments" }

        known[t] <- cross

        let recordView =
            { Name = name
              Domain = Meta.csName t
              Display = Meta.display t
              Fields = fields
              Into = None }

        seam.Records.Add recordView

        json.Add name |> ignore

        let declared =
            t.GetProperties Meta.declared
            |> Array.map (fun p ->
                let m = Meta.mapping (p.GetCustomAttributesData())
                p, m)

        let recordFields =
            declared
            |> Array.choose (fun (p, m) ->
                match m with
                | Some(f, [ n ]) when f &&& 31 = 4 -> Some(n, p)
                | _ -> None)
            |> Array.sortBy fst

        for _, p in recordFields do
            match view with
            | Some v when v.Omit.Contains p.Name -> v.OmitUsed.Add p.Name |> ignore
            | _ ->
                let at = $"{Meta.display t}.{p.Name}"

                fields.Add
                    { Name = Meta.pascal p.Name
                      Member = p.Name
                      Cross = go (treeOfProperty p at) AsField at }

        let computed (m: string) =
            declared
            |> Array.tryPick (fun (p, flags) ->
                if p.Name = m && flags.IsNone && p.GetIndexParameters().Length = 0 then
                    match p.GetMethod with
                    | null -> None
                    | g when g.IsPublic && not g.IsStatic -> Some p
                    | _ -> None
                else
                    None)

        for f in (match fieldsFor.TryGetValue t with
                  | true, l -> List.ofSeq l
                  | _ -> []) do
            f.Used <- true
            let whole = $"{Meta.display t}.{f.Member}"

            let place (field: Field) =
                match f.After, f.Before with
                | None, None -> fields.Add field
                | Some other, _
                | _, Some other ->
                    let side = if f.After.IsSome then "after" else "before"

                    match fields |> Seq.tryFindIndex (fun x -> x.Member = other) with
                    | Some i -> fields.Insert((if f.After.IsSome then i + 1 else i), field)
                    | None -> Seam.failAt "SEAM003" f.At.Origin $"SeamField places {whole} {side} {other}, which {name} does not have"

            match fields |> Seq.tryFindIndex (fun x -> x.Member = f.Member) with
            | Some i ->
                if f.After.IsNone && f.Before.IsNone then
                    Seam.failAt "SEAM003" f.At.Origin $"SeamField names {whole}, a field {name} has already: give After or Before to place it"

                let field = fields[i]
                fields.RemoveAt i
                place field
            | None ->
                if recordFields |> Array.exists (fun (_, p) -> p.Name = f.Member) then
                    Seam.failAt "SEAM003" f.At.Origin $"SeamField names {whole}, which SeamView omits from {name}"

                match computed f.Member with
                | Some p ->
                    if fields |> Seq.exists (fun x -> x.Name = Meta.pascal f.Member) then
                        Seam.failAt "SEAM003" f.At.Origin $"SeamField adds {whole}, but {name} already has a field of that name"

                    place
                        { Name = Meta.pascal f.Member
                          Member = f.Member
                          Cross = go (treeOfProperty p whole) AsField whole }
                | None ->
                    Seam.failAt
                        "SEAM003"
                        f.At.Origin
                        $"SeamField names {whole}, which is neither a field nor a public member of the record: for a computed field, write `member r.{f.Member} = ...` on it"

        if fields.Count = 0 then
            Seam.failAt "SEAM003" (view |> Option.bind _.At.Origin) $"{Meta.display t}'s view {name} would have no field left"

        // Made from C#: every field of the record, in its order, from the view.
        let recordNames = recordFields |> Array.map (fun (_, p) -> p.Name)
        let byMember = fields |> Seq.map (fun f -> f.Member, f) |> dict

        let lacking =
            [ for n in recordNames do
                  match byMember.TryGetValue n with
                  | true, f when f.Cross.In.IsSome -> ()
                  | true, f -> yield $"{n} cannot come from C# ({f.Cross.NoIn |> Option.defaultValue String.Empty})"
                  | _ -> yield $"SeamView leaves out {n}"
              for f in fields do
                  if not (Array.contains f.Member recordNames) then
                      yield $"{f.Member} is computed" ]

        // Written once a parameter takes it, so that a record only answered
        // gets no mapping back; taking it takes each record inside it too.
        let take () =
            if recordView.Into.IsNone then
                recordView.Into <- Some [ for n in recordNames -> byMember[n].Cross.In.Value $"value.{byMember[n].Name}" ]

        let crossed =
            if lacking.IsEmpty then
                { cross with
                    In =
                        Some(fun e ->
                            take ()
                            $"SeamMap.Domain({e})")
                    NoIn = None }
            else
                { cross with
                    NoIn = Some $"""{name} cannot be made from C#, so it cannot be a parameter: {String.concat "; " lacking}""" }

        known[t] <- crossed
        crossed

    and union (t: Type) (where: string) : Cross =
        if not (Meta.visible t) then
            raise (cannot where t "the union is not public, so C# cannot read it")

        if t.IsGenericType then
            raise (cannot where t "a generic union has no view yet")

        let found = Meta.unionCases t
        let hidden = Meta.hidden t || found |> List.exists (fun c -> not c.Public)

        let value =
            match t.GetProperty("Value", BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) with
            | null -> None
            | p when (match p.GetMethod with null -> false | g -> g.IsPublic) -> Some p
            | _ -> None

        match found with
        // One case holding one value: a typed id, seen as what it holds.
        | [ { Fields = [ only ] } as single ] when not hidden ->
            let at = $"{Meta.display t}.{single.Name}"
            let c = go (treeOfProperty only at) AsField at

            let cross =
                { c with
                    Domain = Meta.csName t
                    Identity = false
                    Out = fun e -> c.Out $"{e}.{only.Name}"
                    In = c.In |> Option.map (fun into e -> $"{Meta.csName t}.New{single.Name}({into e})") }

            known[t] <- cross
            cross
        | _ when hidden && value.IsSome ->
            // Its cases private, and a public Value: a validated value, seen as its Value.
            let at = $"{Meta.display t}.Value"
            let v = go (treeOfProperty value.Value at) AsField at

            let cross =
                { v with
                    Domain = Meta.csName t
                    Identity = false
                    Out = fun e -> v.Out $"{e}.Value"
                    In = None
                    NoIn =
                        Some
                            $"{Meta.display t} keeps its case private, so only its own create function makes one: take the plain value ({v.Cs}) as the parameter and call create in the method" }

            known[t] <- cross
            cross
        | _ when hidden ->
            Seam.fail
                "SEAM011"
                $"{where}: {Meta.display t} keeps its cases private, so the seam cannot read them: make the cases public, or (for a validated value) add a public `member x.Value = ...`, which the seam shows in its place"
        | _ ->
            let view =
                match views.TryGetValue t with
                | true, v ->
                    v.Used <- true

                    if v.Omit.Count > 0 then
                        Seam.failAt "SEAM003" v.At.Origin $"SeamView has Omit for {v.Type}, which is a union: Omit is for a record's fields"

                    Some v
                | _ -> None

            let name = view |> Option.bind _.Name |> Option.defaultValue (t.Name + "View")
            claim name (Meta.display t) (view |> Option.map _.At)
            let list = List<CaseView>()

            let cross =
                { Cs = name
                  Domain = Meta.csName t
                  Nullable = false
                  ValueType = true
                  Identity = false
                  Out = fun e -> $"SeamMap.View({e})"
                  In = None
                  NoIn = Some $"{name} is a view of the union {Meta.display t}, and a view is only returned; take plain arguments" }

            known[t] <- cross

            seam.Unions.Add
                { Name = name
                  Domain = Meta.csName t
                  Display = Meta.display t
                  Cases = list }

            json.Add name |> ignore
            let kinds = Dictionary<string, string>()

            for c in found do
                let configured =
                    match cases.TryGetValue((t, c.Name)) with
                    | true, x ->
                        x.Used <- true
                        Some x
                    | _ -> None

                let caseName = configured |> Option.bind _.Name |> Option.defaultValue c.Name
                claim caseName $"{Meta.display t}.{c.Name}" (configured |> Option.map _.At)
                let kind = configured |> Option.bind _.Kind |> Option.defaultValue (Meta.camel caseName)

                if not (kinds.TryAdd(kind, c.Name)) then
                    Seam.failAt
                        "SEAM005"
                        (configured |> Option.bind _.At.Origin)
                        $"{Meta.display t}.{c.Name} and {Meta.display t}.{kinds[kind]} would both have the JSON kind \"{kind}\": give one another with SeamCase's Kind"

                let fields = List<Field>()

                for p in c.Fields do
                    let at = $"{Meta.display t}.{c.Name}.{p.Name}"

                    if Meta.pascal p.Name = "Kind" then
                        Seam.fail
                            "SEAM005"
                            $"{at}: a case's field cannot be called kind, which is the name of the JSON property that says which case it is; rename the field"

                    fields.Add
                        { Name = Meta.pascal p.Name
                          Member = p.Name
                          Cross = go (treeOfProperty p at) AsField at }

                list.Add
                    { Name = caseName
                      Case = c.Name
                      Kind = kind
                      DomainCase = if found.Length = 1 then "" else $"{Meta.csName t}.Tags.{c.Name}"
                      Cast = if not c.Fields.IsEmpty && c.Holder <> t then Some(Meta.csName c.Holder) else None
                      Fields = fields }

                json.Add caseName |> ignore

            cross

    /// A change whose refusal is the domain's own (ChangeResult<'T, 'R>): C# is
    /// given ChangeResult of the view, the refusal a Refused (SeamMap.Refusal).
    and refusable (node: Node) (use': Use) (where: string) : Cross =
        let t = node.Type

        if use' = AsField then
            raise (cannot where t "an outcome is what a method returns, not a view's field")

        let arguments = t.GetGenericArguments()
        let value = go node.Arguments[0] AsResult where

        if value.Nullable then
            raise (cannot where t "a case of an outcome cannot be null")

        let refusal = refusalOf arguments[1] where
        let one = nonNull (t.Assembly.GetType "Plasm.CSharp.ChangeResult`1")
        let cs = $"global::Plasm.CSharp.ChangeResult<{value.CsFull}>"

        if seam.Shapes |> Seq.forall (fun s -> s.Cs <> "global::Plasm.CSharp.ChangeResult") then
            seam.Shapes.Add
                { Name = "ChangeResult"
                  Cs = "global::Plasm.CSharp.ChangeResult"
                  Cases =
                    [ for c in one.GetConstructors() do
                          match c.GetParameters() with
                          | [| p |] -> (if p.ParameterType.IsGenericParameter then None else Some(Meta.csName p.ParameterType)), p.ParameterType.Name
                          | _ -> () ] }

        let arms =
            [ for c in t.GetConstructors() do
                  match c.GetParameters() with
                  | [| p |] when p.ParameterType = arguments[0] -> Meta.csName p.ParameterType, value
                  | [| p |] when p.ParameterType = arguments[1] -> Meta.csName p.ParameterType, refusal
                  | [| p |] ->
                      json.Add(Meta.csName p.ParameterType) |> ignore
                      Meta.csName p.ParameterType, same (Meta.csName p.ParameterType) false
                  | _ -> () ]

        json.Add "global::Plasm.CSharp.Refused" |> ignore

        if seam.Outcomes |> Seq.forall (fun o -> o.Domain <> Meta.csName t) then
            seam.Outcomes.Add { Cs = cs; Domain = Meta.csName t; Arms = arms }

        { Cs = cs
          Domain = Meta.csName t
          Nullable = false
          ValueType = true
          Identity = false
          Out = fun e -> $"SeamMap.View({e})"
          In = None
          NoIn = Some "an outcome is only returned" }

    /// The domain's refusal, which C# is given as a Refused: an F# union with
    /// a public Message, each case's name its code.
    and refusalOf (t: Type) (where: string) : Cross =
        let refusedCross =
            { Cs = "global::Plasm.CSharp.Refused"
              Domain = Meta.csName t
              Nullable = false
              ValueType = false
              Identity = false
              Out = fun e -> $"SeamMap.Refusal({e})"
              In = None
              NoIn = Some "a refusal is only returned" }

        if seam.Refusals |> Seq.exists (fun r -> r.Domain = Meta.csName t) then
            refusedCross
        else
            if not (Meta.isUnion t) || not (Meta.visible t) then
                Seam.fail
                    "SEAM013"
                    $"{where}: {Meta.display t} is the refusal of a ChangeResult, and a refusal is a public F# union, whose cases' names are the codes C# is given"

            let message =
                match t.GetProperty("Message", BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> None
                | p when p.PropertyType.FullName = "System.String" && p.GetIndexParameters().Length = 0 -> Some p
                | _ -> None

            if message.IsNone then
                Seam.fail
                    "SEAM013"
                    $"{where}: {Meta.display t} is the refusal of a ChangeResult, and has no Message: write `member r.Message = ...` on it, the words C# is given with each refusal's code"

            seam.Refusals.Add
                { Domain = Meta.csName t
                  Display = Meta.display t
                  Codes = Meta.unionCases t |> List.map _.Name }

            refusedCross

    and outcome (node: Node) (use': Use) (where: string) : Cross =
        let t = node.Type

        if use' = AsField then
            raise (cannot where t "an outcome is what a method returns, not a view's field")

        let definition = if t.IsGenericType then t.GetGenericTypeDefinition() else t
        let opened = definition.GetConstructors() |> Array.filter (fun c -> c.GetParameters().Length = 1)
        let closed = t.GetConstructors() |> Array.filter (fun c -> c.GetParameters().Length = 1)

        if closed.Length = 0 then
            raise (cannot where t "it is marked as a union but has no case")

        let arms = List<string * Cross>()
        let shape = List<string option * string>()

        for i in 0 .. closed.Length - 1 do
            let p = closed[i].GetParameters().[0]
            let isOpen = opened[i].GetParameters().[0].ParameterType.IsGenericParameter

            let c =
                if not isOpen && p.ParameterType.Assembly = t.Assembly && not p.ParameterType.IsGenericType then
                    // The outcome's own cases (Missing, Refused, ...) cross as they are.
                    let c = same (Meta.csName p.ParameterType) false
                    json.Add c.Cs |> ignore
                    c
                else
                    let c = go (treeOfParameter p closed[i] where) AsResult where

                    if c.Nullable then
                        raise (cannot where t "a case of an outcome cannot be null")

                    if c.Cs.EndsWith "[]" then
                        json.Add c.Cs |> ignore

                    c

            arms.Add((Meta.csName p.ParameterType, c))
            shape.Add(((if isOpen then None else Some(Meta.csName p.ParameterType)), p.ParameterType.Name))

        let name =
            let n = Meta.csName definition
            if n.Contains '<' then n.Substring(0, n.IndexOf '<') else n

        if seam.Shapes |> Seq.forall (fun s -> s.Cs <> name) then
            seam.Shapes.Add
                { Name = definition.Name.Split('`')[0]
                  Cs = name
                  Cases = List.ofSeq shape }

        if arms |> Seq.forall (fun (_, c) -> c.Identity) then
            same (Meta.csName t) t.IsValueType
        else
            let viewArguments =
                t.GetGenericArguments()
                |> Array.map (fun argument ->
                    match closed |> Array.tryFindIndex (fun c -> c.GetParameters().[0].ParameterType = argument) with
                    | Some i -> (snd arms[i]).CsFull
                    | None -> Meta.csName argument)

            let cs = $"""{name}<{String.concat ", " viewArguments}>"""

            if seam.Outcomes |> Seq.forall (fun o -> o.Domain <> Meta.csName t) then
                seam.Outcomes.Add
                    { Cs = cs
                      Domain = Meta.csName t
                      Arms = List.ofSeq arms }

            { Cs = cs
              Domain = Meta.csName t
              Nullable = false
              ValueType = t.IsValueType
              Identity = false
              Out = fun e -> $"SeamMap.View({e})"
              In = None
              NoIn = Some "an outcome is only returned" }

    let result (node: Node) (where: string) : Returns * Cross option =
        let t = node.Type
        let unit (x: Type) = x.FullName = "System.Void" || x.FullName = Meta.core + "Unit"

        if unit t then
            Nothing, None
        elif Meta.full t = "System.Threading.Tasks.Task" then
            Task, None
        else
            let isTask = Meta.is t "System.Threading.Tasks.Task`1"

            if isTask || Meta.is t "System.Threading.Tasks.ValueTask`1" then
                if unit node.Arguments[0].Type then
                    if isTask then
                        Task, None
                    else
                        raise (cannot where t "return a Task where nothing is answered")
                else
                    let inner = go node.Arguments[0] AsResult where

                    if inner.Cs.EndsWith "[]" then
                        json.Add inner.Cs |> ignore

                    (if isTask then TaskOf else ValueTaskOf), Some inner
            else
                let c = go node AsResult where

                if c.Cs.EndsWith "[]" && not c.Identity then
                    json.Add c.Cs |> ignore

                Value, Some c

    let dataType (t: Type) : DataClass =
        let members = List<Member>()

        for m in t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly) do
            if not m.IsSpecialName then
                fresh <- 0
                let where = $"{Meta.display t}.{m.Name}"
                let typeParameters = if m.IsGenericMethodDefinition then m.GetGenericArguments() else [||]
                let mutable constraints = ""

                for p in typeParameters do
                    let flags = p.GenericParameterAttributes &&& GenericParameterAttributes.SpecialConstraintMask
                    let others = p.GetGenericParameterConstraints() |> Array.filter (fun c -> c.FullName <> "System.ValueType")
                    let has (f: GenericParameterAttributes) = flags &&& f <> GenericParameterAttributes.None

                    if
                        others.Length > 0
                        || (has GenericParameterAttributes.DefaultConstructorConstraint
                            && not (has GenericParameterAttributes.NotNullableValueTypeConstraint))
                    then
                        Seam.fail
                            "SEAM010"
                            $"{where}: the type parameter '{p.Name} has a constraint the seam does not carry to C#; leave it unconstrained, or constrain it to struct or not struct"

                    if has GenericParameterAttributes.NotNullableValueTypeConstraint then
                        constraints <- constraints + $" where {p.Name} : struct"
                    elif has GenericParameterAttributes.ReferenceTypeConstraint then
                        constraints <- constraints + $" where {p.Name} : class"

                let parameters =
                    [ for p in m.GetParameters() do
                          let at = $"{where}'s parameter {p.Name}"
                          let c = go (treeOfParameter p m at) AsParameter at

                          if c.In.IsNone then
                              Seam.fail "SEAM010" $"{at}: {Meta.display p.ParameterType} cannot be a parameter: {c.NoIn |> Option.defaultValue String.Empty}"

                          (match p.Name with null -> "_" | n -> n), c ]

                let returns, res = result (treeOfParameter m.ReturnParameter m (where + "'s result")) (where + "'s result")

                members.Add
                    { Static = m.IsStatic
                      Property = false
                      Name = m.Name
                      TypeParameters = typeParameters |> Array.map _.Name |> List.ofArray
                      Constraints = constraints
                      Parameters = parameters
                      Returns = returns
                      Result = res }

        for p in t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly) do
            match p.GetMethod with
            | null -> ()
            | getter when getter.IsPublic && p.GetIndexParameters().Length = 0 ->
                fresh <- 0
                let where = $"{Meta.display t}.{p.Name}"

                members.Add
                    { Static = getter.IsStatic
                      Property = true
                      Name = p.Name
                      TypeParameters = []
                      Constraints = ""
                      Parameters = []
                      Returns = Value
                      Result = Some(go (treeOfProperty p where) AsResult where) }
            | _ -> ()

        { Name = wrappers[t]
          Domain = Meta.csName t
          Display = Meta.display t
          Members = List.ofSeq members }

    member _.Read() : SeamModel =
        for v in config.Views do
            views[find v.Type v.At] <- v

        for f in config.Fields do
            let r = find f.Record f.At

            if not (Meta.isRecord r) then
                Seam.failAt "SEAM003" f.At.Origin $"SeamField names {Meta.dotted r}, which is not an F# record"

            match fieldsFor.TryGetValue r with
            | true, l -> l.Add f
            | _ -> fieldsFor[r] <- List [ f ]

        for c in config.Cases do
            let u = find c.Union c.At

            if not (Meta.isUnion u) then
                Seam.failAt "SEAM003" c.At.Origin $"SeamCase names {Meta.dotted u}, which is not an F# union"

            cases[(u, c.Case)] <- c

        let roots = List<Type>()

        for d in config.Data do
            let t = find d.Type d.At

            if not t.IsPublic then
                Seam.failAt "SEAM003" d.At.Origin $"SeamData names {Meta.dotted t}, which is not public"

            let name = d.Name |> Option.defaultValue t.Name

            if t.Namespace = seam.Namespace && name = t.Name then
                Seam.failAt
                    "SEAM005"
                    d.At.Origin
                    $"the C# class for {Meta.dotted t} would have its own name in its own namespace ({seam.Namespace}): give SeamData a Name, or set the PlasmSeamNamespace property"

            claim name (Meta.dotted t) (Some d.At)
            wrappers[t] <- name
            roots.Add t

        for t in roots do
            seam.Data.Add(dataType t)

        for v in config.Views do
            if not v.Used then
                Seam.failAt "SEAM004" v.At.Origin $"SeamView names {v.Type}, which no data method returns, so it has no view: remove it, or check the type"

            for o in v.Omit do
                if not (v.OmitUsed.Contains o) then
                    Seam.failAt "SEAM003" v.At.Origin $"SeamView omits {v.Type}.{o}, which is not a field of the record"

        for f in config.Fields do
            if not f.Used then
                Seam.failAt "SEAM004" f.At.Origin $"SeamField names {f.Record}, which no data method returns, so it has no view: remove it, or check the type"

        for c in config.Cases do
            if not c.Used then
                let isCase = Meta.unionCases (find c.Union c.At) |> List.exists (fun u -> u.Name = c.Case)

                if isCase then
                    Seam.failAt "SEAM004" c.At.Origin $"SeamCase names {c.Union}.{c.Case}, a union no data method returns: remove it, or check the type"
                else
                    Seam.failAt "SEAM003" c.At.Origin $"SeamCase names {c.Union}.{c.Case}, which is not a case of the union"

        seam.Json.AddRange(json |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)))
        seam.HostJson.AddRange config.Json
        seam

    interface IDisposable with
        member _.Dispose() = context.Dispose()
