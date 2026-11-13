/// Runs one koan in the browser: compiles its code, then runs it in a sandbox
/// over PGlite, exactly as the terminal runner does over PostgreSQL.
module Plasm.Koans.Browser.KoanRun

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Plasm
open Plasm.Koans
open Plasm.Koans.Runner
open Plasm.PGlite

let private commands = ResizeArray<CommandEvent>()
let mutable private repo: Repo option = None

/// Creates the koans database in PGlite and the repository over it.
let init (bridge: IPGliteBridge) : Task<string> =
    task {
        for sql in [ KoansData.schema; KoansData.data ] do
            match bridge.Execute sql with
            | "" -> ()
            | error -> failwithf "setting up the koans database failed: %s" error

        let! provider = PGlite.connect bridge

        repo <-
            Some(
                Repo(
                    Shop.Db.model,
                    provider,
                    { RepoOptions.Default with
                        OnCommand = Some commands.Add
                        IncludeParameterValues = true }
                )
            )

        return "ready"
    }

/// The source compiled for one koan: its lesson's helpers, then the koan's
/// body as a function value. The koan's own lines start at `prefixLines` + 1.
let source (helpers: string) (isTask: bool) (lambda: string) (body: string) =
    let head =
        String.concat
            "\n"
            [ "module Koan"
              "open Plasm"
              "open Plasm.Koans"
              "open Plasm.Koans.Shop"
              helpers
              // Boxed, so it compiles to a property (a function-typed `let`
              // would compile to a method).
              $"let koanBody : obj = box (({lambda}" ]

    let signature = if isTask then "KoanContext -> System.Threading.Tasks.Task<unit>" else "KoanContext -> unit"
    head + "\n" + body + $") : {signature})", head.Split('\n').Length

let private json (fields: (string * obj) list) = JsonSerializer.Serialize(dict fields)

/// Compiles and runs a koan; returns JSON: {status, text, errors?, sql?}.
/// Compile errors are reported with lines relative to the koan's body.
let run (refsDir: string) (title: string) (name: string) (helpers: string) (isTask: bool) (lambda: string) (body: string) : Task<string> =
    task {
        let src, prefix = source helpers isTask lambda body

        match! Compiler.compile refsDir src with
        | Error errors ->
            let shown =
                errors
                |> List.map (fun e -> dict [ "line", box (e.Line - prefix); "column", box e.Column; "code", box e.Code; "message", box e.Message ])
                |> Array.ofList

            return json [ "status", box "compile"; "errors", box shown ]
        | Ok assembly ->
            let value = assembly.GetType("Koan").GetProperty("koanBody").GetValue(null)

            let compiledBody: KoanContext -> Task<unit> =
                if isTask then
                    value :?> (KoanContext -> Task<unit>)
                else
                    let sync = value :?> (KoanContext -> unit)

                    fun k ->
                        sync k
                        Task.FromResult()

            let koan =
                { Name = name
                  File = ""
                  Line = 0
                  Body = compiledBody }

            let! r = Runner.run repo.Value commands (Lesson.make title [ koan ]) koan

            let status =
                match r.Outcome with
                | Passed -> "passed"
                | Unanswered _ -> "unanswered"
                | Failed _ -> "failed"
                | Threw _ -> "threw"

            let text = if r.Outcome = Passed then "" else describeNext [] r

            // The first blank still in the koan's code (1-based, within the body).
            let blankLine =
                body.Split('\n')
                |> Array.tryFindIndex (fun l ->
                    let code = match l.IndexOf "//" with | -1 -> l | i -> l.Substring(0, i)
                    Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w.])__(?!\w)"))
                |> Option.map ((+) 1)
                |> Option.defaultValue 0

            return json [ "status", box status; "text", box text; "blankLine", box blankLine; "sql", box (Array.ofList r.Sql) ]
    }
