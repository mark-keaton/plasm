/// The koans in a notebook: `start` once, then each koan cell calls `koan` or
/// `koanTask`, which runs it in its own sandbox and shows the outcome. The
/// notebook itself is generated from the lesson sources (`generate`).
module Plasm.Koans.Notebook

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Plasm
open Plasm.Postgres
open Plasm.Koans.Runner

type private Session =
    { Repo: Repo
      Commands: ResizeArray<CommandEvent>
      Database: Database.KoansDatabase }

let mutable private session: Session option = None
let private guidance = lazy (Guidance.load ())

/// Starts the koans database for this notebook (once; later calls do nothing).
let start () =
    match session with
    | Some s -> printfn "Database: %s" s.Database.Description
    | None ->
        let db = (Database.provision ()).GetAwaiter().GetResult()
        let commands = ResizeArray<CommandEvent>()

        let repo =
            Repo(
                Shop.Db.model,
                Postgres.connect db.ConnectionString,
                { RepoOptions.Default with
                    OnCommand = Some commands.Add
                    IncludeParameterValues = true }
            )

        session <-
            Some
                { Repo = repo
                  Commands = commands
                  Database = db }

        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> db.Dispose().GetAwaiter().GetResult())
        printfn "Database: %s" db.Description

let private check (name: string) (body: KoanContext -> Task<unit>) =
    if session.IsNone then start ()
    let s = session.Value

    let title =
        guidance.Value
        |> List.tryFind (fun l -> l.Koans.ContainsKey name)
        |> Option.map (fun l -> l.Title)
        |> Option.defaultValue "Plasm Koans"

    // No source file: a blank is reported as "in this cell".
    let koan =
        { Name = name
          File = ""
          Line = 0
          Body = body }

    let r = (run s.Repo s.Commands (Lesson.make title [ koan ]) koan).GetAwaiter().GetResult()

    match r.Outcome with
    | Passed -> printfn "✔ %s" name
    | _ -> printfn "✘ %s\n\n%s" name (describeNext guidance.Value r)

/// Runs a koan whose body is ordinary code.
let koan (name: string) (body: KoanContext -> unit) =
    check name (fun k ->
        body k
        Task.FromResult())

/// Runs a koan whose body is a task.
let koanTask (name: string) (body: KoanContext -> Task<unit>) = check name body

// ---------------------------------------------------------------------------
// Generating the notebook from the lesson sources
// ---------------------------------------------------------------------------

let private koanStart = Regex(@"^(\s*)(?:\[ )?Koan\.(Sync|Task)\(\s*$")
let private nameLine = Regex("^\\s*\"(.*)\",\\s*$")
let private lambdaLine = Regex(@"^\s*(fun \w+ ->)\s*$")

let private dedent (lines: string list) =
    let indents =
        lines |> List.filter (fun l -> l.Trim() <> "") |> List.map (fun l -> l.Length - l.TrimStart().Length)

    match indents with
    | [] -> lines
    | _ ->
        let n = List.min indents
        lines |> List.map (fun l -> if l.Length >= n then l.Substring n else l.TrimStart())

type private Cell =
    | Markdown of string list
    | Code of string list

/// One koan as source: its name, whether its body is a task, its lambda's
/// head ("fun k ->") and its body lines (indented by four).
type KoanSource =
    { Name: string
      IsTask: bool
      Lambda: string
      Body: string list }

