namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Counts, aggregates and grouping
[<Binding>]
type AggregateSteps(w: World) =

    let published =
        plasm {
            for o in tableOf<Order> do
                where (o.Status = Published)
        }

    let one (cmd: CommandEvent list) = cmd |> List.exactlyOne

    let rejectWith (r: Result<'a, DbError>) =
        w.Set("rejection", (match r with Error e -> Some e | Ok _ -> None))

    // Published totals 10..50, draft totals 5 and 7.
    [<StepDefinition("5 published Orders and 2 draft Orders")>]
    member _.PublishedAndDrafts() =
        task {
            for i in 1..5 do
                let! _ = Seed.order w.Db None "published" (decimal (i * 10)) (Seed.at i)
                ()

            let! _ = Seed.order w.Db None "draft" 5m (Seed.at 6)
            let! _ = Seed.order w.Db None "draft" 7m (Seed.at 7)
            ()
        }
        |> unit

    // Scenario: Counting a filtered query

    [<StepDefinition("I count the published Orders")>]
    member _.CountPublished() =
        task {
            w.MarkCommands()
            let! n = w.Repo.Count published
            w.Set("count", ok n)
        }
        |> unit

    [<StepDefinition("^the count is (\\d+)$")>]
    member _.CountIs(n: int64) = equal n (w.Get<int64> "count") "count"

    [<StepDefinition("the count runs as one database command that transfers a single row")>]
    member _.OneRow() =
        let cmd = one w.CommandsSinceMark
        equal 1 cmd.Rows "rows transferred"
        check (cmd.Sql.StartsWith "SELECT COUNT(*)") cmd.Sql

    [<StepDefinition("the filter is part of the counting SQL")>]
    member _.CountFilter() =
        check ((one w.CommandsSinceMark).Sql.Contains "WHERE \"t0\".\"status\" = @p0") "filter in SQL"

    // Scenario: Counting a page counts the page

    [<StepDefinition("I count a page of 3 published Orders after skipping 4")>]
    member _.CountPage() =
        task {
            let page =
                plasm {
                    for o in published do
                        orderBy o.Id
                        skip 4
                        take 3
                }

            let! n = w.Repo.Count page
            w.Set("count", ok n)
        }
        |> unit

    // Scenario: Testing existence

    [<StepDefinition("^I ask whether any (cancelled|draft) Order exists$")>]
    member _.AnyExists(status: string) =
        task {
            if not (w.Has "existsMark") then
                w.MarkCommands()
                w.Set("existsMark", true)

            let s = if status = "draft" then Draft else Cancelled

            let! r =
                w.Repo.Exists(
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Status = s)
                    }
                )

            w.Set("answer", ok r)
        }
        |> unit

    [<StepDefinition("^the answer is (true|false)$")>]
    member _.Answer(expected: bool) = equal expected (w.Get<bool> "answer") "exists"

    [<StepDefinition("each existence test reads at most one row")>]
    member _.AtMostOne() =
        let cmds = w.CommandsSinceMark
        equal 2 cmds.Length "commands"

        for c in cmds do
            check (c.Rows <= 1) $"rows: {c.Rows}"
            check (c.Sql.Contains "LIMIT") c.Sql

    // Scenario: Aggregating a filtered query

    [<StepDefinition("I select the count, sum, minimum, maximum and average of published Order totals")>]
    member _.Aggregates() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.One(
                    plasm {
                        for o in published do
                            select (Agg.count (), Agg.sum o.Total, Agg.min o.Total, Agg.max o.Total, Agg.avg o.Total)
                    }
                )

            w.Set("aggregates", (ok r).Value)
        }
        |> unit

    [<StepDefinition("each aggregate is computed by the database")>]
    member _.ComputedByDb() =
        let cmd = one w.CommandsSinceMark
        equal 1 cmd.Rows "rows transferred"

        for f in [ "COUNT(*)"; "SUM(\"t0\".\"total\")"; "MIN("; "MAX("; "AVG(" ] do
            check (cmd.Sql.Contains f) $"{f} in {cmd.Sql}"

    [<StepDefinition("the results are typed values that match the stored totals")>]
    member _.TypedAggregates() =
        let (count: int64), (sum: decimal), (min: decimal option), (max: decimal option), (avg: decimal option) =
            w.Get "aggregates"

        equal 5L count "count"
        equal 150m sum "sum"
        equal (Some 10m) min "min"
        equal (Some 50m) max "max"
        equal (Some 30m) avg "avg"

    // Scenario: Aggregates over no rows are explicit

    [<StepDefinition("I aggregate Order totals over a query that matches nothing")>]
    member _.AggregateNothing() =
        task {
            let! r =
                w.Repo.One(
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Total < 0m)
                            select (Agg.count (), Agg.sum o.Total, Agg.min o.Total, Agg.max o.Total, Agg.avg o.Total)
                    }
                )

            w.Set("aggregates", (ok r).Value)
        }
        |> unit

    [<StepDefinition("the count is 0 and the sum is 0")>]
    member _.ZeroCountSum() =
        let (count: int64), (sum: decimal), (_: decimal option), (_: decimal option), (_: decimal option) =
            w.Get "aggregates"

        equal 0L count "count"
        equal 0m sum "sum"

    [<StepDefinition("the minimum, maximum and average are None")>]
    member _.NoneAggregates() =
        let (_: int64), (_: decimal), (min: decimal option), (max: decimal option), (avg: decimal option) =
            w.Get "aggregates"

        equal (None, None, None) (min, max, avg) "min, max, avg"

    // Scenario: Aggregates are checked at compile time

    [<StepDefinition("I attempt to sum a text column or average an integer key")>]
    member _.BadAggregates() =
        w.Set(
            "snippets",
            [ "let q = plasm { for c in tableOf<Customer> do select (Agg.sum c.Name) }"
              "let q = plasm { for o in tableOf<Order> do select (Agg.avg o.Id) }" ]
        )

    [<StepDefinition("the aggregate is rejected at compile time")>]
    member _.AggregateRejected() =
        task {
            let control = "let q = plasm { for o in tableOf<Order> do select (Agg.count (), Agg.sum o.Total, Agg.avg o.Total, Agg.max o.PlacedAt) }"
            let! r = CompileCheck.check control
            check r.Compiles ("expected to compile:\n" + control + "\n" + String.concat "\n" r.Errors)

            for code in w.Get<string list> "snippets" do
                let! r = CompileCheck.check code
                check (r.Errors |> List.exists (fun e -> e.StartsWith "FS0001")) $"expected FS0001 for:\n{code}\ngot: %A{r.Errors}"
        }
        |> unit

    // Rule: Grouping summarises in the database
    // Ada has 3 orders (10, 20, 30), Bob 1 (40), Cy none; one order (5) has no customer.

    [<StepDefinition("Customers with different numbers of Orders")>]
    member _.Customers() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! _ = Seed.customer w.Db "Cy" "cy@example.com"

            for i, total in [ 1, 10m; 2, 20m; 3, 30m ] do
                let! _ = Seed.order w.Db (Some ada) "published" total (Seed.at i)
                ()

            let! _ = Seed.order w.Db (Some bob) "published" 40m (Seed.at 4)
            let! _ = Seed.order w.Db None "draft" 5m (Seed.at 5)
            w.Set("ada", ada)
            w.Set("bob", bob)
        }
        |> unit

    [<StepDefinition("I group Orders by Customer and select each Customer's count and total")>]
    member _.GroupByCustomer() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            where o.CustomerId.IsSome
                            groupBy o.CustomerId
                            orderBy o.CustomerId
                            select (o.CustomerId, Agg.count (), Agg.sum o.Total)
                    }
                )

            w.Set("groups", ok r)
        }
        |> unit

    [<StepDefinition("there is one result per Customer with Orders")>]
    member _.OnePerCustomer() =
        let groups: (CustomerId option * int64 * decimal) list = w.Get "groups"
        equal [ Some(w.Get<CustomerId> "ada"); Some(w.Get<CustomerId> "bob") ] (groups |> List.map (fun (c, _, _) -> c)) "groups"

    [<StepDefinition("each result has that Customer's count and total")>]
    member _.GroupValues() =
        let groups: (CustomerId option * int64 * decimal) list = w.Get "groups"
        equal [ 3L, 60m; 1L, 40m ] (groups |> List.map (fun (_, n, t) -> n, t)) "count and total"

    [<StepDefinition("the grouping runs as a single database command")>]
    member _.GroupingOneCommand() =
        let cmd = one w.CommandsSinceMark
        check (cmd.Sql.Contains "GROUP BY \"t0\".\"customer_id\"") cmd.Sql
        equal 2 cmd.Rows "rows transferred"

    // Scenario: Filtering groups

    [<StepDefinition("I keep only Customers with more than one Order")>]
    member _.Having() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            where o.CustomerId.IsSome
                            groupBy o.CustomerId
                            having (Agg.count () > 1L)
                            select (o.CustomerId, Agg.count ())
                    }
                )

            w.Set("having", ok r)
        }
        |> unit

    [<StepDefinition("only groups satisfying the condition are returned")>]
    member _.HavingResult() =
        equal [ Some(w.Get<CustomerId> "ada"), 3L ] (w.Get<(CustomerId option * int64) list> "having") "groups"

    [<StepDefinition("the condition is applied by the database after grouping")>]
    member _.HavingInSql() =
        let cmd = one w.CommandsSinceMark
        check (cmd.Sql.Contains "GROUP BY \"t0\".\"customer_id\" HAVING COUNT(*) > @p0") cmd.Sql
        equal 1 cmd.Rows "rows transferred"

    // Scenario: Grouping across a join

    [<StepDefinition("I join Orders to Customers and group by the Customer name")>]
    member _.GroupJoin() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                            groupBy c.Name
                            orderByDescending (Agg.count ())
                            select (c.Name, Agg.count ())
                    }
                )

            w.Set("byName", ok r)
        }
        |> unit

    [<StepDefinition("each Customer name is paired with its Order count")>]
    member _.ByName() =
        equal [ "Ada", 3L; "Bob", 1L ] (w.Get<(string * int64) list> "byName") "names and counts"

    [<StepDefinition("the results can be ordered by the aggregate")>]
    member _.OrderedByAggregate() =
        let cmd = one w.CommandsSinceMark
        check (cmd.Sql.Contains "GROUP BY \"j1\".\"name\" ORDER BY COUNT(*) DESC") cmd.Sql

    // Scenario: Grouping mistakes are explicit

    [<StepDefinition("I select a column that is neither grouped nor aggregated")>]
    member _.Ungrouped() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            groupBy o.CustomerId
                            select (o.CustomerId, o.Total)
                    }
                )

            rejectWith r
        }
        |> unit

    [<StepDefinition("the query is rejected before execution with the column identified")>]
    member _.RejectedColumn() =
        match w.Get<DbError option> "rejection" with
        | Some(DbError.Translation(TranslationError.InvalidQuery msg)) ->
            check (msg.Contains "o.Total" && msg.Contains "neither grouped nor aggregated") msg
        | other -> failwithf "expected InvalidQuery, got %A" other

    [<StepDefinition("I filter on an aggregate with where instead of having")>]
    member _.WhereAggregate() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            where (Agg.count () > 1L)
                    }
                )

            rejectWith r
            w.Set("expectedExplanation", [ "cannot be used in where"; "having" ])
        }
        |> unit

    /// Shared with other features: "rejection" holds the error and
    /// "expectedExplanation" the phrases its message must contain.
    [<StepDefinition("the query is rejected before execution with an explanation")>]
    member _.RejectedExplanation() =
        match w.Get<DbError option> "rejection" with
        | Some(DbError.Translation(TranslationError.InvalidQuery msg)) ->
            for phrase in w.Get<string list> "expectedExplanation" do
                check (msg.Contains phrase) $"expected '{phrase}' in: {msg}"
        | other -> failwithf "expected InvalidQuery, got %A" other
