/// `dotnet run --project koans/Plasm.Koans` walks the path to enlightenment.
///   --lesson <number or title>   only that lesson
///   --list                       every lesson with how many of its koans pass
///   --all                        run every koan instead of stopping at the first unsatisfied one
///   --report <file>              also write every result as JSON (for the koans' own checks)
///   --notebook <file> [--bin <dir>]  write the koans as a notebook (no database needed)
///   --course <file>              write the course as JSON for the browser (no database needed)
module Plasm.Koans.Program

open System
open System.IO
open System.Text.Json
open Plasm
open Plasm.Postgres
open Plasm.Koans.Runner

let private arg (name: string) (argv: string[]) =
    argv |> Array.tryFindIndex ((=) name) |> Option.bind (fun i -> Array.tryItem (i + 1) argv)

let private writeReport (path: string) (results: KoanResult list) =
    let rows =
        results
        |> List.map (fun r ->
            let status, file, line, expected, actual, message =
                match r.Outcome with
                | Passed -> "passed", "", 0, "", "", ""
                | Unanswered(f, l) -> "unanswered", relative f, l, "", "", ""
                | Failed(e, a) -> "failed", relative r.Koan.File, r.Koan.Line, e, a, ""
                | Threw(t, m) -> "threw", relative r.Koan.File, r.Koan.Line, "", "", $"{t}: {m}"

            dict
                [ "lesson", box r.Lesson.Title
                  "koan", box r.Koan.Name
                  "status", box status
                  "file", box file
                  "line", box line
                  "expected", box expected
                  "actual", box actual
                  "message", box message ])

    File.WriteAllText(path, JsonSerializer.Serialize(rows, JsonSerializerOptions(WriteIndented = true)))

[<EntryPoint>]
let main argv =
    match arg "--course" argv with
    | Some path ->
        File.WriteAllText(path, Notebook.course ())
        printfn "Wrote the koans course to %s" path
        0
    | None ->

    match arg "--notebook" argv with
    | Some path ->
        let bin = arg "--bin" argv |> Option.defaultValue "../Plasm.Koans/bin/Debug/net11.0"
        File.WriteAllText(path, Notebook.generate bin)
        printfn "Wrote the koans notebook to %s" path
        0
    | None ->

    let guidance = Guidance.load ()
    let all = Plasm.Koans.Path.lessons
    let total = all |> List.sumBy (fun l -> l.Koans.Length)

    let selected =
        match arg "--lesson" argv with
        | None -> all
        | Some wanted ->
            all
            |> List.indexed
            |> List.filter (fun (i, l) -> l.Title = wanted || $"%02d{i + 1}" = wanted || string (i + 1) = wanted)
            |> List.map snd

    if selected.IsEmpty then
        eprintfn "No lesson matches. Lessons:"
        all |> List.iteri (fun i l -> eprintfn "  %02d %s" (i + 1) l.Title)
        2
    else

    let listing = Array.contains "--list" argv
    let runAll = listing || Array.contains "--all" argv

    printfn "Plasm Koans: the path to enlightenment"
    printfn ""

    let db = (Database.provision ()).GetAwaiter().GetResult()
    printfn "Database: %s" db.Description
    printfn ""

    let commands = ResizeArray<CommandEvent>()

    // The koans' own queries include ones Plasm refuses on purpose (lesson 21
    // checks them), so the course's repository is written as unchecked.
    let repo =
        Repo
            .Start(
                Shop.Db.model,
                Postgres.connect db.ConnectionString,
                queriesIn = typeof<Shop.Customer>.Assembly,
                options =
                    { RepoOptions.Unchecked with
                        OnCommand = Some commands.Add
                        IncludeParameterValues = true }
            )
            .GetAwaiter()
            .GetResult()

    let results = ResizeArray<KoanResult>()
    let mutable stopped: KoanResult option = None

    try
        for lesson in selected do
            if stopped.IsNone then
                if not listing then printfn "  %s" lesson.Title

                for koan in lesson.Koans do
                    if stopped.IsNone then
                        let r = (run repo commands lesson koan).GetAwaiter().GetResult()
                        results.Add r

                        match r.Outcome with
                        | Passed -> if not listing then printfn "    ✔ %s" koan.Name
                        | _ ->
                            if not listing then printfn "    ✘ %s" koan.Name
                            if not runAll then stopped <- Some r
    finally
        db.Dispose().GetAwaiter().GetResult()

    let passed = results |> Seq.filter (fun r -> r.Outcome = Passed) |> Seq.length
    printfn ""

    if listing then
        printfn "Lessons:"

        for i, lesson in List.indexed all do
            let mine = results |> Seq.filter (fun r -> r.Lesson.Title = lesson.Title) |> List.ofSeq
            let ok = mine |> List.filter (fun r -> r.Outcome = Passed) |> List.length
            printfn "  %02d %-40s %d of %d koans pass" (i + 1) lesson.Title ok lesson.Koans.Length
    else
        match stopped with
        | Some r ->
            printfn "%s" (describeNext guidance r)
            printfn ""
            printfn "  Your path so far: %s" (progressBar passed total)
        | None when selected.Length = all.Length && passed = total ->
            printfn "The path is complete: all %d koans of %d lessons are passed." total all.Length

            for lesson in all do
                printfn "  ✔ %s" lesson.Title
        | None ->
            let failed = results |> Seq.filter (fun r -> r.Outcome <> Passed) |> Seq.length
            printfn "%d passed, %d not yet." passed failed

    match arg "--report" argv with
    | Some path -> writeReport path (List.ofSeq results)
    | None -> ()

    if db.Description.StartsWith "a throwaway" then
        printfn ""
        printfn "Removed the throwaway database."

    0
