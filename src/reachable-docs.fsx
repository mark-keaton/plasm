// A package's XML doc describes only what a caller can reach. The F# compiler
// writes every `///` comment into it, internal and private ones too, so a
// reader of the packed XML (an IDE, an agent) would be shown functions nobody
// outside can call. Reachable: public or protected, on a type public all the
// way out; an F# union's case keeps its entry ("T:Union.Case"), which F#
// reads for the case's doc.
//
//   dotnet fsi src/reachable-docs.fsx filter <dll> <xml>   drop the rest from <xml>
//   dotnet fsi src/reachable-docs.fsx check <dll> <xml>    name each entry not reachable; exit 1 if any
//
// src/Directory.Build.targets filters each package's XML as it is packed;
// tests/Plasm.Package/run.sh checks the packed XML. It fails (exit 1, and so
// the pack) on a doc it cannot read, one of no members, or one it would leave
// with none. A method is matched by its parameter types, decoded from its
// signature into the doc id's own form.
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Xml.Linq

let mode, dll, xml =
    match fsi.CommandLineArgs with
    | [| _; ("filter" | "check") as m; d; x |] -> m, d, x
    | _ -> eprintfn "usage: reachable-docs.fsx filter|check <dll> <xml>"; exit 2

let unreachable =
    use pe = new PEReader(File.OpenRead dll)
    let md = pe.GetMetadataReader()
    let str (h: StringHandle) = md.GetString h

    let callable (a: MethodAttributes) =
        let a = a &&& MethodAttributes.MemberAccessMask
        a = MethodAttributes.Public || a = MethodAttributes.Family || a = MethodAttributes.FamORAssem

    let rec reachable (t: TypeDefinition) =
        let a = t.Attributes &&& TypeAttributes.VisibilityMask
        if t.GetDeclaringType().IsNil then a = TypeAttributes.Public
        else
            (a = TypeAttributes.NestedPublic || a = TypeAttributes.NestedFamily || a = TypeAttributes.NestedFamORAssem)
            && reachable (md.GetTypeDefinition(t.GetDeclaringType()))

    let rec fullName (t: TypeDefinition) =
        if t.GetDeclaringType().IsNil then
            let ns = str t.Namespace
            if ns = "" then str t.Name else ns + "." + str t.Name
        else fullName (md.GetTypeDefinition(t.GetDeclaringType())) + "." + str t.Name

    let types = dict [ for h in md.TypeDefinitions -> let t = md.GetTypeDefinition h in fullName t, t ]

    let stripArity (n: string) = match n.IndexOf '`' with -1 -> n | i -> n.Substring(0, i)

    let rec refName (r: TypeReference) =
        let n = str r.Name
        if r.ResolutionScope.Kind = HandleKind.TypeReference then
            refName (md.GetTypeReference(TypeReferenceHandle.op_Explicit r.ResolutionScope)) + "." + n
        else
            let ns = str r.Namespace
            if ns = "" then n else ns + "." + n

    /// A parameter's type as a doc id writes it: System.Int32,
    /// Microsoft.FSharp.Core.FSharpFunc{``0,``1}, `0 for a type's generic
    /// parameter, ``0 for a method's, [] for an array, @ for a byref.
    let docTypes =
        { new ISignatureTypeProvider<string, obj> with
            member _.GetPrimitiveType code =
                match code with
                | PrimitiveTypeCode.Boolean -> "System.Boolean" | PrimitiveTypeCode.Byte -> "System.Byte"
                | PrimitiveTypeCode.SByte -> "System.SByte" | PrimitiveTypeCode.Char -> "System.Char"
                | PrimitiveTypeCode.Int16 -> "System.Int16" | PrimitiveTypeCode.UInt16 -> "System.UInt16"
                | PrimitiveTypeCode.Int32 -> "System.Int32" | PrimitiveTypeCode.UInt32 -> "System.UInt32"
                | PrimitiveTypeCode.Int64 -> "System.Int64" | PrimitiveTypeCode.UInt64 -> "System.UInt64"
                | PrimitiveTypeCode.Single -> "System.Single" | PrimitiveTypeCode.Double -> "System.Double"
                | PrimitiveTypeCode.IntPtr -> "System.IntPtr" | PrimitiveTypeCode.UIntPtr -> "System.UIntPtr"
                | PrimitiveTypeCode.Object -> "System.Object" | PrimitiveTypeCode.String -> "System.String"
                | PrimitiveTypeCode.TypedReference -> "System.TypedReference" | _ -> "System.Void"
            member _.GetTypeFromDefinition(_, h, _) = fullName (md.GetTypeDefinition h)
            member _.GetTypeFromReference(_, h, _) = refName (md.GetTypeReference h)
            member this.GetTypeFromSpecification(_, ctx, h, _) = (md.GetTypeSpecification h).DecodeSignature(this, ctx)
            member _.GetSZArrayType e = e + "[]"
            member _.GetArrayType(e, shape) = e + "[" + String.concat "," (List.replicate shape.Rank "0:") + "]"
            member _.GetByReferenceType e = e + "@"
            member _.GetPointerType e = e + "*"
            member _.GetGenericInstantiation(g, args) =
                // A nested generic's name carries its own arity only on its last part.
                let parts = g.Split '.'
                String.concat "." (parts |> Array.map stripArity) + "{" + String.concat "," args + "}"
            member _.GetGenericTypeParameter(_, i) = "`" + string i
            member _.GetGenericMethodParameter(_, i) = "``" + string i
            member _.GetFunctionPointerType _ = "=FUNC"
            member _.GetModifiedType(_, t, _) = t
            member _.GetPinnedType t = t }

    /// The callable methods of a type named `name`, each with its parameters
    /// as the doc id writes them ("" for none).
    let methods (t: TypeDefinition) name =
        [ for h in t.GetMethods() do
            let m = md.GetMethodDefinition h
            if str m.Name = name && callable m.Attributes then
                String.concat "," (m.DecodeSignature(docTypes, null).ParameterTypes) ]

    /// An F# union's case, "Union.Case": a case of a reachable union, which
    /// tests for it by name. (A case with fields is a nested type too, which F#
    /// may make internal; a case without is no type at all.)
    let unionCase (id: string) =
        let dot = id.LastIndexOf '.'
        dot > 0
        && (match types.TryGetValue(id.Substring(0, dot)) with
            | true, u -> reachable u && not (List.isEmpty (methods u ("get_Is" + id.Substring(dot + 1))))
            | _ -> false)

    let reachableMember (id: string) =
        let kind, rest = id.[0], id.Substring 2
        if kind = 'T' then
            (match types.TryGetValue rest with
             | true, t -> reachable t
             | _ -> false)
            || unionCase rest
        else
            // A conversion operator's id ends with ~ and its return type; none here has one.
            let name, args =
                match rest.IndexOf '(' with
                | -1 -> rest, ""
                | i -> rest.Substring(0, i), rest.Substring(i + 1, rest.Length - i - 2)
            let dot = name.LastIndexOf '.'
            let mem = name.Substring(dot + 1)
            let mem = (match mem.IndexOf "``" with -1 -> mem | i -> mem.Substring(0, i)).Replace('#', '.')
            match types.TryGetValue(name.Substring(0, dot)) with
            | true, t when reachable t ->
                match kind with
                | 'M' -> methods t mem |> List.contains args
                | 'P' ->
                    t.GetProperties() |> Seq.map md.GetPropertyDefinition |> Seq.exists (fun p ->
                        let a = p.GetAccessors()
                        str p.Name = mem
                        && ((not a.Getter.IsNil && callable (md.GetMethodDefinition a.Getter).Attributes)
                            || (not a.Setter.IsNil && callable (md.GetMethodDefinition a.Setter).Attributes)))
                | 'F' ->
                    t.GetFields() |> Seq.map md.GetFieldDefinition |> Seq.exists (fun f ->
                        let a = f.Attributes &&& FieldAttributes.FieldAccessMask
                        str f.Name = mem && (a = FieldAttributes.Public || a = FieldAttributes.Family || a = FieldAttributes.FamORAssem))
                | 'E' ->
                    t.GetEvents() |> Seq.map md.GetEventDefinition |> Seq.exists (fun e ->
                        str e.Name = mem && callable (md.GetMethodDefinition(e.GetAccessors().Adder)).Attributes)
                | _ -> false
            | _ -> false

    let doc = XDocument.Load xml
    let members = List.ofSeq (doc.Descendants(XName.Get "member"))
    // A doc of nothing, or one left with nothing, is a pack gone wrong: never packed quietly.
    if members.IsEmpty then failwithf "%s describes no member: no XML doc to pack" xml
    let gone = [ for m in members do if not (reachableMember (m.Attribute(XName.Get "name").Value)) then m ]
    if mode = "filter" && gone.Length = members.Length then failwithf "%s: no member a caller can reach is described" xml
    if mode = "filter" then
        for m in gone do m.Remove()
        doc.Save xml
    gone |> List.map (fun m -> m.Attribute(XName.Get "name").Value)

if mode = "check" then
    for id in unreachable do printfn "%s describes %s, which no caller can reach" (Path.GetFileName xml) id
    exit (if unreachable.IsEmpty then 0 else 1)
else
    printfn "%s: dropped the docs of %d members no caller can reach" (Path.GetFileName xml) unreachable.Length
