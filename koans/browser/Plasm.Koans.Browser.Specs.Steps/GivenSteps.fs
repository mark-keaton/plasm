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
