/// Compile-time acceptance and rejection checks: type-checks F# snippets
/// against the real Plasm and test-domain assemblies with the F# compiler.
module Plasm.TestDomain.CompileCheck

open System
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

type CompileResult =
    { Errors: string list }

    member r.Compiles = r.Errors.IsEmpty

let private checker = lazy (FSharpChecker.Create())
let private gate = new SemaphoreSlim(1, 1)
let mutable private n = 0

let private prelude =
    let plasm = typeof<Plasm.Query>.Assembly.Location
    let postgres = typeof<Plasm.Postgres.PostgresProvider>.Assembly.Location
    let domain = typeof<Domain.Order>.Assembly.Location

    String.concat
        "\n"
        [ $"#r @\"{plasm}\""
          $"#r @\"{postgres}\""
          $"#r @\"{domain}\""
          "open System"
          "open Plasm"
          "open Plasm.TestDomain.Domain"
          "open Plasm.TestDomain.Domain.Schemas"
          "" ]

/// Type-checks `code` (with Plasm and the test domain opened).
let check (code: string) =
    task {
        do! gate.WaitAsync()

        try
            n <- n + 1
            let file = Path.Combine(Path.GetTempPath(), $"plasm_snippet_{Environment.ProcessId}_{n}.fsx")
            let source = SourceText.ofString (prelude + code)
            let c = checker.Force()

            let! options, _ =
                c.GetProjectOptionsFromScript(file, source, assumeDotNetFramework = false, useSdkRefs = true)
                |> Async.StartAsTask

            let! _, answer = c.ParseAndCheckFileInProject(file, 0, source, options) |> Async.StartAsTask

            let errors =
                match answer with
                | FSharpCheckFileAnswer.Succeeded r ->
                    r.Diagnostics
                    |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                    |> Array.map (fun d -> $"FS{d.ErrorNumber:D4}: {d.Message}")
                    |> List.ofArray
                | FSharpCheckFileAnswer.Aborted -> [ "type checking aborted" ]

            return { Errors = errors }
        finally
            gate.Release() |> ignore
    }
