namespace Plasm.Koans.Browser.Specs

open System
open System.Diagnostics
open System.Text.Json
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// A completion as the page's engine gives it.
type Completion = { name: string; kind: string; detail: string }

/// Feature: Walking the path in the browser, rule "The editor knows F#".
/// `window.koans.complete(lesson, koan, code, offset)` asks the engine what can
/// follow the cursor at `offset` in a koan's code.
[<Binding>]
type CompletionSteps(w: BrowserWorld) =
    let mutable items: Completion[] = [||]
    let mutable took = TimeSpan.Zero
    let mutable hoverJson = ""

    let ask (koanName: string) (after: string) =
        task {
            let! koan = koanNamed w.Page koanName
            // The koan's code with `after` typed on a new line at its end.
            let code = koan.Body.TrimEnd() + "\n" + after
            let watch = Stopwatch.StartNew()
            let! json = w.Page.EvaluateAsync<string>("async ([l, k, c, o]) => JSON.stringify(await window.koans.complete(l, k, c, o))", box [| box koan.Lesson; box koan.Index; box code; box code.Length |])
            took <- watch.Elapsed
            printfn $"completions after '{after}' in '{koanName}': {took.TotalMilliseconds:F0} ms"
            items <- JsonSerializer.Deserialize<Completion[]> json
        }

    [<StepDefinition("the learner asks what can follow \"(.*)\" in the koan \"(.*)\"")>]
    member _.Ask(after: string, koanName: string) = ask koanName after |> unit

    [<StepDefinition("the learner has asked for completions once")>]
    member _.Warm() = ask "Every koan is given a repo" "repo." |> unit

    [<StepDefinition("the completions include (.*)")>]
    member _.Include(list: string) =
        let expected = list.Replace(" and ", ", ").Split(", ")
        let names = items |> Array.map (fun i -> i.name) |> Set.ofArray
        let missing = expected |> Array.filter (fun e -> not (names.Contains e))
        check (missing.Length = 0) $"missing %A{missing}; the completions are %A{names}"

    [<StepDefinition("each completion says what it is")>]
    member _.Described() =
        let bare = items |> Array.filter (fun i -> String.IsNullOrWhiteSpace i.kind)
        check (items.Length > 0 && bare.Length = 0) $"completions without a kind: %A{bare}"

    [<StepDefinition("the completions arrive within (\\d+) milliseconds")>]
    member _.Within(ms: int) =
        check (took.TotalMilliseconds <= float ms) $"the completions took {took.TotalMilliseconds:F0} ms"

    [<StepDefinition("the learner types \"(.*)\" on a new line at the end of the code")>]
    member _.TypeAtEnd(text: string) =
        task {
            let! code = codeOf w.Page
            do! focusAt w.Page code.Length code.Length
            do! w.Page.Keyboard.TypeAsync("\n" + text)
        }
        |> unit

    [<StepDefinition("a list of completions opens, offering (\\w+)")>]
    member _.Offered(name: string) =
        task {
            let! _ =
                w.Page.WaitForFunctionAsync(
                    "n => [...document.querySelectorAll('.cm-tooltip-autocomplete .cm-completionLabel')].some(l => l.textContent === n)",
                    name,
                    PageWaitForFunctionOptions(Timeout = Nullable 30000.f)
                )

            // CodeMirror ignores accepting within 75 ms of the list opening
            // (so a keystroke already on its way cannot pick something); a
            // person reads the list first.
            do! w.Page.WaitForTimeoutAsync 200.f
        }
        |> unit

    [<StepDefinition("choosing (\\w+) puts \"(.*)\" in the code")>]
    member _.Choose(name: string, expected: string) =
        task {
            do! w.Page.ClickAsync($".cm-tooltip-autocomplete li:has(.cm-completionLabel:text-is(\"{name}\"))")
            let! code = codeOf w.Page
            check (code.TrimEnd().EndsWith expected) $"the code ends:\n{code}"
        }
        |> unit

    /// The offset of `name` within the first `within` in the editor's code.
    member private _.OffsetOf(name: string, within: string) =
        task {
            let! code = codeOf w.Page
            let at = code.IndexOf within
            check (at >= 0) $"the code has no {within}:\n{code}"
            return at + within.IndexOf name
        }

    [<StepDefinition("the learner points at \"(\\w+)\" in \"(.*)\"")>]
    member this.PointAt(name: string, within: string) =
        task {
            let! at = this.OffsetOf(name, within)
            // As a person would, scroll the code into view first.
            do! w.Page.EvaluateAsync("p => window.koans.editor.reveal(p)", at) :> Threading.Tasks.Task
            do! w.Page.WaitForTimeoutAsync 100.f
            let! xy = w.Page.EvaluateAsync<float[]>("p => { const c = window.koans.editor.coordsAt(p); return [c.x, c.y]; }", at + 1)
            do! w.Page.Mouse.MoveAsync(float32 xy.[0], float32 xy.[1])
        }
        |> unit

    [<StepDefinition("the learner puts the cursor on \"(\\w+)\" in \"(.*)\" and types \"(.*)\"")>]
    member this.CursorAndType(name: string, within: string, keys: string) =
        task {
            let! at = this.OffsetOf(name, within)
            do! focusAt w.Page at at
            do! w.Page.Keyboard.TypeAsync keys
        }
        |> unit

    [<StepDefinition("a tip shows \"(.*)\"")>]
    member _.TipShows(text: string) =
        task {
            let! _ =
                w.Page.WaitForFunctionAsync(
                    "t => [...document.querySelectorAll('.type-tip .signature')].some(s => s.textContent.includes(t))",
                    text,
                    PageWaitForFunctionOptions(Timeout = Nullable 30000.f)
                )

            ()
        }
        |> unit

    [<StepDefinition("the tip says \"(.*)\"")>]
    member _.TipSays(text: string) =
        task {
            let! doc = w.Page.TextContentAsync ".type-tip .doc"
            check (doc.Contains text) $"the tip says: {doc}"
        }
        |> unit

    [<StepDefinition("the tip says something about it")>]
    member _.TipSaysSomething() =
        task {
            let! n = w.Page.Locator(".type-tip .doc").CountAsync()
            check (n > 0) "the tip has no documentation"
            let! doc = w.Page.TextContentAsync ".type-tip .doc"
            check (doc.Trim().Length > 10) $"the tip says only: {doc}"
        }
        |> unit

    [<StepDefinition("the learner asks what \"(\\w+)\" in \"(.*)\" is, in the koan \"(.*)\"")>]
    member _.AskHover(name: string, within: string, koanName: string) =
        task {
            let! koan = koanNamed w.Page koanName
            let at = koan.Body.IndexOf within + within.IndexOf name + name.Length
            let! json = w.Page.EvaluateAsync<string>("async ([l, k, c, o]) => JSON.stringify(await window.koans.hover(l, k, c, o))", box [| box koan.Lesson; box koan.Index; box koan.Body; box at |])
            printfn $"hover: {json}"
            hoverJson <- json
        }
        |> unit

    [<StepDefinition("the answer's signature mentions \"(.*)\"")>]
    member _.SignatureMentions(text: string) =
        check (hoverJson.Contains text) $"the answer is {hoverJson}"

    [<StepDefinition("^(?:the learner )?presses (Tab|Escape|Ctrl\\+Space)$")>]
    member _.Press(key: string) =
        w.Page.Keyboard.PressAsync(
            match key with
            | "Ctrl+Space" -> "Control+Space"
            | k -> k
        )

    [<StepDefinition("^the code ends with \"(.*)\"$")>]
    member _.EndsWith(text: string) =
        task {
            let! code = codeOf w.Page
            check (code.TrimEnd().EndsWith text) $"the code ends:\n%A{code.Substring(max 0 (code.Length - 60))}"
        }
        |> unit

    [<StepDefinition("^the code ends with a line of four spaces$")>]
    member _.EndsIndented() =
        task {
            let! code = codeOf w.Page
            check (code.EndsWith "\n    ") $"the code ends:\n%A{code.Substring(max 0 (code.Length - 20))}"
        }
        |> unit

    [<StepDefinition("^no completion is a member every object has, such as Equals$")>]
    member _.NoObjectMembers() =
        let found = items |> Array.filter (fun i -> List.contains i.name [ "Equals"; "GetHashCode"; "GetType"; "ToString" ])
        check (found.Length = 0) $"completions include %A{found |> Array.map (fun i -> i.name)}"

    [<StepDefinition("^no completion's description spells out System.Threading$")>]
    member _.ShortDetails() =
        let long = items |> Array.filter (fun i -> i.detail.Contains "System.Threading")
        check (long.Length = 0) $"descriptions such as %A{long |> Array.truncate 2 |> Array.map (fun i -> i.detail)}"

    /// Waits until the compiler has checked the editor's current code.
    member private _.Checked() =
        task {
            let! _ =
                w.Page.WaitForFunctionAsync(
                    "() => window.koans.editor.checkedCode() === window.koans.editor.get()",
                    null,
                    PageWaitForFunctionOptions(Timeout = Nullable 60000.f)
                )

            let! messages = w.Page.EvaluateAsync<string[]>("() => window.koans.editor.diagnostics().map(d => d.message)")
            return messages
        }

    [<StepDefinition("^the mistake is underlined$")>]
    member this.Underlined() =
        task {
            let! messages = this.Checked()
            check (messages.Length > 0) "nothing is underlined"
            let! marks = w.Page.Locator("#code .cm-lintRange-error").CountAsync()
            check (marks > 0) $"the compiler says %A{messages}, but nothing is underlined"
        }
        |> unit

    [<StepDefinition("^the underline says what the compiler says: \"(.*)\"$")>]
    member this.UnderlineSays(text: string) =
        task {
            let! messages = this.Checked()
            check (messages |> Array.exists (fun m -> m.Contains text)) $"the underlines say %A{messages}"
        }
        |> unit

    [<StepDefinition("^nothing is underlined$")>]
    member this.NothingUnderlined() =
        task {
            let! messages = this.Checked()
            check (messages.Length = 0) $"underlined: %A{messages}"
        }
        |> unit

    [<StepDefinition("^the learner replaces the code with its original code$")>]
    member _.Original() =
        task {
            let! name = w.Page.TextContentAsync "#name"
            let! koan = koanNamed w.Page (name.Trim())
            do! typeCode w.Page koan.Body
        }
        |> unit

