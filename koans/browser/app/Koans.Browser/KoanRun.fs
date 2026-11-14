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
/// code inside the frame every koan shares, `fun repo -> task { ... }` (or
/// `fun repo -> ...` when the koan is not framed). The page shows only the code;
/// its lines start at `prefixLines` + 1, indented by `indent`.
let source (helpers: string) (isTask: bool) (framed: bool) (body: string) =
    let indent = if framed then 8 else 4

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
              "let koanBody : obj = box ((fun (repo: Repo) ->"
              if framed then "    task {" ]

    let code =
        body.Split('\n') |> Array.map (fun l -> if l = "" then l else String(' ', indent) + l) |> String.concat "\n"

    let close = if framed then "\n    }" else ""
    let signature = if isTask then "Repo -> System.Threading.Tasks.Task<unit>" else "Repo -> unit"
    head + "\n" + code + close + $") : {signature})", head.Split('\n').Length, indent

let private json (fields: (string * obj) list) = JsonSerializer.Serialize(dict fields)

/// Compiles and runs a koan; returns JSON: {status, text, errors?, sql?}.
/// Compile errors are reported with lines relative to the koan's body.
let run (refsDir: string) (title: string) (name: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) : Task<string> =
    task {
        let src, prefix, indent = source helpers isTask framed body

        match! Compiler.compile refsDir src with
        | Error errors ->
            let shown =
                errors
                |> List.map (fun e -> dict [ "line", box (e.Line - prefix); "column", box (max 1 (e.Column - indent)); "code", box e.Code; "message", box e.Message ])
                |> Array.ofList

            return json [ "status", box "compile"; "errors", box shown ]
        | Ok assembly ->
            let value = assembly.GetType("Koan").GetProperty("koanBody").GetValue(null)

            let compiledBody: Repo -> Task<unit> =
                if isTask then
                    value :?> (Repo -> Task<unit>)
                else
                    let sync = value :?> (Repo -> unit)

                    fun repo ->
                        sync repo
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

            // The parts of the result, for the page to lay out.
            let expected, actual, threw =
                match r.Outcome with
                | Failed(e, a) -> Runner.formatSql e, Runner.formatSql a, null
                | Threw(t, m) -> null, null, $"{t}: {m}"
                | _ -> null, null, null

            let shownSql =
                match r.Outcome with
                | Failed _ | Threw _ -> r.Sql |> List.map (fun s -> (Runner.formatSql s).Replace("\n", "\n    ")) |> Array.ofList
                | _ -> [||]

            return
                json
                    [ "status", box status
                      "text", box text
                      "blankLine", box blankLine
                      "sql", box (Array.ofList r.Sql)
                      "expected", box expected
                      "actual", box actual
                      "threw", box threw
                      "shownSql", box shownSql ]
    }

/// For checks: compiles `code` (the body of `fun (repo: Repo) -> Task<string>`,
/// indented by four), runs it (in a sandbox when `sandboxed`) and returns
/// JSON {result, sql} or {error}.
let evaluate (refsDir: string) (code: string) (sandboxed: bool) : Task<string> =
    task {
        let src =
            String.concat
                "\n"
                [ "module Koan"
                  "open System"
                  "open System.Threading.Tasks"
                  "open Plasm"
                  "open Plasm.Koans"
                  "open Plasm.Koans.Shop"
                  "let evaluate (repo: Repo) : Task<string> ="
                  code ]

        match! Compiler.compile refsDir src with
        | Error errors -> return json [ "error", box (errors |> List.map (fun e -> $"({e.Line},{e.Column}) {e.Code} {e.Message}") |> String.concat "\n") ]
        | Ok assembly ->
            let f = assembly.GetType("Koan").GetMethod("evaluate")
            let call (r: Repo) = f.Invoke(null, [| box r |]) :?> Task<string>
            commands.Clear()

            try
                let! result = if sandboxed then repo.Value.Sandbox(fun r -> call r) else call repo.Value

                let sql =
                    commands
                    |> Seq.map (fun c -> c.Sql)
                    |> Seq.filter (fun s -> not (s.StartsWith "SAVEPOINT" || s.StartsWith "RELEASE" || s.StartsWith "ROLLBACK"))
                    |> Array.ofSeq

                return json [ "result", box result; "sql", box sql ]
            with ex ->
                return json [ "error", box (string ex) ]
    }
