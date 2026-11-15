namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module Filters =
    /// An outstanding balance: no successful payment yet.
    let withOutstandingBalance (q: Query<Order>) =
        plasm {
            for o in q do
                where (not (exists (plasm { for p in stored<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })))
        }

    /// An ordinary F# function the translator cannot see into.
    let isRound (d: decimal) = d % 10m = 0m

/// Rule: Queries compose without silently changing execution location
[<Binding>]
type CompositionSteps(w: World) =

    // Scenario: Reusable filters compose

    [<StepDefinition("a function that restricts Orders to a Customer")>]
    member _.CustomerFilter() =
        task {
            let! c1 = Seed.customer w.Db "Ada" "ada@example.com"
            let! c2 = Seed.customer w.Db "Bob" "bob@example.com"
            let! paid = Seed.order w.Db (Some c1) "published" 10m (Seed.at 1)
            let! unpaid = Seed.order w.Db (Some c1) "published" 20m (Seed.at 2)
            let! other = Seed.order w.Db (Some c2) "published" 30m (Seed.at 3)
            let! _ = Seed.payment w.Db paid 10m "succeeded" (Seed.at 4)
            let! _ = Seed.payment w.Db unpaid 5m "failed" (Seed.at 5)
            w.Set("c1", c1)
            w.Set("unpaid", unpaid)
            ignore other
        }
        |> unit

    [<StepDefinition("a function that restricts Orders to an outstanding balance")>]
    member _.OutstandingFilter() = ignore Filters.withOutstandingBalance

    [<StepDefinition("I apply both functions to an Order query")>]
    member _.ApplyBoth() =
        task {
            let q =
                plasm { for o in stored<Order> do orderBy o.Id }
                |> forCustomer (w.Get "c1")
                |> Filters.withOutstandingBalance

            w.MarkCommands()
            let! r = w.Repo.All q
            w.Set("orders", ok r)
        }
        |> unit

    [<StepDefinition("the executed query applies both restrictions in the database")>]
    member _.BothInDatabase() =
        let orders: Order list = w.Get "orders"
        equal [ w.Get<OrderId> "unpaid" ] (orders |> List.map (fun o -> o.Id)) "result"
        // The Orders' command (their OrderLines follow in one more).
        let cmd = w.CommandsSinceMark |> List.find (fun c -> c.Sql.Contains "FROM \"orders\"")
        check (cmd.Sql.Contains "\"customer_id\" = @") "customer predicate is in SQL"
        check (cmd.Sql.Contains "NOT EXISTS") "relationship predicate is in SQL"
        // Only the matching row crossed the wire: filtering happened in the database.
        equal 1 cmd.Rows "rows transferred"

    // Scenario: Captured values are treated as data

    [<StepDefinition("a Customer name containing SQL syntax")>]
    member _.HostileName() =
        task {
            let name = "Robert'); DROP TABLE orders; --"
            let! _ = Seed.customer w.Db name "bobby@example.com"
            let! _ = Seed.customer w.Db "Alice" "alice@example.com"
            w.Set("name", name)
        }
        |> unit

    [<StepDefinition("I use that name as a query parameter")>]
    member _.UseAsParameter() =
        task {
            let name: string = w.Get "name"
            let repo = w.Db.RepoWith(includeParameterValues = true)
            w.MarkCommands()
            let! r = repo.All(plasm { for c in stored<Customer> do where (c.Name = name) })
            w.Set("customers", ok r)
        }
        |> unit

    [<StepDefinition("the name is passed as a database parameter")>]
    member _.PassedAsParameter() =
        let name: string = w.Get "name"
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        check (not (cmd.Sql.Contains "DROP")) "the value is not part of the SQL text"
        equal [ Some name ] (cmd.Parameters |> List.map snd) "parameter values"

    [<StepDefinition("it cannot alter the command structure")>]
    member _.CannotAlter() =
        task {
            let customers: Customer list = w.Get "customers"
            equal [ w.Get<string> "name" ] (customers |> List.map (fun c -> c.Name)) "matched customers"
            let! n = w.Db.Scalar<int64>("select count(*) from information_schema.tables where table_name = 'orders'")
            equal 1L n "orders table still exists"
        }
        |> unit

    // Scenario: Unsupported translation is explicit

    [<StepDefinition("a query contains an expression that cannot be translated")>]
    member _.Untranslatable() =
        task {
            let! _ = Seed.order w.Db None "published" 10m (Seed.at 1)
            w.Set("query", plasm { for o in stored<Order> do where (Filters.isRound o.Total) })
        }
        |> unit

    [<StepDefinition("I attempt to execute it")>]
    member _.AttemptExecute() =
        task {
            w.MarkCommands()
            let! r = w.Repo.All(w.Get<Query<Order>> "query")
            w.Set("result", r)
            w.Set("failure", (match r with Error e -> Some e | Ok _ -> None))
            w.Set("expectedExpression", "isRound")
        }
        |> unit

    [<StepDefinition("execution fails with the unsupported expression identified")>]
    member _.FailsIdentified() =
        match w.Get<DbError option> "failure" with
        | Some(DbError.Translation(TranslationError.UnsupportedExpression(expr, reason))) ->
            check (expr.Contains(w.Get<string> "expectedExpression")) $"expression identified: {expr}"
            check (reason.Contains "will not be evaluated in application memory") $"reason: {reason}"
        | other -> failwithf "expected UnsupportedExpression, got %A" other

    [<StepDefinition("the library does not silently load rows for client-side filtering")>]
    member _.NoClientSide() = equal 0 w.CommandsSinceMark.Length "commands"

    // Scenario: Client-side processing is an explicit boundary

    [<StepDefinition("I explicitly execute a query into immutable values")>]
    member _.ExecuteIntoValues() =
        task {
            let! _ = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! _ = Seed.order w.Db None "published" 25m (Seed.at 2)
            let! r = w.Repo.All(plasm { for o in stored<Order> do orderBy o.Id })
            w.Set("orders", ok r)
        }
        |> unit

    [<StepDefinition("I apply an ordinary F# function to those values")>]
    member _.ApplyFunction() =
        w.MarkCommands()
        let orders: Order list = w.Get "orders"
        w.Set("filtered", orders |> List.filter (fun o -> Filters.isRound o.Total))

    [<StepDefinition("the function runs in application memory")>]
    member _.RunsInMemory() =
        equal [ 10m ] (w.Get<Order list> "filtered" |> List.map (fun o -> o.Total)) "filtered totals"

    [<StepDefinition("no additional database commands are issued")>]
    member _.NoAdditional() = equal 0 w.CommandsSinceMark.Length "commands"

    // Scenario: Projection retrieves the requested data

    [<StepDefinition("a query projecting Order identifiers and totals")>]
    member _.Projection() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "draft" 20m (Seed.at 2)
            w.Set("expected", [ a, 10m; b, 20m ])

            let projection =
                plasm {
                    for o in stored<Order> do
                        orderBy o.Id
                        select (o.Id, o.Total)
                }

            w.Pending <-
                fun () ->
                    task {
                        w.MarkCommands()
                        let! r = w.Repo.All projection
                        w.Set("rows", ok r)
                    }
        }
        |> unit

    [<StepDefinition("unrelated mapped scalar columns are not retrieved")>]
    member _.OnlyProjected() =
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        let selectList = cmd.Sql.Substring(0, cmd.Sql.IndexOf " FROM ")
        check (selectList.Contains "\"id\"" && selectList.Contains "\"total\"") selectList

        for unrelated in [ "status"; "customer_id"; "cancellation_reason"; "placed_at"; "version" ] do
            check (not (selectList.Contains unrelated)) $"{unrelated} is not selected: {selectList}"

    [<StepDefinition("complete Order entities are not materialized")>]
    member _.NoEntities() =
        // The result type is (OrderId * decimal) list: no Order value exists to inspect.
        let rows: (OrderId * decimal) list = w.Get "rows"
        equal (w.Get<(OrderId * decimal) list> "expected") rows "projected rows"
