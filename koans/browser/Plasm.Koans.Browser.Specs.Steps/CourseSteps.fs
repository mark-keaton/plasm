namespace Plasm.Koans.Browser.Specs

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// A koan as the lesson specifications describe it.
type SpecKoan =
    { Lesson: string
      Koan: string
      Discovers: string
      Teaches: string }

module Specs =
    let private row = Regex(@"^\s*\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*\|\s*$")

    let koans =
        lazy
            (Directory.GetFiles(Path.Combine(root, "koans", "features", "lessons"), "*.feature")
             |> Array.sort
             |> Array.collect (fun f ->
                 let lines = File.ReadAllLines f
                 let title = lines |> Array.pick (fun l -> let t = l.Trim() in if t.StartsWith "Feature:" then Some(t.Substring(8).Trim()) else None)

                 lines
                 |> Array.choose (fun l ->
                     let m = row.Match l

                     if m.Success && m.Groups.[1].Value <> "koan" then
                         Some
                             { Lesson = title
                               Koan = m.Groups.[1].Value
                               Discovers = m.Groups.[2].Value.Replace("\\|", "|")
                               Teaches = m.Groups.[3].Value }
                     else
                         None))
             |> List.ofArray)

/// Feature: Walking the path in the browser
[<Binding>]
type CourseSteps(w: BrowserWorld) =

    let mutable runs: (string * string * string) list = [] // koan, status, text
    let mutable edit = ""

    /// Runs every koan through the page's engine (answered or as shipped).
    let runAll (answered: bool) =
        task {
            let! count = w.Page.EvaluateAsync<int[]>("() => window.koans.course.lessons.map(l => l.koans.length)")

            let results = ResizeArray()

            for l in 0 .. count.Length - 1 do
                for k in 0 .. count.[l] - 1 do
                    let! koan = koanAt w.Page l k
                    let body = if answered then fill koan.Body answers.Value.[koan.Name] else koan.Body

                    let! json =
                        w.Page.EvaluateAsync<string>(
                            "async ([l, k, b]) => JSON.stringify(await window.koans.runKoan(l, k, b))",
                            box [| box l; box k; box body |]
                        )

                    use doc = JsonDocument.Parse json
                    let text = match doc.RootElement.TryGetProperty "text" with | true, t -> t.GetString() | _ -> json
                    results.Add(koan.Name, doc.RootElement.GetProperty("status").GetString(), text)

            runs <- List.ofSeq results
        }

    let passKoan (l: int) (k: int) =
        task {
            let! koan = koanAt w.Page l k
            do! show w.Page koan
            do! w.Page.FillAsync("#code", fill koan.Body answers.Value.[koan.Name])
            let! _ = run w.Page
            ()
        }

    // The course is the same course

    [<StepDefinition("the browser course is compared with the koans' lesson specifications")>]
    member _.Compare() = ()

    [<StepDefinition("it has every lesson, in order, and every koan, in order, with the same guidance")>]
    member _.SameCourse() =
        task {
            let! json =
                w.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.koans.course.lessons.flatMap(l => l.koans.map(k => [l.title, k.name, k.discovers, k.teaches])))"
                )

            let inPage =
                JsonSerializer.Deserialize<string[][]>(json) |> Array.map (fun a -> a.[0], a.[1], a.[2], a.[3]) |> List.ofArray

            let specified = Specs.koans.Value |> List.map (fun s -> s.Lesson, s.Koan, s.Discovers, s.Teaches)
            check (inPage = specified) $"the browser course differs from the lesson specifications:\npage %A{inPage |> List.truncate 3}\nspec %A{specified |> List.truncate 3}"
        }
        |> unit

    [<StepDefinition("every koan is filled in with its reference answer and run in the page")>]
    [<StepDefinition("every koan is run with its reference answer against PGlite in the page")>]
    member _.RunAnswered() = runAll true |> unit

    [<StepDefinition("every koan passes")>]
    member _.AllPass() =
        let failing = runs |> List.filter (fun (_, s, _) -> s <> "passed") |> List.map (fun (k, s, t) -> $"{k}: {s}\n{t}")
        check (runs.Length = Specs.koans.Value.Length) $"{runs.Length} koans ran"
        check failing.IsEmpty (String.concat "\n\n" failing)

    [<StepDefinition("every koan is run in the page as shipped")>]
    member _.RunShipped() = runAll false |> unit

    [<StepDefinition("every koan asks for its blank")>]
    member _.AllAsk() =
        let wrong = runs |> List.filter (fun (_, s, _) -> s <> "unanswered") |> List.map (fun (k, s, t) -> $"{k}: {s}\n{t}")
        check (runs.Length = Specs.koans.Value.Length) $"{runs.Length} koans ran"
        check wrong.IsEmpty (String.concat "\n\n" wrong)

    // Progress is kept

    [<StepDefinition("the learner has passed the first two koans and edited the third")>]
    member _.Progress() =
        task {
            do! passKoan 0 0
            do! passKoan 0 1
            let! third = koanAt w.Page 0 2
            do! show w.Page third
            edit <- third.Body + "\n    // my notes"
            do! w.Page.FillAsync("#code", edit)
        }
        |> unit

    [<StepDefinition("the learner reloads the page")>]
    member _.Reload() =
        task {
            let! _ = w.Page.ReloadAsync()
            let! _ = w.Page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }
        |> unit

    [<StepDefinition("the first two koans are shown as passed")>]
    member _.StillPassed() =
        task {
            for k in [ "0/0"; "0/1" ] do
                let! state = w.Page.GetAttributeAsync($"nav button[data-koan=\"{k}\"]", "data-state")
                check (state = "passed") $"koan {k} shows {state} after the reload"
        }
        |> unit

    [<StepDefinition("the third koan shows the learner's edit")>]
    member _.EditKept() =
        task {
            let! third = koanAt w.Page 0 2
            do! show w.Page third
            let! code = w.Page.InputValueAsync "#code"
            check (code = edit) $"the third koan shows:\n{code}"
        }
        |> unit

    [<StepDefinition("the learner has edited a koan")>]
    member _.Edited() =
        task {
            let! koan = koanAt w.Page 0 0
            do! show w.Page koan
            do! w.Page.FillAsync("#code", "    // not what was there")
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("the learner resets it")>]
    member _.Reset() = w.Page.ClickAsync "#reset"

    [<StepDefinition("it shows its original code with its blanks")>]
    member _.Original() =
        task {
            let! code = w.Page.InputValueAsync "#code"
            check (code = w.Koan.Value.Body) $"after reset the koan shows:\n{code}"
        }
        |> unit

    [<StepDefinition("the learner has passed some koans")>]
    member _.PassedSome() = passKoan 0 0

    [<StepDefinition("every lesson shows how many of its koans are passed")>]
    member _.LessonProgress() =
        task {
            let! counts = w.Page.EvaluateAsync<int[]>("() => window.koans.course.lessons.map(l => l.koans.length)")

            for l in 0 .. counts.Length - 1 do
                let! text = w.Page.TextContentAsync($"nav h2[data-lesson=\"{l}\"] .progress")
                let expected = if l = 0 then $"1 of {counts.[l]}" else $"0 of {counts.[l]}"
                check (text = expected) $"lesson {l} shows '{text}', expected '{expected}'"
        }
        |> unit

    // The editor helps

    [<StepDefinition("a koan is shown")>]
    member _.Shown() =
        task {
            let! koan = koanAt w.Page 0 0
            do! show w.Page koan
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("each blank in its code is highlighted")>]
    member _.Highlighted() =
        task {
            let! marks = w.Page.Locator("#highlight mark.blank").CountAsync()
            let blanks = Regex.Matches(w.Koan.Value.Body, @"(?<![\w.])__(?!\w)").Count
            check (marks = blanks) $"{marks} highlighted blanks for {blanks} blanks"
        }
        |> unit

    [<StepDefinition("it links to the acceptance scenario it teaches")>]
    member _.Links() =
        task {
            let spec = Specs.koans.Value |> List.find (fun s -> s.Koan = w.Koan.Value.Name)
            let feature, scenario = let i = spec.Teaches.IndexOf ": " in spec.Teaches.Substring(0, i), spec.Teaches.Substring(i + 2)
            let! href = w.Page.GetAttributeAsync("#teaches", "href")
            check (href = $"spec/{feature}.feature") $"the koan links to {href}"
            let! text = w.Page.EvaluateAsync<string>("async href => (await fetch(href)).text()", href)
            check (text.Contains $"Scenario: {scenario}" || text.Contains $"Scenario Outline: {scenario}") $"{href} does not contain the scenario '{scenario}'"
        }
        |> unit
