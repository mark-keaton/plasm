namespace Plasm.Koans.Browser.Specs

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

type KoanInfo =
    { Lesson: int
      Index: int
      Name: string
      Body: string
      Discovers: string }

/// Scenario state: the page and what the scenario remembers.
type BrowserWorld() =
    member val Page: IPage = null with get, set
    member val Koan: KoanInfo option = None with get, set
    member val RunMs = 0L with get, set
    member val Ticks = 0 with get, set

[<AutoOpen>]
module Driving =
    let check (condition: bool) (message: string) = if not condition then failwith message

    let unit (t: Task<'a>) : Task = t :> Task

    let koanAt (page: IPage) (l: int) (k: int) =
        task {
            let! json = page.EvaluateAsync<string>($"() => JSON.stringify(window.koans.course.lessons[{l}].koans[{k}])")
            use doc = JsonDocument.Parse json
            let r = doc.RootElement

            return
                { Lesson = l
                  Index = k
                  Name = r.GetProperty("name").GetString()
                  Body = r.GetProperty("body").GetString()
                  Discovers = r.GetProperty("discovers").GetString() }
        }

    /// Finds a koan by name anywhere in the course.
    let koanNamed (page: IPage) (name: string) =
        task {
            let! pos =
                page.EvaluateAsync<int[]>(
                    "name => { const ls = window.koans.course.lessons; for (let l = 0; l < ls.length; l++) { const k = ls[l].koans.findIndex(k => k.name === name); if (k >= 0) return [l, k]; } return [-1, -1]; }",
                    name
                )

            check (pos.[0] >= 0) $"no koan named {name}"
            return! koanAt page pos.[0] pos.[1]
        }

    let show (page: IPage) (koan: KoanInfo) =
        page.EvaluateAsync($"() => window.koans.show({koan.Lesson}, {koan.Index})") :> Task

    /// Clicks Run and waits for the result.
    let run (page: IPage) =
        task {
            let started = Diagnostics.Stopwatch.StartNew()
            do! page.ClickAsync "#run"
            let! _ = page.WaitForFunctionAsync("() => document.getElementById('result').dataset.status", null, PageWaitForFunctionOptions(Timeout = Nullable 120000.f))
            return started.ElapsedMilliseconds
        }

    let result (page: IPage) =
        task {
            let! text = page.TextContentAsync "#result"
            let! status = page.GetAttributeAsync("#result", "data-status")
            return status, text
        }

    let contains (expected: string) (text: string) =
        check (text.Contains expected) $"expected the koan to show:\n{expected}\n\nbut it showed:\n{text}"

/// Feature: Running a koan in the browser
[<Binding>]
type RunningSteps(w: BrowserWorld) =

    let first () = koanAt w.Page 0 0

    let answered (koan: KoanInfo) = fill koan.Body answers.Value.[koan.Name]

    let runWith (koan: KoanInfo) (code: string) =
        task {
            do! show w.Page koan
            do! typeCode w.Page (code)
            let! ms = run w.Page
            w.Koan <- Some koan
            w.RunMs <- ms
        }

    [<StepDefinition("the koans page has finished loading")>]
    member _.PageReady() =
        task {
            let! page = openKoans ()
            w.Page <- page
        }
        |> unit

    [<AfterScenario>]
    member _.Close() =
        if not (isNull w.Page) then w.Page.CloseAsync() else Task.CompletedTask

    // A koan with its blank unfilled asks for it

    [<StepDefinition("the learner runs the first koan without changing it")>]
    member _.RunShipped() =
        task {
            let! koan = first ()
            do! runWith koan koan.Body
        }
        |> unit

    [<StepDefinition("the koan shows that it is not yet passed")>]
    member _.NotPassed() =
        task {
            let! status, text = result w.Page
            check (status = "unanswered") $"status {status}: {text}"
            contains "Fill in the blank (__)" text
        }
        |> unit

    [<StepDefinition("it points at the line of the blank in the editor")>]
    member _.PointsAtBlank() =
        task {
            let! _, text = result w.Page
            contains $"on line {firstBlankLine w.Koan.Value.Body}" text
        }
        |> unit

    // A correct answer passes

    [<StepDefinition("the learner fills in the first koan's blanks with its reference answer and runs it")>]
    member _.RunAnswered() =
        task {
            let! koan = first ()
            do! runWith koan (answered koan)
        }
        |> unit

    [<StepDefinition("the koan shows that it passed")>]
    member _.Passed() =
        task {
            let! status, text = result w.Page
            check (status = "passed") $"status {status}: {text}"
        }
        |> unit

    [<StepDefinition("the next koan is offered")>]
    member _.NextOffered() =
        task {
            do! w.Page.ClickAsync "#next"
            let! second = koanAt w.Page 0 1
            let! name = w.Page.TextContentAsync "#name"
            check (name = second.Name) $"after Next the page shows {name}, not {second.Name}"
        }
        |> unit

    // A wrong answer shows what was expected and what happened

    [<StepDefinition("the learner fills in the first koan's first blank with a wrong value and runs it")>]
    member _.RunWrong() =
        task {
            let! koan = first ()
            do! runWith koan (fill koan.Body ("\"Gadget\"" :: List.tail answers.Value.[koan.Name]))
        }
        |> unit

    [<StepDefinition("the koan shows the expected and the actual value, displayed as F# Interactive would")>]
    member _.ExpectedActual() =
        task {
            let! _, text = result w.Page
            let! expected = w.Page.TextContentAsync "#result .expected pre"
            let! actual = w.Page.TextContentAsync "#result .actual pre"
            check (expected = "\"Gadget\"" && actual = "\"Widget\"") $"the koan shows expected {expected} and actual {actual}:\n{text}"
        }
        |> unit

    [<StepDefinition("it shows what the koan teaches")>]
    member _.Teaches() =
        task {
            let! text = w.Page.TextContentAsync "#result .meditate"
            contains w.Koan.Value.Discovers text
        }
        |> unit

    // A koan that talks to the database shows the SQL it generated

    [<StepDefinition("the learner runs a database koan with a wrong answer")>]
    member _.RunDatabaseWrong() =
        task {
            let! koan = koanNamed w.Page "Extending makes a new query"
            do! runWith koan (fill koan.Body ("99L" :: List.tail answers.Value.[koan.Name]))
        }
        |> unit

    [<StepDefinition("the koan shows the SQL Plasm generated, formatted by clause")>]
    member _.ShowsSql() =
        task {
            let! _, text = result w.Page
            let! sql = w.Page.TextContentAsync "#result .sql pre"
            contains "\n    FROM \"orders\" AS \"o\"" sql
        }
        |> unit

    // A compile error points at the code

    [<StepDefinition("the learner writes code in a koan that does not compile and runs it")>]
    member _.RunBroken() =
        task {
            // Pass the first koan, so the check below has an earlier result to keep.
            let! firstKoan = first ()
            do! runWith firstKoan (answered firstKoan)
            let! second = koanAt w.Page 0 1
            do! runWith second "    let total: int = \"one\"\n    ()"
        }
        |> unit

    [<StepDefinition("the koan shows the compiler's message at its line and column")>]
    member _.CompileError() =
        task {
            let! status, text = result w.Page
            check (status = "compile") $"status {status}: {text}"
            contains "Line 1, column" text
            contains "FS0001" text
        }
        |> unit

    [<StepDefinition("no earlier koan's result changes")>]
    member _.EarlierKept() =
        task {
            let! state = w.Page.GetAttributeAsync("nav button[data-koan=\"0/0\"]", "data-state")
            check (state = "passed") $"the first koan now shows {state}"
        }
        |> unit

    // The page stays responsive while a koan compiles and runs

    [<StepDefinition("the learner runs a koan")>]
    member _.RunMeasured() =
        task {
            let! koan = first ()
            do! show w.Page koan
            do! typeCode w.Page (answered koan)
            // Count main-thread timer ticks (every 50 ms) while the koan runs.
            do! w.Page.EvaluateAsync("() => { window.ticks = 0; window.ticker = setInterval(() => window.ticks++, 50); }") :> Task
            let! ms = run w.Page
            let! ticks = w.Page.EvaluateAsync<int>("() => { clearInterval(window.ticker); return window.ticks; }")
            w.RunMs <- ms
            w.Ticks <- ticks
        }
        |> unit

    [<StepDefinition("the page keeps responding to input while it compiles and runs")>]
    member _.Responsive() =
        // A blocked main thread would fire almost no ticks; allow for timer jitter.
        let expected = int (w.RunMs / 50L) / 2
        check (w.Ticks >= expected) $"{w.Ticks} timer ticks in {w.RunMs} ms (expected at least {expected})"

    // A koan that never finishes can be stopped

    [<StepDefinition("the learner runs a koan whose code loops forever")>]
    member _.RunForever() =
        task {
            let! koan = first ()
            do! show w.Page koan
            do! typeCode w.Page ("    while true do ()")
            do! w.Page.ClickAsync "#run"
            do! Task.Delay 3000
            let! status = w.Page.GetAttributeAsync("#result", "data-status")
            check (isNull status) $"the endless koan already finished with {status}"
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("the learner can stop it without reloading the page")>]
    member _.Stops() =
        task {
            do! w.Page.ClickAsync "#stop"
            let! _ = w.Page.WaitForFunctionAsync("() => document.getElementById('result').dataset.status === 'stopped'")
            ()
        }
        |> unit

    [<StepDefinition("later koans can still be run")>]
    [<StepDefinition("the page carries on")>]
    member _.Continues() =
        task {
            let! koan = first ()
            do! runWith koan (answered koan)
            let! status, text = result w.Page
            check (status = "passed") $"status {status}: {text}"
        }
        |> unit

    // An exception is reported as the koan's failure

    [<StepDefinition("the learner fills in a koan with code that throws")>]
    member _.RunThrows() =
        task {
            let! koan = first ()
            do! runWith koan (fill koan.Body ("failwith \"the learner's code threw\"" :: List.tail answers.Value.[koan.Name]))
        }
        |> unit

    [<StepDefinition("the koan shows the exception's message")>]
    member _.ShowsException() =
        task {
            let! _, text = result w.Page
            contains "the learner's code threw" text
        }
        |> unit
