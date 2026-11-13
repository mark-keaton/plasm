/// Compiles F# source to an assembly in memory, in a browser.
module Plasm.Koans.Browser.Compiler

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics

/// A compiler error, with its position in the compiled source.
type CompileError =
    { Line: int
      Column: int
      Code: string
      Message: string }

let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = false))
let mutable private counter = 0

/// Compiles `source` (an F# module) against the reference assemblies in `refsDir`.
let compile (refsDir: string) (source: string) : Task<Result<Assembly, CompileError list>> =
    task {
        // Single-threaded WebAssembly cannot block on a task, so the compiler's
        // parallel reference resolution and parallel compilation are switched
        // off; its asynchronous work then completes synchronously.
        Environment.SetEnvironmentVariable("FCS_ParallelReferenceResolution", "false")
        counter <- counter + 1
        let dir = Path.Combine(Path.GetTempPath(), "koan")
        Directory.CreateDirectory dir |> ignore
        let input = Path.Combine(dir, $"koan{counter}.fs")
        let output = Path.Combine(dir, $"koan{counter}.dll")
        File.WriteAllText(input, source)
        let refs = Directory.GetFiles(refsDir, "*.dll") |> Array.map (fun r -> "-r:" + r)

        let argv =
            Array.concat
                [ [| "fsc.exe"; "--noframework"; "--nologo"; "--target:library"; "--optimize-"; "--debug-"; "--parallelcompilation-"; $"--out:{output}" |]
                  refs
                  [| input |] ]

        let! diagnostics, failure =
            task {
                try
                    return! checker.Value.Compile(argv) |> Async.StartAsTask
                with ex ->
                    return [||], Some ex
            }

        let errors =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
            |> Array.map (fun d ->
                { Line = d.StartLine
                  Column = d.StartColumn + 1
                  Code = d.ErrorNumberText
                  Message = d.Message })
            |> List.ofArray

        match failure, errors with
        | None, [] -> return Ok(Assembly.Load(File.ReadAllBytes output))
        | Some ex, [] -> return Error [ { Line = 0; Column = 0; Code = "internal"; Message = string ex } ]
        | _ -> return Error errors
    }
