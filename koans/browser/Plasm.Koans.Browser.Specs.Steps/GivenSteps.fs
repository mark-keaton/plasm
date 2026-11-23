namespace Plasm.Koans.Browser.Specs

open System.IO
open System.Text.RegularExpressions
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// Feature: Walking the path in the browser, rule "Each koan opens with what
/// it is given". The Given section is `#given`: a `.table[data-table]` per
/// table (cells of NULL are `td.null`) and a `.definition[data-name]` per
/// definition, whose marked lines are `.marked`.
[<Binding>]
type GivenSteps(w: BrowserWorld) =

    /// Shows a koan once the starting data has been read.
    let open' (koan: KoanInfo) =
        task {
            let! _ = w.Page.WaitForFunctionAsync("() => document.getElementById('given').dataset.state === 'ready'")
            do! show w.Page koan
            w.Koan <- Some koan
        }

    let table (name: string) = $"#given .table[data-table=\"{name}\"]"

    let rows (name: string) =
        task {
            let! cells = w.Page.EvalOnSelectorAllAsync<string[][]>($"{table name} tbody tr", "trs => trs.map(tr => [...tr.cells].map(td => td.textContent.trim()))")
            return cells
        }

    let columns (name: string) =
        w.Page.EvalOnSelectorAllAsync<string[]>($"{table name} thead th", "ths => ths.map(th => th.dataset.column)")

    let cell (name: string) (key: string) (column: string) =
        task {
            let! cols = columns name
            let! rs = rows name
            let c = Array.findIndex ((=) column) cols
            let row = rs |> Array.find (fun r -> r.[0] = key)
            return row.[c]
        }

    [<StepDefinition("the learner opens the koan \"(.*)\"")>]
    member _.Open(name: string) =
        task {
            let! koan = koanNamed w.Page name
            do! open' koan
        }
        |> unit

    [<StepDefinition("the learner opens the first koan")>]
    member _.OpenFirst() =
        task {
            let! koan = koanAt w.Page 0 0
            do! open' koan
        }
        |> unit

    [<StepDefinition("under Given the (\\w+) table shows its (\\d+) starting rows")>]
    member _.TableRows(name: string, count: int) =
        task {
            let! visible = w.Page.IsVisibleAsync(table name)
            check visible $"the {name} table is not shown under Given"
            let! rs = rows name
            check (rs.Length = count) $"the {name} table shows {rs.Length} rows, not {count}"
        }
        |> unit

    [<StepDefinition("Ada's row shows the id 1")>]
    member _.AdaId() =
        task {
            let! rs = rows "customers"
            let! cols = columns "customers"
            let id, name = Array.findIndex ((=) "id") cols, Array.findIndex ((=) "name") cols
            check (rs |> Array.exists (fun r -> r.[name] = "Ada" && r.[id] = "1")) $"no row for Ada with id 1: %A{rs}"
        }
        |> unit

    [<StepDefinition("under Given the orders table shows NULL as the customer of order 4")>]
    member _.NullCustomer() =
        task {
            let! value = cell "orders" "4" "customer_id"
            check (value = "NULL") $"order 4's customer shows '{value}'"
            let orders = table "orders"
            let! nulls = w.Page.Locator($"{orders} td.null").CountAsync()
            check (nulls > 0) "NULL cells are not marked as NULL"
        }
        |> unit

    [<StepDefinition("under Given the definition of (\\w+) includes \"(.*)\"")>]
    member _.DefinitionIncludes(name: string, text: string) =
        task {
            let! code = w.Page.TextContentAsync($"#given .definition[data-name=\"{name}\"] pre")
            check (code.Contains text) $"the definition of {name} does not include {text}:\n{code}"
        }
        |> unit

    [<StepDefinition("the definition says it comes from (.*)")>]
    member _.DefinitionSource(source: string) =
        task {
            let! text = w.Page.TextContentAsync "#given .definition .source"
            check (text.Contains source) $"the definition's source is '{text}'"
        }
        |> unit

    [<StepDefinition("under Given the definition of (\\w+) marks the fields (.*)")>]
    member _.Marked(name: string, list: string) =
        let expected = list.Replace(" and ", ", ").Split(", ") |> Set.ofArray

        task {
            let! marked = w.Page.EvalOnSelectorAllAsync<string[]>($"#given .definition[data-name=\"{name}\"] .marked", "ms => ms.map(m => m.textContent.trim())")
            let fields = marked |> Array.map (fun m -> m.Split(':').[0].Trim().TrimStart('{').Trim())
            check (Set.ofArray fields = expected) $"marked lines are %A{marked}"
        }
        |> unit

    [<StepDefinition("its code starts with its first comment, not with fun repo -> or task \\{")>]
    member _.OwnCode() =
        task {
            let! code = codeOf w.Page
            check (code.StartsWith "//") $"the koan's code starts:\n{code}"
            check (not (code.Contains "fun repo ->") && not (code.Contains "task {")) $"the koan's code shows its frame:\n{code}"
        }
        |> unit

    [<StepDefinition("it uses the repo it is given as repo")>]
    member _.UsesRepo() =
        task {
            let! code = codeOf w.Page
            check (code.Contains "repo.All(") $"the koan's code does not use repo:\n{code}"
        }
        |> unit

    [<StepDefinition("under Given the definition of (\\w+) is labelled \"(.*)\"")>]
    member _.Kind(name: string, kind: string) =
        task {
            let! text = w.Page.TextContentAsync($"#given .definition[data-name=\"{name}\"] .kind")
            check (text.Trim() = kind) $"the definition of {name} is labelled '{text}'"
        }
        |> unit

    [<StepDefinition("the definition of (\\w+) says it comes from (.*)")>]
    member _.NamedSource(name: string, source: string) =
        task {
            let! text = w.Page.TextContentAsync($"#given .definition[data-name=\"{name}\"] .source")
            check (text.Contains source) $"the source of {name} is '{text}'"
        }
        |> unit

    [<StepDefinition("under Given the definition of (\\w+) marks the members (.*)")>]
    member _.MarkedMembers(name: string, list: string) =
        let expected = list.Replace(" and ", ", ").Split(", ") |> Set.ofArray

        task {
            let! marked = w.Page.EvaluateAsync<string[]>($"() => [...document.querySelectorAll('#given .definition[data-name=\"{name}\"] .marked')].map(m => m.textContent.trim())")
            let members = marked |> Array.map (fun m -> Regex.Match(m, @"member _\.(\w+)").Groups.[1].Value)
            check (Set.ofArray members = expected) $"marked lines are %A{marked}"
        }
        |> unit

    [<StepDefinition("asks for all tables")>]
    member _.AllTables() = w.Page.ClickAsync "#all-tables"

    [<StepDefinition("under Given every table of the koans database is shown")>]
    member _.EveryTable() =
        task {
            let data = File.ReadAllText(Path.Combine(root, "koans", "Plasm.Koans.Core", "KoansData.fs"))
            let expected = Regex.Matches(data, @"create table (\w+)") |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq
            let! shown = w.Page.EvalOnSelectorAllAsync<string[]>("#given .table", "ts => ts.filter(t => t.offsetParent !== null).map(t => t.dataset.table)")
            check (Set.ofArray shown = expected) $"tables shown %A{shown}, expected %A{expected}"
        }
        |> unit

    [<StepDefinition("no Given is shown")>]
    member _.NoGiven() =
        task {
            let! visible = w.Page.IsVisibleAsync "#given"
            check (not visible) "a Given is shown for a koan that needs nothing"
        }
        |> unit

    /// The definition's panel shown (its chip pressed), and the offset of
    /// `name` within `within` in its code.
    member private _.Definition(definition: string, name: string, within: string) =
        task {
            let panel = $"#given .definition[data-name=\"{definition}\"]"
            let! hidden = w.Page.EvalOnSelectorAsync<bool>(panel, "p => p.hidden")
            if hidden then
                do! w.Page.ClickAsync($"#given-chips .chip >> text=\"{definition}\"")
            let! code = w.Page.TextContentAsync($"{panel} pre")
            let at = code.IndexOf within
            check (at >= 0) $"the definition of {definition} does not include {within}:\n{code}"
            return panel, at + within.IndexOf name
        }

    [<StepDefinition("the learner points at \"(\\w+)\" in \"(.*)\" in the Given definition of (\\w+)")>]
    member this.PointAtGiven(name: string, within: string, definition: string) =
        task {
            let! panel, at = this.Definition(definition, name, within)
            // The middle of the name's first letter, found through the text the page shows.
            let! xy =
                w.Page.EvalOnSelectorAsync<float[]>(
                    $"{panel} pre",
                    """(pre, at) => {
                      const walk = document.createTreeWalker(pre, NodeFilter.SHOW_TEXT);
                      let n, left = at;
                      while ((n = walk.nextNode()) && left >= n.length) left -= n.length;
                      const r = document.createRange(); r.setStart(n, left); r.setEnd(n, left + 1);
                      n.parentElement.scrollIntoView({ block: 'center' });
                      const b = r.getBoundingClientRect();
                      return [b.x + b.width / 2, b.y + b.height / 2];
                    }""",
                    at
                )
            do! w.Page.Mouse.MoveAsync(0.f, 0.f)
            do! w.Page.Mouse.MoveAsync(float32 xy.[0], float32 xy.[1])
        }
        |> unit

    [<StepDefinition("the learner moves with the keyboard to \"(\\w+)\" in \"(.*)\" in the Given definition of (\\w+)")>]
    member this.KeyboardToGiven(name: string, within: string, definition: string) =
        task {
            let! panel, at = this.Definition(definition, name, within)
            // Tab reaches the definition's code; the arrow keys move from name to name.
            do! w.Page.FocusAsync($"{panel} pre")
            let current () =
                w.Page.EvalOnSelectorAsync<int>($"{panel} pre", "pre => { const c = pre.querySelector('.name.current'); if (!c) return -1; const r = document.createRange(); r.setStart(pre, 0); r.setEndBefore(c); return r.toString().length; }")
            let mutable presses = 0
            let! now = current ()
            let mutable here = now
            while here <> at && presses < 200 do
                do! w.Page.Keyboard.PressAsync "ArrowRight"
                presses <- presses + 1
                let! now = current ()
                here <- now
            check (here = at) $"the arrow keys never reached {name} in {within} (last at {here})"
        }
        |> unit

    [<StepDefinition("no tip shows")>]
    member _.NoTip() =
        task {
            // Longer than the pause before a tip, and the compiler's answer.
            do! w.Page.WaitForTimeoutAsync 3000.f
            let! tips = w.Page.EvalOnSelectorAllAsync<int>(".type-tip", "ts => ts.filter(t => t.offsetParent !== null).length")
            check (tips = 0) "a tip shows"
        }
        |> unit

    [<StepDefinition("the learner asks what each name in the definitions under Given is, in every koan")>]
    member _.AskEveryGivenName() =
        task {
            let! json = w.Page.EvaluateAsync<string>("async () => JSON.stringify(await window.koans.givenNames())")
            w.GivenNames <- json
        }
        |> unit

    [<StepDefinition("the compiler answers for every one")>]
    member _.EveryGivenNameAnswered() =
        let names = System.Text.Json.JsonDocument.Parse(w.GivenNames).RootElement
        let all = [ for n in names.EnumerateArray() -> n ]
        let unanswered =
            all
            |> List.filter (fun n -> not (n.GetProperty("answered").GetBoolean()))
            |> List.map (fun n -> $"""{n.GetProperty("definition").GetString()}: {n.GetProperty("word").GetString()} in {n.GetProperty("line").GetString().Trim()}""")
        printfn $"Given names: {all.Length}, unanswered: {unanswered.Length}"
        check (all.Length > 100) $"only {all.Length} names were asked about"
        check unanswered.IsEmpty (sprintf "the compiler says nothing of %d of %d names:\n%s" unanswered.Length all.Length (String.concat "\n" unanswered))
