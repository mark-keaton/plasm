namespace Plasm.Koans.Specs

open System
open System.Diagnostics
open System.IO
open Reqnroll
open Plasm.Koans.Specs.Workspace

/// Scenario state shared by the koans' step classes.
type KoansWorld() =
    member val Program: unit -> Threading.Tasks.Task<string> = shipped with get, set
    member val UseServer = true with get, set
    member val Runs: RunResult list = [] with get, set
    member val Koan = "" with get, set
    member this.Last = List.last this.Runs

[<AutoOpen>]
module Checks =
    let check (condition: bool) (message: string) = if not condition then failwith message

    let contains (text: string) (output: string) =
        check (output.Contains text) $"expected the output to contain:\n{text}\n\nbut it was:\n{output}"

    let lacks (text: string) (output: string) =
        check (not (output.Contains text)) $"expected the output not to contain:\n{text}\n\nbut it was:\n{output}"

    let unit (t: Threading.Tasks.Task<'a>) : Threading.Tasks.Task = t :> Threading.Tasks.Task

/// Feature: The path to enlightenment
[<Binding>]
type PathSteps(w: KoansWorld) =

    let specs () = koanSpecs ()
    let firstLessonKoans () = specs () |> List.filter (fun s -> s.Number = 1)

    /// Every reference answer, except for the koans `overrides` names.
    let answeredExcept (overrides: Map<string, string list option>) : Answering =
        let a = answers ()
        fun koan -> match overrides.TryFind koan with Some o -> o | None -> a.TryFind koan

    /// Answers every koan before `koan` (in source order), applies `own` to it, and leaves the rest.
    let answeredUpTo (koan: string) (own: string list) : Answering =
        let a = answers ()
        let order = koansInSource () |> List.map snd
        let before = order |> List.takeWhile (fun k -> k <> koan) |> Set.ofList
        fun k -> if k = koan then Some own elif before.Contains k then a.TryFind k else None

    let run args =
        task {
            let! program = w.Program()
            let! r = runKoans program args w.UseServer
            w.Runs <- w.Runs @ [ r ]
        }

    [<StepDefinition("a fresh copy of the koans")>]
    member _.Fresh() = w.Program <- shipped

    [<StepDefinition("the learner runs the koans")>]
    member _.Run() = run "" |> unit

    [<StepDefinition("the first koan of the first lesson is reported as the next step")>]
    member _.FirstIsNext() =
        let first = (firstLessonKoans ()).Head
        contains $"The koan \"{first.Koan}\" in \"{first.Lesson}\"" w.Last.Output

    [<StepDefinition("the report names the lesson, the koan and the file and line of its first blank")>]
    member _.NamesBlank() =
        let file = (lessonFiles ()).Head
        let lines = File.ReadAllLines file
        let name, start, last = (regions lines).Head
        let line, _ = (blanksIn lines start last).Head
        contains $"Fill in the blank (__) at Lessons/{Path.GetFileName file}:{line + 1}" w.Last.Output

    [<StepDefinition("the report shows the lesson's guidance for that koan")>]
    member _.Guidance() = contains $"Meditate on this: {(firstLessonKoans ()).Head.Discovers}" w.Last.Output

    [<StepDefinition("no later koan is run")>]
    member _.NoLater() = lacks (firstLessonKoans ()).[1].Koan w.Last.Output

    [<StepDefinition("the learner has filled in the first koan correctly")>]
    member _.FirstCorrect() =
        let first = (firstLessonKoans ()).Head.Koan
        let a = answers ()
        w.Program <- fun () -> variant "first-answered" (fun k -> if k = first then a.TryFind k else None)

    [<StepDefinition("the first koan is reported as passed")>]
    member _.FirstPassed() = contains $"✔ {(firstLessonKoans ()).Head.Koan}" w.Last.Output

    [<StepDefinition("the second koan is reported as the next step")>]
    member _.SecondIsNext() = contains $"The koan \"{(firstLessonKoans ()).[1].Koan}\"" w.Last.Output

    [<StepDefinition("progress shows 1 of the total number of koans")>]
    member _.Progress() =
        let total = koansInSource () |> List.length
        contains $"] 1 of {total}" w.Last.Output

    [<StepDefinition("the learner has filled in the first koan with a wrong value")>]
    member _.FirstWrong() =
        let first = (firstLessonKoans ()).Head.Koan
        let rest = (answers ()).[first] |> List.tail
        w.Program <- fun () -> variant "first-wrong" (fun k -> if k = first then Some("\"Gadget\"" :: rest) else None)

    [<StepDefinition("the report shows the expected and the actual value, displayed as F# Interactive would")>]
    member _.ExpectedActual() =
        contains "Expected:\n    \"Gadget\"" w.Last.Output
        contains "Actual:\n    \"Widget\"" w.Last.Output

    [<StepDefinition("the koan is still the next step")>]
    member _.StillNext() = contains $"The koan \"{(firstLessonKoans ()).Head.Koan}\"" w.Last.Output

    [<StepDefinition("the learner has reached a koan that asserts on generated SQL")>]
    member _.SqlKoan() = w.Koan <- "Counting transfers no rows"

    [<StepDefinition("has filled it in with a wrong value")>]
    member _.SqlWrong() =
        let koan = w.Koan
        // Its last blank is the assertion on the SQL sent.
        let own = (answers ()).[koan] |> fun xs -> List.mapi (fun i a -> if i = xs.Length - 1 then "false" else a) xs
        w.Program <- fun () -> variant "sql-wrong" (answeredUpTo koan own)

    [<StepDefinition("the report shows the SQL Plasm generated, formatted by clause")>]
    member _.SqlShown() =
        contains "Plasm generated:" w.Last.Output
        contains "\n    FROM \"orders\" AS \"t0\"\n    WHERE" w.Last.Output

    [<StepDefinition("the learner has filled in a koan with code that throws")>]
    member _.Throws() =
        let first = (firstLessonKoans ()).Head.Koan
        let rest = (answers ()).[first] |> List.tail
        w.Program <- fun () -> variant "throws" (fun k -> if k = first then Some("failwith \"the learner's code threw\"" :: rest) else None)

    [<StepDefinition("the report names the koan and shows the exception's message")>]
    member _.ExceptionShown() =
        contains $"The koan \"{(firstLessonKoans ()).Head.Koan}\"" w.Last.Output
        contains "the learner's code threw" w.Last.Output

    [<StepDefinition("the runner does not crash")>]
    member _.NoCrash() =
        check (w.Last.ExitCode = 0) $"exit code {w.Last.ExitCode}:\n{w.Last.Output}"
        contains "Your path so far" w.Last.Output

    [<StepDefinition("every koan has been filled in correctly")>]
    member _.AllCorrect() = w.Program <- answered

    [<StepDefinition("the report says the path is complete and shows every lesson as passed")>]
    member _.Complete() =
        contains "The path is complete" w.Last.Output

        for s in specs () |> List.map (fun s -> s.Lesson) |> List.distinct do
            contains $"✔ {s}" w.Last.Output

    [<StepDefinition("no koans database is configured")>]
    member _.NoDatabase() = w.UseServer <- false

    [<StepDefinition("a throwaway PostgreSQL database is started with the koans' schema and data")>]
    member _.Throwaway() = contains "Database: a throwaway PostgreSQL container" w.Last.Output

    [<StepDefinition("it is removed when the run ends")>]
    member _.Removed() =
        contains "Removed the throwaway database." w.Last.Output
        use p = Process.Start(ProcessStartInfo("docker", "ps -q --filter label=plasm-koans=true", RedirectStandardOutput = true))
        let left = p.StandardOutput.ReadToEnd().Trim()
        p.WaitForExit()
        check (left = "") $"koans containers still running: {left}"

    [<StepDefinition("the PLASM_KOANS_PG environment variable names a PostgreSQL server")>]
    member _.Configured() = w.UseServer <- true

    [<StepDefinition("the koans run against a database created on that server")>]
    member _.OnServer() = contains "Database: the server named by PLASM_KOANS_PG" w.Last.Output

    [<StepDefinition("a koan that inserts, updates and deletes rows")>]
    member _.Writing() = w.Program <- answered

    [<StepDefinition("the learner runs the koans twice")>]
    member _.Twice() =
        task {
            do! run "--all"
            do! run "--all"
        }
        |> unit

    [<StepDefinition("the koan sees the same starting data both times")>]
    [<StepDefinition("the next koan sees the starting data, not the earlier koan's changes")>]
    member _.SameData() =
        for r in w.Runs do
            let failed = r.Report |> List.filter (fun e -> field "status" e <> "passed")
            let names = failed |> List.map (field "koan")
            check (failed.IsEmpty && not r.Report.IsEmpty) $"koans not passing: %A{names}\n{r.Output}"

    [<StepDefinition("the learner runs the koans for the lesson \"(.*)\"")>]
    member _.OneLesson(lesson: string) =
        w.Koan <- lesson
        run $"--lesson \"{lesson}\"" |> unit

    [<StepDefinition("only that lesson's koans are run, in order, stopping at the first unsatisfied one")>]
    member _.OnlyThatLesson() =
        let mine = specs () |> List.filter (fun s -> s.Lesson = w.Koan)
        let others = specs () |> List.filter (fun s -> s.Lesson <> w.Koan) |> List.map (fun s -> s.Lesson) |> List.distinct
        contains $"  {w.Koan}" w.Last.Output
        contains $"The koan \"{mine.Head.Koan}\" in \"{w.Koan}\"" w.Last.Output

        for o in others do
            lacks $"  {o}\n" w.Last.Output

    [<StepDefinition("the learner asks for the list of lessons")>]
    member _.List() = run "--list" |> unit

    [<StepDefinition("every lesson is listed in order with how many of its koans pass")>]
    member _.Listed() =
        let out = w.Last.Output
        let lessons = specs () |> List.map (fun s -> s.Number, s.Lesson) |> List.distinct

        let positions =
            lessons
            |> List.map (fun (n, title) ->
                let total = specs () |> List.filter (fun s -> s.Lesson = title) |> List.length
                let line = $"%02d{n} {title}"
                contains line out
                contains $"0 of {total} koans pass" out
                out.IndexOf line)

        check (positions = List.sort positions) "lessons are not listed in order"
