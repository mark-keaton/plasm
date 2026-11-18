/// Plasm's trimming and AOT warnings, suppressed where they arise and why each
/// is safe (docs/aot.md, "Trimming warnings"). Plasm reads the application's
/// F# types by reflection by design, so its warnings are not removed by
/// removing reflection; each is suppressed per module and warning, never for
/// the project, because an F# closure is a type of its own that no
/// suppression on its function reaches. What makes each safe:
///   - the application's assembly and its project references' are kept whole
///     (Plasm.targets), so what Plasm reflects over in them is in the binary;
///   - FSharp.Core's and the framework's members a query names, the scalar
///     types Plasm maps, System.Tuple's fields and Plasm itself are kept by
///     the descriptor (ILLink.Descriptors.xml), pinned by unit tests;
///   - a generic instantiation over a value type is never made at run time in
///     a native binary: the provider's value types are written out, the rest
///     are reference types or only inspected (docs/aot.md, "What only a
///     quotation names");
///   - the native check (tests/Plasm.Aot/run.sh) runs it all as a binary.
module internal Plasm.Trimming

open System.Diagnostics.CodeAnalysis

/// Marks a type or module whose public members a user's query names inside
/// its quotations (docs/aot.md). Plasm is kept whole in a native binary, so
/// nothing marked can be trimmed; the mark says which members the native
/// check must name, which a unit test (QuerySurface.fs) holds it to.
[<System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)>]
type QuerySurfaceAttribute() =
    inherit System.Attribute()

// tests/Plasm.Aot/trim-audit.sh compiles Plasm without the suppressions below
// (PlasmTrimAudit) and fails unless what warns is exactly what is suppressed.
#if !PLASM_TRIM_AUDIT

[<Literal>]
let private Rooted =
    "Reflects over the application's and its domain's F# types: Plasm.targets keeps their assemblies whole (TrimmerRootAssembly), and its field-getter step keeps their union fields' attributes, which the native check and its refusal step verify (docs/aot.md)"

[<Literal>]
let private Kept =
    "Reflects over members a query names in FSharp.Core, the framework or Plasm: ILLink.Descriptors.xml keeps them, and unit tests fail when the translator names a type it does not keep (docs/aot.md)"

[<Literal>]
let private NoValueTypeInstantiation =
    "Makes a generic type or method only over a reference type, whose shared code is in the binary, or only to inspect it; options and lists of the provider's value types are written out in Shape, and the native check has a row for each kind with a value type (docs/aot.md)"

[<Literal>]
let private RuntimeOnly =
    "Compiles or instantiates code only behind RuntimeFeature.IsDynamicCodeSupported; a native binary takes the path beside it, which the native check runs (docs/aot.md)"

[<Literal>]
let private FSharpCoreInternals =
    "Calls FSharp.Core's internal decoder of definitions: the descriptor keeps it, a unit test pins it per FSharp.Core version, and a missing part fails naming the version (docs/aot.md)"

// What a model is built from: the domain's records, unions and create functions.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.ModelModule", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.ModelModule", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2026", Scope = "type", Target = "T:Plasm.Conventions", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Conventions", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2070", Scope = "type", Target = "T:Plasm.Conventions", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Conventions", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Conventions", Justification = NoValueTypeInstantiation)>]

// The shapes of F# types: records, unions, options, lists, tuples.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Shape", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2067", Scope = "type", Target = "T:Plasm.Shape", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2070", Scope = "type", Target = "T:Plasm.Shape", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Shape", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2077", Scope = "type", Target = "T:Plasm.Shape", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Shape", Justification = RuntimeOnly)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2067", Scope = "type", Target = "T:Plasm.Reflection", Justification = Rooted)>]
// Reflection.Shown writes a value in a message: the application's records,
// unions and tuples (rooted) or a framework type, whose ToString is kept.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2072", Scope = "type", Target = "T:Plasm.Reflection", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Reflection", Justification = Rooted)>]

// FSharp.Core's decoder of [<ReflectedDefinition>] bodies.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2026", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2060", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2080", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Definitions", Justification = FSharpCoreInternals)>]

// Translating a query: the members and types its quotation names.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Translate", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2060", Scope = "type", Target = "T:Plasm.Translate", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2067", Scope = "type", Target = "T:Plasm.Translate", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2072", Scope = "type", Target = "T:Plasm.Translate", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Translate", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2080", Scope = "type", Target = "T:Plasm.Translate", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Translate", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Ties", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2060", Scope = "type", Target = "T:Plasm.Ties", Justification = NoValueTypeInstantiation)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2072", Scope = "type", Target = "T:Plasm.Ties", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Ties", Justification = NoValueTypeInstantiation)>]

// Reading rows, writing records, and the start-up check of an application's queries.
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2055", Scope = "type", Target = "T:Plasm.Execution", Justification = RuntimeOnly)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2067", Scope = "type", Target = "T:Plasm.Execution", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2072", Scope = "type", Target = "T:Plasm.Execution", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Execution", Justification = RuntimeOnly)>]
[<assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type", Target = "T:Plasm.Execution", Justification = RuntimeOnly)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2072", Scope = "type", Target = "T:Plasm.Writes", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2075", Scope = "type", Target = "T:Plasm.Writes", Justification = Kept)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2026", Scope = "type", Target = "T:Plasm.Repo", Justification = Rooted)>]
[<assembly: UnconditionalSuppressMessage("Trimming", "IL2065", Scope = "type", Target = "T:Plasm.Repo", Justification = Rooted)>]
do ()
#endif
