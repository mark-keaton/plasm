/// The two compilers a scenario needs, in process: F#'s, to build the data
/// project a scenario writes, and C#'s, to compile what the seam wrote with
/// the host, System.Text.Json's source generator run as a build runs it.
module Plasm.Seam.Specs.Compilers

open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics

/// The folder this run keeps its builds in.
let work =
    let d = Path.Combine(Path.GetTempPath(), $"plasm-seam-specs-{Environment.ProcessId}")
    Directory.CreateDirectory d |> ignore
    d

let private runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()

/// The shared frameworks: .NET's, and ASP.NET Core's of the same version.
let private frameworks =
    let netcore = DirectoryInfo(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))
    let aspnet = Path.Combine(netcore.Parent.Parent.FullName, "Microsoft.AspNetCore.App", netcore.Name)
    [ netcore.FullName; aspnet ] |> List.filter Directory.Exists

let private managed (path: string) =
    try
        Reflection.AssemblyName.GetAssemblyName path |> ignore
        true
    with _ ->
        false

let private frameworkAssemblies =
    lazy
        [ for d in frameworks do
              for f in Directory.GetFiles(d, "*.dll") do
                  if managed f then f ]

/// Plasm's assemblies and FSharp.Core, as this run loaded them.
let plasmAssemblies =
    [ typeof<Plasm.TableAttribute>.Assembly.Location
      typeof<Plasm.Query>.Assembly.Location
      typeof<Plasm.CSharp.Missing>.Assembly.Location
      typeof<Microsoft.FSharp.Core.Unit>.Assembly.Location ]

/// Every reference a host over the data project has: the frameworks, Plasm, FSharp.Core.
let references () = frameworkAssemblies.Value @ plasmAssemblies

let private hash (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).Substring(0, 16)

let private checker = lazy (FSharpChecker.Create())
let private built = ConcurrentDictionary<string, Result<string, string>>()

/// The data project `source` compiled to <name>.dll, once for each source.
let fsharp (name: string) (source: string) : Result<string, string> =
    built.GetOrAdd(
        name + hash source,
        fun key ->
            let dir = Path.Combine(work, key)
            Directory.CreateDirectory dir |> ignore
            let file = Path.Combine(dir, name + ".fs")
            File.WriteAllText(file, source)
            let dll = Path.Combine(dir, name + ".dll")

            let argv =
                [| yield "fsc.exe"
                   yield $"-o:{dll}"
                   yield "-a"
                   yield "--targetprofile:netcore"
                   yield "--noframework"
                   yield "--nologo"
                   yield "--optimize+"
                   yield "--doc:" + Path.ChangeExtension(dll, ".xml")
                   for r in references () do
                       yield $"-r:{r}"
                   yield file |]

            let diagnostics, failure = checker.Force().Compile argv |> Async.RunSynchronously

            let errors =
                diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                |> Array.map (fun d -> $"{d.Range.StartLine},{d.Range.StartColumn}: FS{d.ErrorNumber:D4}: {d.Message}")

            match failure with
            | Some e -> Error $"the F# compiler failed: {e.Message}"
            | None when errors.Length > 0 -> Error(String.concat "\n" errors)
            | None -> Ok dll
    )

let private jsonGenerator =
    lazy
        (let netcore = DirectoryInfo(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))
         let root = netcore.Parent.Parent.Parent.FullName
         let pack = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref", netcore.Name, "analyzers", "dotnet", "cs", "System.Text.Json.SourceGeneration.dll")

         if not (File.Exists pack) then
             failwith $"System.Text.Json's source generator is not at {pack}"

         let analyzer = new AnalyzerFileReference(pack, { new IAnalyzerAssemblyLoader with
                                                             member _.AddDependencyLocation _ = ()
                                                             member _.LoadFromPath p = Reflection.Assembly.LoadFrom p })

         analyzer.GetGenerators(LanguageNames.CSharp))

/// What C#'s compiler says of `sources` (name, text) over the data assembly:
/// each diagnostic of warning or error, as the compiler prints it.
let csharp (sources: (string * string) list) (data: string) : Diagnostic list =
    let parse = CSharpParseOptions(LanguageVersion.Preview)

    let trees =
        [ for name, text in sources -> CSharpSyntaxTree.ParseText(text, parse, name, Encoding.UTF8) ]
        @ [ CSharpSyntaxTree.ParseText(
                "global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;",
                parse,
                "Usings.cs"
            ) ]

    let refs =
        (references () @ [ data ]) |> List.map (fun p -> MetadataReference.CreateFromFile p :> MetadataReference)

    let options =
        CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions = NullableContextOptions.Enable)

    let compilation = CSharpCompilation.Create("Host", trees, refs, options)
    let driver = CSharpGeneratorDriver.Create(jsonGenerator.Value, parseOptions = parse)
    let mutable output: Compilation = compilation
    let mutable generated = Collections.Immutable.ImmutableArray<Diagnostic>.Empty
    driver.RunGeneratorsAndUpdateCompilation(compilation, &output, &generated) |> ignore

    [ yield! generated
      yield! output.GetDiagnostics() ]
    |> List.filter (fun d -> d.Severity >= DiagnosticSeverity.Warning)