/// A lesson's source: its helper code (opens, helpers, types) and its koans.
let parseLesson (file: string) : string list * KoanSource list =
    let lines = File.ReadAllLines file
    let lessonAt = lines |> Array.findIndex (fun l -> l.StartsWith "let lesson =")

    let helpers =
        lines.[.. lessonAt - 1]
        |> Array.filter (fun l -> not (l.StartsWith "module ") && not (l.StartsWith "/// Lesson"))
        |> Array.map (fun l -> l.Replace("let private ", "let ").Replace("type private ", "type "))
        |> List.ofArray
        |> List.skipWhile (fun l -> l.Trim() = "")
        |> List.rev
        |> List.skipWhile (fun l -> l.Trim() = "")
        |> List.rev

    let koans =
        [ for i in lessonAt .. lines.Length - 3 do
              let m = koanStart.Match lines.[i]

              if m.Success then
                  let column = lines.[i].IndexOf "Koan."
                  let name = (nameLine.Match lines.[i + 1]).Groups.[1].Value
                  let lambda = (lambdaLine.Match lines.[i + 2]).Groups.[1].Value

                  // The koan ends at the ")" in the column where "Koan." starts.
                  let close =
                      seq { i + 3 .. lines.Length - 1 }
                      |> Seq.find (fun j ->
                          let t = lines.[j]
                          t.Length > column && t.[column] = ')' && t.Substring(0, column).Trim() = "")

                  let body =
                      lines.[i + 3 .. close - 1]
                      |> List.ofArray
                      |> dedent
                      |> List.map (fun l -> if l = "" then l else "    " + l)
                      |> List.rev
                      |> List.skipWhile (fun l -> l.Trim() = "")
                      |> List.rev

                  yield
                      { Name = name
                        IsTask = m.Groups.[2].Value = "Task"
                        Lambda = lambda
                        Body = body } ]

    helpers, koans

/// A lesson's cells: its helpers, then one cell per koan.
let private lessonCells (file: string) : Cell list =
    let helpers, koans = parseLesson file

    let cells =
        koans
        |> List.map (fun k ->
            let call = if k.IsTask then "koanTask" else "koan"
            Code([ $"{call} \"{k.Name}\" ({k.Lambda}" ] @ (List.take (k.Body.Length - 1) k.Body) @ [ List.last k.Body + ")" ]))

    (if helpers.IsEmpty then [] else [ Code helpers ]) @ cells

/// The course as data for the browser: every lesson's helpers and every
/// koan's code and guidance, in order.
let course () : string =
    let byTitle = guidance.Value |> List.map (fun g -> g.Title, g) |> Map.ofList

    // The shop's source, next to the lessons' sources (koans/Plasm.Koans.Core/Shop.fs).
    let shop =
        let lessonsDir = IO.Path.GetDirectoryName Path.lessons.Head.Koans.Head.File
        let file = IO.Path.Combine(lessonsDir, "..", "..", "Plasm.Koans.Core", "Shop.fs")
        Given.parse "Shop.fs" (IO.File.ReadAllLines file)

    // What koans are given from Koans.fs: their context k, and ok and err.
    let koans =
        let lessonsDir = IO.Path.GetDirectoryName Path.lessons.Head.Koans.Head.File
        let file = IO.Path.Combine(lessonsDir, "..", "..", "Plasm.Koans.Core", "Koans.fs")
        Given.parse "Koans.fs" (IO.File.ReadAllLines file)
        |> List.filter (fun d -> List.contains d.Name [ "KoanContext"; "ok"; "err" ])

    let given (helpers: Given.Definition list) (body: string) =
        let g = Given.forKoan shop helpers body
        let g = { g with Definitions = g.Definitions @ Given.fromKoans koans body }

        dict
            [ "tables", box (Array.ofList g.Tables)
              "definitions",
              box (
                  g.Definitions
                  |> List.map (fun (d, marked) ->
                      dict
                          [ "name", box d.Name
                            "source", box d.Source
                            "code", box (String.concat "\n" d.Code)
                            "marked", box (Array.ofList marked) ])
                  |> Array.ofList
              ) ]

    let lessons =
        Path.lessons
        |> List.map (fun lesson ->
            let g = byTitle.TryFind lesson.Title
            let helpers, koans = parseLesson lesson.Koans.Head.File
            let helperDefinitions = Given.parse "this lesson" (Array.ofList helpers)

            dict
                [ "title", box lesson.Title
                  "goal", box (g |> Option.map (fun g -> g.Goal) |> Option.defaultValue "")
                  "helpers", box (String.concat "\n" helpers)
                  "koans",
                  box (
                      koans
                      |> List.map (fun k ->
                          let kg = g |> Option.bind (fun g -> g.Koans.TryFind k.Name)

                          dict
                              [ "name", box k.Name
                                "isTask", box k.IsTask
                                "lambda", box k.Lambda
                                "body", box (String.concat "\n" k.Body)
                                "discovers", box (kg |> Option.map (fun x -> x.Discovers) |> Option.defaultValue "")
                                "teaches", box (kg |> Option.map (fun x -> x.Teaches) |> Option.defaultValue "")
                                "given", box (given helperDefinitions (String.concat "\n" k.Body)) ])
                      |> Array.ofList
                  ) ])
        |> Array.ofList

    JsonSerializer.Serialize(dict [ "tables", box (Array.ofList Given.tables); "lessons", box lessons ], JsonSerializerOptions(WriteIndented = true)) + "\n"

