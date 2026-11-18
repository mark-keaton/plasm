/// Plasm's build step (docs/aot.md): reads the F# records and unions of the
/// assemblies an application references that use Plasm.Annotations (its
/// domain), from their metadata, and writes the F# that registers their
/// shapes with Plasm, so that a native binary needs no reflection over them.
///
/// Usage: Plasm.Build <output.fs> <file listing the referenced assemblies>
module Plasm.Build.Program

open System
open System.IO
open System.Reflection
open System.Text

// SourceConstructFlags, as F# writes them in CompilationMappingAttribute.
let private sumType = 1
let private recordType = 2
let private field = 4
let private unionCase = 8
let private kindMask = 31
let private nonPublicRepresentation = 32

/// The CompilationMapping attribute's arguments on a member, if it has one.
let private mapping (m: MemberInfo) : int list option =
    m.GetCustomAttributesData()
    |> Seq.tryFind (fun a -> a.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationMappingAttribute")
    |> Option.map (fun a -> a.ConstructorArguments |> Seq.map (fun x -> Convert.ToInt32 x.Value) |> List.ofSeq)

let private kind (t: Type) =
    match mapping t with
    | Some(flags :: _) -> Some(flags &&& kindMask, flags &&& nonPublicRepresentation <> 0)
    | _ -> None

let private identifier (s: string) = "``" + s + "``"

/// A type as F# source names it, from the global namespace.
let rec private name (t: Type) : string =
    if t.IsArray then
        name (t.GetElementType()) + "[]"
    elif t.IsGenericType then
        let def = t.GetGenericTypeDefinition().FullName
        let args = t.GetGenericArguments() |> Array.map name

        match def with
        | "Microsoft.FSharp.Core.FSharpOption`1" -> "(" + args.[0] + " option)"
        | "Microsoft.FSharp.Collections.FSharpList`1" -> "(" + args.[0] + " list)"
        | _ when def.StartsWith "System.Tuple`" -> "(" + String.Join(" * ", args) + ")"
        | _ ->
            let plain = t.Name.Substring(0, t.Name.IndexOf '`')
            path t + identifier plain + "<" + String.Join(", ", args) + ">"
    else
        path t + identifier (sourceName t)

/// A module compiled with a Module suffix keeps its source name in F#.
and private sourceName (t: Type) =
    match kind t with
    | Some(7, _) when t.Name.EndsWith "Module" ->
        let stripped = t.Name.Substring(0, t.Name.Length - "Module".Length)
        let siblings = if isNull t.DeclaringType then t.Assembly.GetTypes() |> Array.filter (fun s -> s.Namespace = t.Namespace && isNull s.DeclaringType) else t.DeclaringType.GetNestedTypes()
        if siblings |> Array.exists (fun s -> s.Name = stripped) then stripped else t.Name
    | _ -> t.Name

and private path (t: Type) =
    if not (isNull t.DeclaringType) then
        path t.DeclaringType + identifier (sourceName t.DeclaringType) + "."
    elif String.IsNullOrEmpty t.Namespace then
        "global."
    else
        "global." + String.Join(".", t.Namespace.Split('.') |> Array.map identifier) + "."

/// Public all the way out: generated code outside the assembly can name it.
let rec private visible (t: Type) =
    (t.IsPublic || t.IsNestedPublic) && (isNull t.DeclaringType || visible t.DeclaringType)

type private Shape =
    | Record of Type * (string * Type) list
    | Union of Type * isPrivate: bool * cases: (string * int * (string * Type) list) list

let private recordFields (t: Type) =
    t.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
    |> Array.choose (fun p ->
        match mapping p with
        | Some [ flags; seq ] when flags &&& kindMask = field -> Some(seq, (p.Name, p.PropertyType))
        | _ -> None)
    |> Array.sortBy fst
    |> Array.map snd
    |> List.ofArray

let private unionCases (t: Type) =
    let all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

    // A case is a static NewX method (with fields) or a static property X
    // (without), marked UnionCase with its tag.
    let cases =
        Seq.append (t.GetProperties all |> Seq.cast<MemberInfo>) (t.GetMethods all |> Seq.filter (fun m -> not (m.Name.StartsWith "get_")) |> Seq.cast<MemberInfo>)
        |> Seq.choose (fun m ->
            match mapping m with
            | Some [ flags; tag ] when flags &&& kindMask = unionCase ->
                let caseName = if m :? MethodInfo && m.Name.StartsWith "New" then m.Name.Substring 3 else m.Name
                Some(tag, caseName)
            | _ -> None)
        |> Seq.distinctBy fst
        |> Seq.sortBy fst
        |> List.ofSeq

    // A case's fields: properties marked Field with the case's tag, on the
    // union (one case) or on the case's nested class.
    let fieldsOf (tag: int) =
        Seq.append [ t ] (t.GetNestedTypes(BindingFlags.Public ||| BindingFlags.NonPublic))
        |> Seq.collect (fun n -> n.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly))
        |> Seq.choose (fun p ->
            match mapping p with
            | Some [ flags; variant; seq ] when flags &&& kindMask = field && variant = tag -> Some(seq, (p.Name, p.PropertyType))
            | _ -> None)
        |> Seq.distinctBy fst
        |> Seq.sortBy fst
        |> Seq.map snd
        |> List.ofSeq

    cases |> List.map (fun (tag, caseName) -> caseName, tag, fieldsOf tag)

