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