/// The notebook (.ipynb JSON) for the lessons, referencing the built koans in `bin`.
let generate (bin: string) : string =
    let byTitle = guidance.Value |> List.map (fun g -> g.Title, g) |> Map.ofList

    let intro =
        Markdown
            [ "# The Plasm Koans"
              ""
              "Each koan below has blanks (`__`) to fill in. Run the setup cell once, then run each koan cell:"
              "it runs the koan in its own sandbox, so every koan starts from the same data, and shows ✔ when"
              "it passes, or what to fill in or fix. Edit a cell and run it again until it passes."
              ""
              "Build the koans first: `dotnet build koans/Plasm.Koans`. The setup cell starts a throwaway"
              "PostgreSQL container, or uses the server named by `PLASM_KOANS_PG`." ]

    let setup =
        Code
            [ $"#I \"{bin}\""
              "#r \"Microsoft.Extensions.Logging.Abstractions.dll\""
              "#r \"Plasm.dll\""
              "#r \"Plasm.Postgres.dll\""
              "#r \"Npgsql.dll\""
              "#r \"Testcontainers.dll\""
              "#r \"Testcontainers.PostgreSql.dll\""
              "#r \"Plasm.Koans.Core.dll\""
              "#r \"Plasm.Koans.dll\""
              "open Plasm"
              "open Plasm.Koans"
              "open Plasm.Koans.Shop"
              "open Plasm.Koans.Notebook"
              ""
              "start ()" ]

    let lessons =
        Path.lessons
        |> List.collect (fun lesson ->
            let goal = byTitle.TryFind lesson.Title |> Option.map (fun g -> g.Goal) |> Option.defaultValue ""
            Markdown [ $"## {lesson.Title}"; ""; goal ] :: lessonCells lesson.Koans.Head.File)

    let cellJson (cell: Cell) =
        let lines, kind =
            match cell with
            | Markdown l -> l, "markdown"
            | Code l -> l, "code"

        let source = lines |> List.mapi (fun i l -> if i < lines.Length - 1 then l + "\n" else l) |> Array.ofList

        if kind = "markdown" then
            box (dict [ "cell_type", box "markdown"; "metadata", box (dict []); "source", box source ])
        else
            box (
                dict
                    [ "cell_type", box "code"
                      "execution_count", null
                      "metadata", box (dict [ "dotnet_interactive", box (dict [ "language", "fsharp" ]); "polyglot_notebook", box (dict [ "kernelName", "fsharp" ]) ])
                      "outputs", box [||]
                      "source", box source ]
            )

    let notebook =
        dict
            [ "cells", box (intro :: setup :: lessons |> List.map cellJson |> Array.ofList)
              "metadata",
              box (
                  dict
                      [ "kernelspec", box (dict [ "display_name", ".NET (F#)"; "language", "F#"; "name", ".net-fsharp" ])
                        "language_info", box (dict [ "name", "F#" ])
                        "polyglot_notebook", box (dict [ "kernelInfo", box (dict [ "defaultKernelName", "fsharp" ]) ]) ]
              )
              "nbformat", box 4
              "nbformat_minor", box 5 ]

    JsonSerializer.Serialize(notebook, JsonSerializerOptions(WriteIndented = true)) + "\n"