let private shapes (assembly: Assembly) =
    assembly.GetTypes()
    |> Array.filter (fun t -> visible t && not t.IsGenericTypeDefinition)
    |> Array.choose (fun t ->
        match kind t with
        | Some(k, false) when k = recordType -> Some(Record(t, recordFields t))
        | Some(k, isPrivate) when k = sumType -> Some(Union(t, isPrivate, unionCases t))
        | _ -> None)
    |> List.ofArray

let private emit (domain: Assembly list) (all: Shape list) =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    line "// Generated by Plasm's build step (docs/aot.md) from the domain assemblies:"

    for a in domain do
        line ("//   " + a.GetName().Name)

    line "// Do not edit: the records and unions are the only place a shape is stated."
    line "module internal Plasm.Generated.Shapes"
    line ""
    line "#nowarn \"26\" // a single case's reader has a rule for other cases"
    line ""
    line "open Plasm"
    line ""
    let body = StringBuilder()
    let outer = line
    let line (s: string) = body.AppendLine(if s = "" then "" else "            " + s) |> ignore

    let elements = Collections.Generic.HashSet<string>()
    let options = ResizeArray<string>()
    let lists = ResizeArray<string>()

    let rec note (t: Type) =
        if t.IsGenericType then
            let def = t.GetGenericTypeDefinition().FullName
            let arg = t.GetGenericArguments().[0]

            if def = "Microsoft.FSharp.Core.FSharpOption`1" && elements.Add("o:" + name arg) then options.Add(name arg)
            if def = "Microsoft.FSharp.Collections.FSharpList`1" && elements.Add("l:" + name arg) then lists.Add(name arg)
            t.GetGenericArguments() |> Array.iter note

    let unboxed (i: int) (ft: Type) = "unbox<" + name ft + "> v.[" + string i + "]"

    // Each type by a short name of its own: F# patterns take no global. path.
    let short =
        all
        |> List.mapi (fun i s ->
            let t = match s with Record(t, _) | Union(t, _, _) -> t
            outer ("type private S" + string i + " = " + name t)
            t, "S" + string i)
        |> dict

    for s in all do
        match s with
        | Record(t, fields) ->
            let n = name t
            if elements.Add("o:" + n) then options.Add n
            if elements.Add("l:" + n) then lists.Add n

            for _, ft in fields do
                note ft

            line ("Shapes.Record<" + n + ">(")
            line ("    [| " + String.Join("; ", fields |> List.map (fun (f, _) -> "typeof<" + n + ">.GetProperty(\"" + f + "\")")) + " |],")
            line ("    (fun v -> ({ " + String.Join("; ", fields |> List.mapi (fun i (f, ft) -> identifier f + " = " + unboxed i ft)) + " }: " + n + ")),")
            line ("    (fun r -> [| " + String.Join("; ", fields |> List.map (fun (f, _) -> "box r." + identifier f)) + " |]))")
            line ""
        | Union(t, isPrivate, cases) ->
            let n = name t
            let abbrev = short.[t]
            if elements.Add("o:" + n) then options.Add n

            for _, _, fields in cases do
                for _, ft in fields do
                    note ft

            let caseRef (c: string) = abbrev + "." + identifier c

            if isPrivate then
                // Its cases are the domain's own: Plasm builds it through its
                // create function and reads it through its own members.
                line ("Shapes.Union<" + n + ">(true, (fun _ -> 0), [||])")
            else
                let pattern (c: string) (fields: (string * Type) list) = if fields.IsEmpty then caseRef c else caseRef c + " _"
                line ("Shapes.Union<" + n + ">(")
                line "    false,"
                line ("    (fun u -> match u with " + String.Join(" ", cases |> List.map (fun (c, tag, fields) -> "| " + pattern c fields + " -> " + string tag)) + "),")
                line "    [|"

                for c, tag, fields in cases do
                    let make =
                        if fields.IsEmpty then
                            caseRef c
                        else
                            caseRef c + "(" + String.Join(", ", fields |> List.mapi (fun i (_, ft) -> unboxed i ft)) + ")"

                    let read =
                        if fields.IsEmpty then
                            "(fun _ -> [||])"
                        else
                            let vars = fields |> List.mapi (fun i _ -> "f" + string i)
                            "(fun u -> match u with " + caseRef c + "(" + String.Join(", ", vars) + ") -> [| " + String.Join("; ", vars |> List.map (fun x -> "box " + x)) + " |] | _ -> [||])"

                    match fields with
                    | [ f, ft ] ->
                        // One field: read and built without an array per value.
                        line (
                            "        Shapes.Case<" + n + ", " + name ft + ">(\"" + c + "\", " + string tag + ", \"" + f + "\", (fun f -> " + caseRef c + "(f)), (fun u -> match u with "
                            + caseRef c + "(f) -> f | _ -> Unchecked.defaultof<_>))"
                        )
                    | _ ->
                        let fieldList = "[| " + String.Join("; ", fields |> List.map (fun (f, ft) -> "\"" + f + "\", typeof<" + name ft + ">")) + " |]"
                        line ("        Shapes.Case<" + n + ">(\"" + c + "\", " + string tag + ", " + fieldList + ", (fun v -> " + make + "), " + read + ")")

                line "    |])"

            line ""

    for o in options do
        line ("Shapes.Option<" + o + ">()")

    for l in lists do
        line ("Shapes.List<" + l + ">()")

    outer ""
    outer "type internal Registration() ="
    outer "    interface IShapes with"
    outer "        member _.Register() ="
    sb.Append(body.ToString()) |> ignore
    outer "            ()"
    outer ""
    outer "[<assembly: Shapes(typeof<Registration>)>]"
    outer "do ()"
    sb.ToString()

