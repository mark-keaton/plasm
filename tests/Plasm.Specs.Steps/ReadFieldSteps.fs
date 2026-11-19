namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: A program builds a query at run time from declared fields,
/// "A program reads the fields its caller chose".
[<Binding>]
type ReadFieldSteps(w: World) =

    let statusF = lazy ReadField.value "status" <@ fun (o: Order) -> o.Status @>
    let totalF = lazy ReadField.value "total" <@ fun (o: Order) -> o.Total @>
    let linesF = lazy ReadField.via "lines" <@ fun (o: Order) -> o.Lines @> List.length
    let titleF = lazy ReadField.value "title" <@ fun (a: Article) -> a.Title @>
    let tagsF = lazy ReadField.via "tags" <@ fun (a: Article) -> a.Tags @> (List.map (fun t -> t.Name) >> List.sort)

    let read (selection: Projection<Selected<'T>>) =
        task {
            w.MarkCommands()
            let! rows = w.Repo.All selection
            w.Set("selected", ok rows |> List.map box)
        }

    member private _.Rows<'T>() = w.Get<obj list> "selected" |> List.map unbox<Selected<'T>>

    member private _.Sent = w.CommandsSinceMark |> List.map (fun c -> c.Sql)

    [<StepDefinition("the Order's status and total, and the number of its lines, declared as fields to read")>]
    member _.OrderFields() = ()

    [<StepDefinition("the Article's title and the names of its tags declared as fields to read")>]
    member _.ArticleFields() = ()

    [<StepDefinition("Orders of 10, 20 and 30, each with two lines")>]
    member _.Orders() =
        task {
            let! p = Seed.product w.Db "P"

            for i, t in List.indexed [ 10m; 20m; 30m ] do
                let! o = Seed.order w.Db None (if t = 30m then "cancelled" else "published") t (Seed.at i)
                let! _ = Seed.line w.Db o p 1 1m
                let! _ = Seed.line w.Db o p 2 1m
                ()
        }
        |> unit

    [<StepDefinition("Articles \"Plasm\" tagged \"fsharp\" and \"sql\", and \"Ecto\" tagged \"elixir\"")>]
    member _.Articles() =
        task {
            let! _ =
                w.Db.Exec
                    "insert into articles (title) values ('Plasm'), ('Ecto');
                     insert into tags (name) values ('fsharp'), ('sql'), ('elixir');
                     insert into article_tags select a.id, t.id from articles a, tags t
                       where (a.title = 'Plasm' and t.name in ('fsharp', 'sql')) or (a.title = 'Ecto' and t.name = 'elixir')"
            ()
        }
        |> unit

    [<StepDefinition("I read the Orders' status, chosen")>]
    member _.ReadStatus() = read (Query.from tableOf<Order> |> BuildQuery.select [ statusF.Value ]) |> unit

    [<StepDefinition("I read the Orders' status, chosen, and then the total of the first row")>]
    member this.ReadUnchosen() =
        task {
            do! read (Query.from tableOf<Order> |> BuildQuery.select [ statusF.Value ])

            try
                (this.Rows<Order>().Head.Get totalF.Value) |> ignore
                w.Set("refused", "")
            with :? InvalidOperationException as e ->
                w.Set("refused", e.Message)
        }
        |> unit

    [<StepDefinition("I read the Orders' total and number of lines, chosen")>]
    member _.ReadTotalLines() = read (Query.from tableOf<Order> |> BuildQuery.select [ totalF.Value; linesF.Value ]) |> unit

    [<StepDefinition("I read the Articles' title and tag names, chosen")>]
    member _.ReadArticles() = read (Query.from tableOf<Article> |> BuildQuery.select [ titleF.Value; tagsF.Value ]) |> unit

    [<StepDefinition("I read the status of the Orders over 15 by total descending, taking 1, chosen")>]
    member _.ReadBuilt() =
        let query =
            Query.from tableOf<Order>
            |> BuildQuery.where <@ fun o -> o.Total > 15m @>
            |> BuildQuery.orderByDescending <@ fun o -> o.Total @>
            |> BuildQuery.take 1

        read (query |> BuildQuery.select [ statusF.Value ]) |> unit

    [<StepDefinition("I select each Order's total and its lines, written")>]
    member _.ReadWritten() =
        task {
            w.MarkCommands()
            let! rows = w.Repo.All(plasm { for o in tableOf<Order> do select (o.Total, o.Lines) })
            w.Set("written", ok rows)
        }
        |> unit

    [<StepDefinition("the read sends one command, reading only the status column")>]
    member this.OnlyStatus() =
        match this.Sent with
        | [ sql ] -> check (sql.Contains "\"status\"" && not (sql.Contains "\"total\"") && not (sql.Contains "\"placed_at\"")) sql
        | sent -> failwithf "commands sent: %A" sent

    [<StepDefinition("each row holds its Order's status")>]
    member this.Statuses() =
        equal (List.sort [ Cancelled; Published; Published ]) (this.Rows<Order>() |> List.map (fun r -> r.Get<OrderStatus> statusF.Value) |> List.sort) "statuses"

    [<StepDefinition("reading is refused with {string}")>]
    member _.Refused(message: string) = equal message (w.Get<string> "refused") "refusal"

    [<StepDefinition("the read sends 2 commands: the Orders' totals, and every Order's lines")>]
    member this.TwoForLines() =
        match this.Sent with
        | [ orders; lines ] ->
            check (orders.Contains "\"total\"" && not (orders.Contains "\"status\"")) orders
            check (lines.Contains "\"order_lines\"" && lines.Contains "ANY(") lines
        | sent -> failwithf "commands sent: %A" sent

    [<StepDefinition("each row holds its Order's total and 2 lines")>]
    member this.TotalsAndLines() =
        equal [ 10m, 2; 20m, 2; 30m, 2 ] (this.Rows<Order>() |> List.map (fun r -> r.Get<decimal> totalF.Value, r.Get<int> linesF.Value) |> List.sort) "rows"

    [<StepDefinition("the read sends 2 commands: the Articles' titles, and every Article's tags")>]
    member this.TwoForTags() =
        match this.Sent with
        | [ articles; tags ] ->
            check (articles.Contains "\"title\"") articles
            check (tags.Contains "\"tags\"" && tags.Contains "ANY(") tags
        | sent -> failwithf "commands sent: %A" sent

    [<StepDefinition("the rows are {string}")>]
    member this.ArticleRows(expected: string) =
        let rows =
            this.Rows<Article>()
            |> List.map (fun r -> $"""{r.Get<string> titleF.Value}: {String.Join(", ", r.Get<string list> tagsF.Value)}""")
            |> List.sort

        equal expected (String.Join("; ", rows)) "rows"

    [<StepDefinition("the read sends one command, with the query's where, order and page")>]
    member this.OneBuilt() =
        match this.Sent with
        | [ sql ] -> check (sql.Contains "WHERE" && sql.Contains "ORDER BY" && sql.Contains "LIMIT" && not (sql.Contains "\"total\" AS")) sql
        | sent -> failwithf "commands sent: %A" sent

    [<StepDefinition("the one row holds the status of the Order of 30")>]
    member this.OneRow() = equal [ Cancelled ] (this.Rows<Order>() |> List.map (fun r -> r.Get<OrderStatus> statusF.Value)) "rows"

    [<StepDefinition("each written row holds its Order's total and its 2 lines")>]
    member _.WrittenRows() =
        equal [ 10m, 2; 20m, 2; 30m, 2 ] (w.Get<(decimal * OrderLine list) list> "written" |> List.map (fun (t, l) -> t, l.Length) |> List.sort) "rows"

    [<StepDefinition("the Customer's name declared as a field to read through its hash code")>]
    member _.HashField() = w.Set("readHash", ReadField.value "nameHash" <@ fun (c: Customer) -> c.Name.GetHashCode() @>)

    [<StepDefinition("I check the declared Customer fields to read")>]
    member _.CheckRead() =
        try
            w.Repo.CheckFields [ w.Get<ReadField<Customer>> "readHash" ]
            w.Set("checked", (Ok() : Result<unit, PlasmStartupException>))
        with :? PlasmStartupException as e ->
            w.Set("checked", (Error e : Result<unit, PlasmStartupException>))

    [<StepDefinition("the check fails start-up, naming {string}")>]
    member _.CheckFailsNaming(name: string) =
        match w.Get<Result<unit, PlasmStartupException>> "checked" with
        | Ok() -> failwith "the check passed"
        | Error e ->
            let named = e.Problems |> List.map (function StartupProblem.Query q -> q.Query | other -> string other)
            equal [ name ] named "fields named"
            check (e.Message.StartsWith "Plasm did not start: 1 declared field Plasm cannot translate") $"message: {e.Message}"
