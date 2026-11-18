/// A workaround, until the F# compiler marks them (docs/aot.md,
/// docs/aot-repro/union-field-attributes): it emits a union case field's
/// getter without SpecialName, and a native binary keeps no attributes on a
/// property whose getter lacks it. For each assembly given, this writes a copy
/// into the output folder with SpecialName set on the getter of every F#
/// field property (CompilationMapping with SourceConstructFlags.Field) that
/// lacks it, in place: two bytes of a MethodDef row, so no table grows and
/// nothing moves. An assembly with none (any that is not F#) is not copied.
///
/// Usage: Plasm.FieldGetters <output folder> <assembly>...
module Plasm.FieldGetters.Program

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable

/// The file offsets of the Flags of each getter to mark.
let private getters (bytes: byte[]) : int list =
    use pe = new PEReader(new MemoryStream(bytes))

    if not pe.HasMetadata then
        []
    else
        let md = pe.GetMetadataReader()

        // Only F# assemblies have F# field properties.
        let fsharp =
            md.AssemblyReferences |> Seq.exists (fun h -> md.GetString(md.GetAssemblyReference(h).Name) = "FSharp.Core")

        if not fsharp then [] else

        let start = pe.PEHeaders.MetadataStartOffset
        let table = md.GetTableMetadataOffset TableIndex.MethodDef
        let row = md.GetTableRowSize TableIndex.MethodDef

        let isField (a: CustomAttribute) =
            a.Constructor.Kind = HandleKind.MemberReference
            && (let parent = md.GetMemberReference(MemberReferenceHandle.op_Explicit a.Constructor).Parent

                parent.Kind = HandleKind.TypeReference
                && (let t = md.GetTypeReference(TypeReferenceHandle.op_Explicit parent)
                    md.GetString t.Name = "CompilationMappingAttribute" && md.GetString t.Namespace = "Microsoft.FSharp.Core")
                && (let mutable blob = md.GetBlobReader a.Value
                    // SourceConstructFlags.Field, under the kind mask.
                    blob.ReadUInt16() = 1us && blob.ReadInt32() &&& 31 = 4))

        [ for ph in md.PropertyDefinitions do
              let p = md.GetPropertyDefinition ph
              let getter = p.GetAccessors().Getter

              if not getter.IsNil
                 && md.GetMethodDefinition(getter).Attributes &&& MethodAttributes.SpecialName = enum 0
                 && (p.GetCustomAttributes() |> Seq.exists (fun h -> isField (md.GetCustomAttribute h))) then
                  // A MethodDef row: RVA (4 bytes), ImplFlags (2), Flags (2).
                  start + table + (MetadataTokens.GetRowNumber getter - 1) * row + 6 ]

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | output :: assemblies ->
        Directory.CreateDirectory output |> ignore
        let mutable count = 0

        for path in assemblies do
            let bytes = File.ReadAllBytes path

            match getters bytes with
            | [] -> ()
            | offsets ->
                for o in offsets do
                    let flags = BitConverter.ToUInt16(bytes, o) ||| uint16 MethodAttributes.SpecialName
                    bytes.[o] <- byte (flags &&& 0xFFus)
                    bytes.[o + 1] <- byte (flags >>> 8)

                File.WriteAllBytes(Path.Combine(output, Path.GetFileName path), bytes)
                count <- count + offsets.Length

        Console.WriteLine("Plasm: " + string count + " F# field getters marked SpecialName for the native compiler")
        0
    | [] ->
        Console.Error.WriteLine "usage: Plasm.FieldGetters <output folder> <assembly>..."
        2
