namespace Plasm.Koans.Browser.Specs

open System
open System.Text.RegularExpressions
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// Feature: Walking the path in the browser, rule "The editor has a simple Vim
/// mode". The toggle is `#vim` (aria-pressed); the mode is `#vim-mode`'s
/// data-mode ("normal" or "insert"). In the examples ‸ marks the cursor, ⏎ a new
/// line and · a space.
[<Binding>]
type VimSteps(w: BrowserWorld) =

    let cursor = "‸"
    let plain (s: string) = s.Replace("⏎", "\n").Replace("·", " ")

    [<StepDefinition("^the learner has turned Vim mode on$")>]
    member _.TurnOn() =
        task {
            let! pressed = w.Page.GetAttributeAsync("#vim", "aria-pressed")
            if pressed <> "true" then do! w.Page.ClickAsync "#vim"
        }
        |> unit

    [<StepDefinition("^Vim mode is off$")>]
    member _.Off() =
        task {
            let! pressed = w.Page.GetAttributeAsync("#vim", "aria-pressed")
            check (pressed = "false") $"the Vim toggle is aria-pressed={pressed}"
            let! shown = w.Page.IsVisibleAsync "#vim-mode"
            check (not shown) "a Vim mode is shown while Vim mode is off"
        }
        |> unit

    [<StepDefinition("^typing in the editor inserts text$")>]
    member _.TypingInserts() =
        task {
            do! w.Page.FocusAsync "#code"
            do! w.Page.EvaluateAsync("() => { const c = document.getElementById('code'); c.setSelectionRange(0, 0); }") :> Threading.Tasks.Task
            do! w.Page.Keyboard.TypeAsync "jj"
            let! code = w.Page.InputValueAsync "#code"
            check (code.StartsWith "jj") $"typing jj gave:\n{code}"
        }
        |> unit

    [<StepDefinition("^the editor is in (Normal|Insert) mode$")>]
    member _.Mode(mode: string) =
        task {
            let! shown = w.Page.GetAttributeAsync("#vim-mode", "data-mode")
            check (shown = mode.ToLowerInvariant()) $"the editor is in {shown} mode"
        }
        |> unit

    [<StepDefinition("^the cursor is on the first blank$")>]
    member _.OnBlank() =
        task {
            let! at = w.Page.EvaluateAsync<string[]>("() => { const c = document.getElementById('code'); return [String(c.selectionStart), c.value, String(document.activeElement === c)]; }")
            check (at.[2] = "true") "the editor does not have the focus"
            let blank = firstBlankAt at.[1]
            check (int at.[0] = blank) $"the cursor is at {at.[0]}, the first blank at {blank}"
        }
        |> unit

    [<Given("^the editor holds \"(.*)\"$")>]
    member _.Holds(text: string) =
        let text = plain text
        let at = text.IndexOf cursor
        let value = text.Replace(cursor, "")

        task {
            do! w.Page.FocusAsync "#code"
            do! w.Page.EvaluateAsync("([v, p]) => { const c = document.getElementById('code'); c.value = v; c.setSelectionRange(p, Math.min(p + 1, v.length)); }", box [| box value; box at |]) :> Threading.Tasks.Task
        }
        |> unit

    [<StepDefinition("^(?:the learner )?types \"(.*)\" in the editor$")>]
    member _.Types(keys: string) =
        task {
            for m in Regex.Matches(keys, "<[^>]+>|.") do
                match m.Value with
                | "<Esc>" -> do! w.Page.Keyboard.PressAsync "Escape"
                | "<C-[>" -> do! w.Page.Keyboard.PressAsync "Control+BracketLeft"
                | "<C-r>" -> do! w.Page.Keyboard.PressAsync "Control+r"
                | key -> do! w.Page.Keyboard.TypeAsync key
        }
        |> unit

    [<Then("^the editor holds \"(.*)\"$")>]
    member _.Held(text: string) =
        task {
            let! state = w.Page.EvaluateAsync<string[]>("() => { const c = document.getElementById('code'); return [c.value, String(c.selectionStart)]; }")
            let at = int state.[1]
            let actual = state.[0].Insert(at, cursor)
            let expected = plain text
            let show (s: string) = s.Replace("\n", "⏎").Replace(" ", "·")
            check (actual = expected) $"the editor holds {show actual}, not {show expected}"
        }
        |> unit

    [<StepDefinition("^(?:the learner )?presses Ctrl\\+Enter$")>]
    member _.Run() =
        task {
            do! w.Page.Keyboard.PressAsync "Control+Enter"
            let! _ = w.Page.WaitForFunctionAsync("() => document.getElementById('result').dataset.status", null, PageWaitForFunctionOptions(Timeout = Nullable 120000.f))
            ()
        }
        |> unit