[<EntryPoint>]
let main argv =
    match argv with
    | [| output; listing |] ->
        let references = File.ReadAllLines listing |> Array.filter (fun l -> l.Trim() <> "")
        let resolver = PathAssemblyResolver(references)
        use context = new MetadataLoadContext(resolver, "System.Runtime")

        let domain =
            references
            |> Array.choose (fun path ->
                try
                    let a = context.LoadFromAssemblyPath path
                    let own = set [ "Plasm"; "Plasm.Annotations"; "Plasm.Postgres"; "Plasm.PGlite" ]

                    if not (own.Contains(a.GetName().Name)) && a.GetReferencedAssemblies() |> Array.exists (fun r -> r.Name = "Plasm.Annotations") then
                        Some a
                    else
                        None
                with _ ->
                    None)
            |> List.ofArray

        let text = emit domain (domain |> List.collect shapes)

        // The domain assemblies, for the build to keep whole in a native binary:
        // a query names their members by reflection.
        File.WriteAllLines(Path.ChangeExtension(output, ".domain"), domain |> List.map (fun a -> a.GetName().Name))

        // Written only when it changes, so the compiler's own up-to-date check holds.
        if not (File.Exists output) || File.ReadAllText output <> text then
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath output)) |> ignore
            File.WriteAllText(output, text)

        0
    | _ ->
        eprintfn "usage: Plasm.Build <output.fs> <file listing the referenced assemblies>"
        2
