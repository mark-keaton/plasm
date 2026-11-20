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
      Teaches: string
      /// One sentence pointing at where the answer comes from; "" for none.
      Hint: string }

module Specs =
    // koan | the learner discovers | teaches | hint (the hint's cell may be empty).
    let private row = Regex(@"^\s*\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?:(?<!\\)\|\s*(.*?)\s*)?\|\s*$")

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
                               Teaches = m.Groups.[3].Value
                               Hint = m.Groups.[4].Value.Replace("\\|", "|") }
                     else
                         None))
             |> List.ofArray)

/// Feature: Walking the path in the browser
[<Binding>]
type CourseSteps(w: BrowserWorld) =

    let mutable runs: (string * string * string) list = [] // koan, status, text
    let mutable edit = ""
    let mutable addresses: string list = []

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
            do! typeCode w.Page (fill koan.Body answers.Value.[koan.Name])
            let! _ = run w.Page
            ()
        }

    // The course is the same course

    /// Writes to the page's storage (localStorage) as an earlier page would have.
    let stored (entries: (string * string) list) =
        task {
            for key, json in entries do
                do! w.Page.EvaluateAsync("([k, v]) => localStorage.setItem(k, v)", box [| box key; box json |]) :> Task
        }

    [<StepDefinition("the browser course is compared with the koans' lesson specifications")>]
    member _.Compare() = ()

    [<StepDefinition("it has every lesson, in order, and every koan, in order, with the same guidance")>]
    member _.SameCourse() =
        task {
            let! json =
                w.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.koans.course.lessons.flatMap(l => l.koans.map(k => [l.title, k.name, k.discovers, k.teaches, k.hint])))"
                )

            let inPage =
                JsonSerializer.Deserialize<string[][]>(json) |> Array.map (fun a -> a.[0], a.[1], a.[2], a.[3], a.[4]) |> List.ofArray

            let specified = Specs.koans.Value |> List.map (fun s -> s.Lesson, s.Koan, s.Discovers, s.Teaches, s.Hint)
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
            do! typeCode w.Page (edit)
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
            let! code = codeOf w.Page
            check (code = edit) $"the third koan shows:\n{code}"
        }
        |> unit

    [<StepDefinition("this browser holds the code and progress of an older course, saved for the second koan of lesson 1")>]
    member _.OlderCourse() =
        // The course before lessons 01-04 were added kept edits by position.
        stored [ "plasm-koans:edits", JsonSerializer.Serialize(dict [ "0/1", "    let ada = 1\n    ada |> shouldBe CustomerId(1)" ])
                 "plasm-koans:passed", JsonSerializer.Serialize [| "0/1" |] ]
        |> unit

    [<StepDefinition("this browser holds an edit to the koan \"(.*)\" made when its code read differently")>]
    member _.EditToOlderKoan(name: string) =
        stored [ "plasm-koans:koans", JsonSerializer.Serialize(dict [ name, dict [ "base", "    // an older version of this koan"; "edit", "    // the learner's edit to it" ] ]) ]
        |> unit

    [<StepDefinition("the koan \"(.*)\" shows its original code")>]
    member _.ShowsOriginal(name: string) =
        task {
            let! koan = koanNamed w.Page name
            do! show w.Page koan
            let! code = codeOf w.Page
            check (code = koan.Body) $"the koan shows:\n{code}"
        }
        |> unit

    [<StepDefinition("the koan \"(.*)\" is not shown as passed")>]
    member _.NotPassed(name: string) =
        task {
            let! koan = koanNamed w.Page name
            let! state = w.Page.GetAttributeAsync($"nav button[data-koan=\"{koan.Lesson}/{koan.Index}\"]", "data-state")
            check (state <> "passed") "the koan is shown as passed"
        }
        |> unit

    [<StepDefinition("the learner has edited a koan")>]
    member _.Edited() =
        task {
            let! koan = koanAt w.Page 0 0
            do! show w.Page koan
            do! typeCode w.Page ("    // not what was there")
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("the learner resets it")>]
    member _.Reset() = w.Page.ClickAsync "#reset"

    [<StepDefinition("it shows its original code with its blanks")>]
    member _.Original() =
        task {
            let! code = codeOf w.Page
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

    [<StepDefinition("the page shows how many of all the koans are passed")>]
    member _.OverallProgress() =
        task {
            let! text = w.Page.TextContentAsync "#overall"
            check (text.Trim() = $"1 of {Specs.koans.Value.Length} passed") $"the page shows '{text}'"
        }
        |> unit

    [<StepDefinition("a koan in a later lesson is shown")>]
    member _.LaterLesson() =
        task {
            let! koan = koanAt w.Page 2 1
            do! show w.Page koan
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("that lesson's koans are listed")>]
    member _.LessonOpen() =
        task {
            let! visible = w.Page.Locator("nav button[data-koan^=\"2/\"]:visible").CountAsync()
            let! all = w.Page.EvaluateAsync<int>("() => window.koans.course.lessons[2].koans.length")
            check (visible = all) $"{visible} of the lesson's {all} koans are listed"
        }
        |> unit

    [<StepDefinition("the other lessons show only their titles and progress")>]
    member _.OthersFolded() =
        task {
            let! visible = w.Page.Locator("nav button[data-koan]:visible").CountAsync()
            let! all = w.Page.EvaluateAsync<int>("() => window.koans.course.lessons[2].koans.length")
            let! lessons = w.Page.EvaluateAsync<int>("() => window.koans.course.lessons.length")
            let! titles = w.Page.Locator("nav h2[data-lesson]:visible .progress").CountAsync()
            check (visible = all) $"{visible} koans are listed; only the open lesson's {all} should be"
            check (titles = lessons) $"{titles} of {lessons} lessons show their title and progress"
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
            let! marks = w.Page.Locator("#code mark.blank").CountAsync()
            // Blanks in code, not in comments or strings (a comment may mention __).
            let code = Regex.Replace(w.Koan.Value.Body, "//[^\n]*|\"(?:\\\\.|[^\"\\\\\n])*\"", "")
            let blanks = Regex.Matches(code, @"(?<![\w.])__(?!\w)").Count
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

    [<StepDefinition("it names the F# topic it teaches, without a link")>]
    member _.NamesFSharp() =
        task {
            let spec = Specs.koans.Value |> List.find (fun s -> s.Koan = w.Koan.Value.Name)
            check (spec.Teaches.StartsWith "F#: ") $"the first koan teaches {spec.Teaches}"
            let! text = w.Page.TextContentAsync "#teaches"
            let! href = w.Page.GetAttributeAsync("#teaches", "href")
            check (text = spec.Teaches) $"the koan shows '{text}'"
            check (isNull href) $"the koan links to {href}"
        }
        |> unit

    /// The highlighted tokens of the editor, as (class, text).
    member private _.Tokens() =
        task {
            let! json = w.Page.EvaluateAsync<string>("() => JSON.stringify([...document.querySelectorAll('#code .cm-content span')].map(s => [s.className, s.textContent]))")
            return JsonSerializer.Deserialize<string[][]>(json) |> Array.map (fun a -> a.[0], a.[1]) |> Set.ofArray
        }

    [<StepDefinition("its keywords, type names, strings, numbers and comments are coloured")>]
    member this.Coloured() =
        task {
            let! tokens = this.Tokens()
            let comment = tokens |> Set.exists (fun (c, t) -> c = "c" && t.StartsWith "// Product is an ordinary immutable F# record")

            for expected in [ "k", "let"; "k", "with"; "t", "ProductId"; "s", "\"WIDGET\""; "n", "1L" ] do
                check (tokens.Contains expected) $"%A{expected} is not coloured; the editor shows %A{tokens}"

            check comment $"the comment is not coloured; the editor shows %A{tokens}"
        }
        |> unit

    [<StepDefinition("the learner types code into a koan")>]
    member _.Types() =
        task {
            let! koan = koanAt w.Page 0 0
            do! show w.Page koan
            let! code = codeOf w.Page
            do! focusAt w.Page code.Length code.Length
            do! w.Page.Keyboard.TypeAsync "\n    let answer = Some \"forty-two\""
        }
        |> unit

    [<StepDefinition("the code they typed is coloured too")>]
    member this.TypedColoured() =
        task {
            let! tokens = this.Tokens()

            for expected in [ "k", "let"; "t", "Some"; "s", "\"forty-two\"" ] do
                check (tokens.Contains expected) $"%A{expected} is not coloured after typing; the editor shows %A{tokens}"
        }
        |> unit

    [<StepDefinition("each line of its code is numbered, starting from 1")>]
    member _.Numbered() =
        task {
            // CodeMirror's first gutter element only sizes the gutter.
            let! numbers = w.Page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#code .cm-lineNumbers .cm-gutterElement')].filter(e => e.style.visibility !== 'hidden').map(e => e.textContent)")
            let gutter = String.Join("\n", numbers)
            let lines = w.Koan.Value.Body.Split('\n').Length
            let expected = String.Join("\n", [ 1..lines ])
            check (gutter.Trim() = expected) $"the editor numbers its lines:\n{gutter}\nfor {lines} lines"
        }
        |> unit

    [<StepDefinition("^the learner fills in the first koan's blanks and presses Ctrl\\+Enter$")>]
    member _.CtrlEnter() =
        task {
            let! koan = koanAt w.Page 0 0
            do! show w.Page koan
            do! typeCode w.Page (fill koan.Body answers.Value.[koan.Name])
            do! pressInEditor w.Page "Control+Enter"
            let! _ = w.Page.WaitForFunctionAsync("() => document.getElementById('result').dataset.status", null, PageWaitForFunctionOptions(Timeout = Nullable 120000.f))
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("^the learner presses Ctrl\\+(\\]|\\[) in the editor$")>]
    member _.PressBracket(key: string) =
        pressInEditor w.Page (if key = "]" then "Control+BracketRight" else "Control+BracketLeft")

    /// The koan the page shows, by name.
    member private _.ShownName() =
        task {
            let! name = w.Page.TextContentAsync "#name"
            return name.Trim()
        }

    [<StepDefinition("^the koan \"(.*)\" is shown$")>]
    member this.KoanShown(name: string) =
        task {
            let! shown = this.ShownName()
            check (shown = name) $"the page shows the koan \"{shown}\""
        }
        |> unit

    [<StepDefinition("^the editor has the focus, with its first blank selected$")>]
    member _.FocusOnBlank() =
        task {
            let! focused, from, until, _, code = editorState w.Page
            let state = [| (if focused then "true" else "false"); code.Substring(from, until - from); (if from = firstBlankAt code then "true" else "false") |]
            check (state.[0] = "true") "the editor does not have the focus"
            check (state.[1] = "__" && state.[2] = "true") $"the selection is %A{state.[1]}, not the first blank"
        }
        |> unit

    [<StepDefinition("^the learner opens the last koan of lesson (\\d+)$")>]
    member _.OpenLast(lesson: int) =
        task {
            let! count = w.Page.EvaluateAsync<int>($"() => window.koans.course.lessons[{lesson - 1}].koans.length")
            let! koan = koanAt w.Page (lesson - 1) (count - 1)
            do! show w.Page koan
            w.Koan <- Some koan
        }
        |> unit

    [<StepDefinition("^the first koan of lesson (\\d+) is shown$")>]
    member this.FirstOfLesson(lesson: int) =
        task {
            let! koan = koanAt w.Page (lesson - 1) 0
            let! shown = this.ShownName()
            check (shown = koan.Name) $"the page shows \"{shown}\", not \"{koan.Name}\""
        }
        |> unit

    [<StepDefinition("^the learner chooses the Next koan button$")>]
    member _.ChooseNext() = w.Page.ClickAsync "#next"

    [<StepDefinition("^the learner chooses the koan \"(.*)\" from the list$")>]
    member _.ChooseFromList(name: string) =
        task {
            let! koan = koanNamed w.Page name
            let button = $"nav button[data-koan=\"{koan.Lesson}/{koan.Index}\"]"
            // A folded lesson is opened first, as a person would.
            let! visible = w.Page.IsVisibleAsync button
            if not visible then do! w.Page.ClickAsync($"nav h2[data-lesson=\"{koan.Lesson}\"] .lesson-toggle")
            do! w.Page.ClickAsync button
        }
        |> unit

    [<StepDefinition("^the address names the koan \"(.*)\"$")>]
    member _.AddressNames(slug: string) =
        task {
            let! _ = w.Page.WaitForFunctionAsync("s => new URL(location.href).searchParams.get('koan') === s", slug, PageWaitForFunctionOptions(Timeout = Nullable 5000.f))
            ()
        }
        |> unit

    [<StepDefinition("^the learner goes to the address of the koan \"(.*)\"$")>]
    member _.GoTo(slug: string) =
        task {
            let! _ = w.Page.GotoAsync(baseUrl () + "index.html?koan=" + slug)
            let! _ = w.Page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }
        |> unit

    [<StepDefinition("^the learner goes back in the browser$")>]
    member _.Back() =
        task {
            let! _ = w.Page.GoBackAsync()
            ()
        }
        |> unit

    [<StepDefinition("^the address of every koan is worked out from its name$")>]
    member _.Addresses() =
        task {
            let! slugs = w.Page.EvaluateAsync<string[]>("() => window.koans.course.lessons.flatMap(l => l.koans.map(k => window.koans.addressOf(k)))")
            addresses <- List.ofArray slugs
        }
        |> unit

    [<StepDefinition("^no two koans share an address$")>]
    member _.UniqueAddresses() =
        let shared = addresses |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst
        check (addresses.Length = Specs.koans.Value.Length) $"{addresses.Length} addresses for {Specs.koans.Value.Length} koans"
        check shared.IsEmpty $"koans share the addresses %A{shared}"

    [<StepDefinition("^the Next koan button shows the key that opens the next koan$")>]
    member _.NextNamesKey() =
        task {
            let! text = w.Page.TextContentAsync "#next .kbd"
            check (text = "Ctrl+]" || text = "⌘]") $"the Next koan button shows the key %A{text}"
        }
        |> unit

    [<StepDefinition("^its code is set in a font that joins operators such as \|> and ->$")>]
    member _.LigatureFont() =
        task {
            let! state =
                w.Page.EvaluateAsync<string[]>(
                    """async () => {
                        await document.fonts.ready;
                        const loaded = [...document.fonts].some(f => f.family.replace(/"/g, '') === 'JetBrains Mono' && f.status === 'loaded');
                        const style = getComputedStyle(document.querySelector('#code .cm-content'));
                        return [String(loaded), style.fontFamily, style.fontVariantLigatures];
                    }"""
                )

            check (state.[0] = "true") $"JetBrains Mono was not loaded from the site (the editor uses {state.[1]})"
            check (state.[1].StartsWith "\"JetBrains Mono\"") $"the editor's font is {state.[1]}"
            check (state.[2] <> "none") "the editor turns ligatures off"
        }
        |> unit
