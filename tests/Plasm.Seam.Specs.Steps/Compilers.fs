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
      typeof<Plasm.Sqlite.SqliteOptions>.Assembly.Location
      typeof<Microsoft.Data.Sqlite.SqliteConnection>.Assembly.Location
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

            // One compile at a time: the compiler service's Compile is not run side by side.
            let diagnostics, failure = lock checker (fun () -> checker.Force().Compile argv |> Async.RunSynchronously)

            let errors =
                diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                |> Array.map (fun d -> $"{d.Range.StartLine},{d.Range.StartColumn}: FS{d.ErrorNumber:D4}: {d.Message}")

            match failure with
            | Some e -> Error("the F# compiler failed: " + string e + "\n" + String.concat "\n" errors)
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
let private compileWith (sources: (string * string) list) (data: string) : Compilation * Diagnostic list =
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

    // Plasm.CSharp's analyzers, as the compiler runs them from its package.
    let analyzers =
        Collections.Immutable.ImmutableArray.Create<DiagnosticAnalyzer>(Plasm.Seam.Analyzers.RequestBodyJson())

    let analyzed =
        output.WithAnalyzers(analyzers).GetAllDiagnosticsAsync() |> Async.AwaitTask |> Async.RunSynchronously

    output,
    [ yield! generated
      yield! analyzed ]
    |> List.filter (fun d -> d.Severity >= DiagnosticSeverity.Warning)

/// What C#'s compiler says of `sources` (name, text) over the data assembly:
/// each diagnostic of warning or error, as the compiler prints it.
let csharp (sources: (string * string) list) (data: string) : Diagnostic list = snd (compileWith sources data)

/// What `Host.Run()` answers, `sources` compiled and run over the data
/// assembly, each in a load context of its own (each scenario's data project
/// has the same name).
let run (sources: (string * string) list) (data: string) : string =
    let output, diagnostics = compileWith sources data

    match diagnostics |> List.filter (fun d -> d.Severity = DiagnosticSeverity.Error) with
    | [] -> ()
    | errors -> failwith ("the C# does not compile:\n" + (errors |> List.map string |> String.concat "\n"))

    use image = new MemoryStream()
    let emitted = output.Emit image

    if not emitted.Success then
        failwith ("the C# was not emitted:\n" + (emitted.Diagnostics |> Seq.map string |> String.concat "\n"))

    let dataName = Reflection.AssemblyName.GetAssemblyName(data).Name
    let context = Runtime.Loader.AssemblyLoadContext($"host-{Guid.NewGuid()}", isCollectible = true)

    context.add_Resolving(fun ctx name ->
        if name.Name = dataName then ctx.LoadFromAssemblyPath data else null)

    try
        image.Position <- 0L
        let host = context.LoadFromStream image
        let t = nonNull (host.GetType "Shop.Api.Host")
        let m = nonNull (t.GetMethod "Run")
        string (m.Invoke(null, [||]))
    finally
        context.Unload()
