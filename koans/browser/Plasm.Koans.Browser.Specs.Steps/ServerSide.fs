/// The same F# snippets the page evaluates against PGlite, evaluated against a
/// real PostgreSQL server (Testcontainers) in a hosted F# Interactive session.
module Plasm.Koans.Browser.Specs.ServerSide

open System
open System.IO
open System.Threading.Tasks
open FSharp.Compiler.Interactive.Shell
open Npgsql
open Testcontainers.PostgreSql

let private exec (cs: string) (sql: string) =
    task {
        use conn = new NpgsqlConnection(cs)
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        let! _ = cmd.ExecuteNonQueryAsync()
        return ()
    }

/// A database with the koans' schema and starting data.
let private database =
    lazy
        (task {
            let! admin =
                match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
                | null
                | "" ->
                    task {
                        let c = PostgreSqlBuilder("postgres:18.4").Build()
                        do! c.StartAsync()
                        return c.GetConnectionString()
                    }
                | cs -> Task.FromResult cs

            let name = $"plasm_browser_{Guid.NewGuid():N}"
            do! exec admin $"create database {name}"
            let b = NpgsqlConnectionStringBuilder(admin)
            b.Database <- name
            do! exec b.ConnectionString Plasm.Koans.KoansData.schema
            do! exec b.ConnectionString Plasm.Koans.KoansData.data
            return b.ConnectionString
        })

let private session =
    lazy
        (task {
            let! cs = database.Force()

            let fsi =
                FsiEvaluationSession.Create(
                    FsiEvaluationSession.GetDefaultConfiguration(),
                    [| "fsi"; "--noninteractive"; "--nologo" |],
                    new StringReader(""),
                    new StringWriter(),
                    new StringWriter(),
                    collectible = false
                )

            let run (code: string) =
                let result, diagnostics = fsi.EvalInteractionNonThrowing code

                match result with
                | Choice2Of2 ex -> failwithf "F# Interactive failed:\n%s\n%A\n%A" code ex (diagnostics |> Array.map (fun d -> d.Message))
                | _ -> ()

            for t in [ typeof<Plasm.Repo>; typeof<Plasm.Postgres.PostgresProvider>; typeof<NpgsqlConnection>; typeof<Plasm.Koans.Koans.KoanContext> ] do
                run $"#r @\"{t.Assembly.Location}\""

            run (
                String.concat
                    "\n"
                    [ "open System"
                      "open System.Threading.Tasks"
                      "open Plasm"
                      "open Plasm.Koans"
                      "open Plasm.Koans.Shop"
                      "let commands = ResizeArray<string>()"
                      $"let repo = Repo(Db.model, Plasm.Postgres.Postgres.connect @\"{cs}\", {{ RepoOptions.Default with OnCommand = Some(fun e -> commands.Add e.Sql) }})" ]
            )

            return fsi, run
        })

let private evaluateOne (code: string) : Task<string * string list> =
    task {
        let! fsi, run = session.Force()
        run ("let evaluate (repo: Repo) : Task<string> =\n" + code)


        run
            "let runIt () =\n    commands.Clear()\n    let r = repo.Sandbox(fun r -> evaluate r).GetAwaiter().GetResult()\n    r, List.ofSeq commands"

        match fsi.EvalExpressionNonThrowing "runIt ()" with
        | Choice1Of2(Some v), _ -> return unbox<string * string list> v.ReflectionValue
        | other -> return failwithf "evaluating on the server failed: %A" other
    }

/// One snippet at a time: the session redefines `evaluate` for each one.
let private gate = new Threading.SemaphoreSlim(1, 1)

/// Runs `code` (the body of `fun (repo: Repo) -> Task<string>`, indented by
/// four) in a sandbox against PostgreSQL; returns its result and the SQL sent.
/// Values are formatted with the invariant culture, as in the browser.
let evaluate (code: string) : Task<string * string list> =
    task {
        do! gate.WaitAsync()
        let culture = Globalization.CultureInfo.CurrentCulture
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo.InvariantCulture

        try
            return! evaluateOne code
        finally
            Globalization.CultureInfo.CurrentCulture <- culture
            gate.Release() |> ignore
    }

