/// The course as the browser koans show it (koans/browser): each lesson's
/// helpers and each koan's code, read from the lesson sources, with what the
/// lesson specifications say it teaches. `--course` writes it as course.json.
module Plasm.Koans.Course

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Plasm.Koans.Runner

let private guidance = lazy (Guidance.load ())

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

/// The course as data for the browser: every lesson's helpers and every
/// koan's code and guidance, in order.
let course () : string =
    let byTitle = guidance.Value |> List.map (fun g -> g.Title, g) |> Map.ofList

    // The shop's source, next to the lessons' sources (koans/Plasm.Koans.Core/Shop.fs).
    let shop =
        let lessonsDir = IO.Path.GetDirectoryName Path.lessons.Head.Koans.Head.File
        let file = IO.Path.Combine(lessonsDir, "..", "..", "Plasm.Koans.Core", "Shop.fs")
        Given.parse "Shop.fs" (IO.File.ReadAllLines file)

    // The test helpers a koan may use from Koans.fs.
    let koans =
        let lessonsDir = IO.Path.GetDirectoryName Path.lessons.Head.Koans.Head.File
        let file = IO.Path.Combine(lessonsDir, "..", "..", "Plasm.Koans.Core", "Koans.fs")
        Given.parse "Koans.fs" (IO.File.ReadAllLines file)
        |> List.filter (fun d -> List.contains d.Name [ "ok"; "err"; "commands" ])

    let given (helpers: Given.Definition list) (body: string) =
        let g = Given.forKoan shop helpers body
        let g = { g with Definitions = Given.forRepo body @ g.Definitions @ Given.fromKoans koans body }

        dict
            [ "tables", box (Array.ofList g.Tables)
              "definitions",
              box (
                  g.Definitions
                  |> List.map (fun (d, marked) ->
                      dict
                          [ "name", box d.Name
                            "source", box d.Source
                            // Where it comes from: Plasm's koans are about Plasm and your
                            // app; the rest only helps a koan check its answer.
                            "kind",
                            box (
                                match d.Source with
                                | "Shop.fs" -> "your app"
                                | "Koans.fs" -> "test helper"
                                | "every koan" -> "Plasm"
                                | _ -> "this lesson"
                            )
                            "code", box (String.concat "\n" d.Code)
                            "marked", box (Array.ofList marked) ])
                  |> Array.ofList
              ) ]

    // What the page shows of a koan: its code, without the frame every koan
    // shares (fun repo -> task { ... }), which the page puts back to run it.
    // A koan whose task is its subject (lesson 01's let!) is shown whole.
    let framed (k: KoanSource) =
        k.IsTask
        && (match k.Body with
            | first :: rest -> first.Trim() = "task {" && rest <> [] && (List.last rest).Trim() = "}"
            | [] -> false)

    let shown (k: KoanSource) =
        dedent (if framed k then k.Body.[1 .. k.Body.Length - 2] else k.Body)

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
                                // Whether the page puts back task { ... } around the code.
                                "framed", box (framed k)
                                "body", box (String.concat "\n" (shown k))
                                "discovers", box (kg |> Option.map (fun x -> x.Discovers) |> Option.defaultValue "")
                                "teaches", box (kg |> Option.map (fun x -> x.Teaches) |> Option.defaultValue "")
                                // Shown after a wrong answer; "" when the koan has none.
                                "hint", box (kg |> Option.map (fun x -> x.Hint) |> Option.defaultValue "")
                                "given", box (given helperDefinitions (String.concat "\n" k.Body)) ])
                      |> Array.ofList
                  ) ])
        |> Array.ofList

    JsonSerializer.Serialize(dict [ "tables", box (Array.ofList Given.tables); "lessons", box lessons ], JsonSerializerOptions(WriteIndented = true)) + "\n"
