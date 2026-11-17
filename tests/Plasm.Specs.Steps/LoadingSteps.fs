namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module Loads =
    /// The successful Payments of some Orders, in the order they were made: a
    /// reusable query over another aggregate, reached by the Orders' ids.
    let successfulPaymentsOf =
        Query.prepare (fun (ids: Arg<OrderId list>) ->
            plasm {
                for p in tableOf<Payment> do
                    where (List.contains p.OrderId ids.Value && p.Status = Succeeded)
                    orderBy p.CreatedAt
            })

/// Rule: An aggregate is loaded whole, and other aggregates are reached by query
[<Binding>]
type LoadingSteps(w: World) =

    let byId (id: OrderId) = plasm { for o in tableOf<Order> do where (o.Id = id) }

    let loadOrders (q: Query<Order>) =
        task {
            let! r = w.Repo.All q
            return ok r
        }

    // Scenario: Reading a relationship never triggers hidden database access

    [<StepDefinition("an Order with an OrderLine, read from the database")>]
    member _.OrderWithLineRead() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! p = Seed.product w.Db "SKU"
            let! _ = Seed.line w.Db id p 1 1m
            let! os = loadOrders (byId id)
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("application code inspects its OrderLines")>]
    member _.Inspect() =
        w.MarkCommands()
        let o: Order = w.Get "order"
        equal 1 o.Lines.Length "lines"
        equal o.Id o.Lines.Head.OrderId "the line's order"

    // Scenario: Sibling collections load without a cartesian product

    [<StepDefinition("an Order has {int} OrderLines")>]
    member _.OrderHasLines(n: int) =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! p = Seed.product w.Db "SKU"

            for i in 1..n do
                let! _ = Seed.line w.Db id p i 1m
                ()

            w.Set("id", id)
            w.Set("lines", n)
        }
        |> unit

    [<StepDefinition("the Order has {int} Payments")>]
    member _.OrderHasPayments(n: int) =
        task {
            for i in 1..n do
                let! _ = Seed.payment w.Db (w.Get "id") 1m "succeeded" (Seed.at i)
                ()

            w.Set("payments", n)
        }
        |> unit

    [<StepDefinition("I read the Order, and its Payments with a query by its id")>]
    member _.ReadBoth() =
        task {
            let id: OrderId = w.Get "id"
            w.MarkCommands()
            let! os = loadOrders (byId id)
            let! ps = w.Repo.All(plasm { for p in tableOf<Payment> do where (p.OrderId = id) })
            w.Set("order", List.exactlyOne os)
            w.Set("orderPayments", ok ps)
        }
        |> unit

    [<StepDefinition("it does not transfer their {int}-row cross product")>]
    member _.NoCrossProduct(product: int) =
        let rows = rowsOf w.CommandsSinceMark
        equal 3 w.CommandsSinceMark.Length "commands"
        equal (1 + w.Get<int> "lines" + w.Get<int> "payments") rows "rows transferred"
        check (rows < product) $"{rows} rows < {product}"

    [<StepDefinition("each related record appears once in its collection")>]
    member _.AppearsOnce() =
        let o: Order = w.Get "order"
        let lines = o.Lines |> List.map (fun l -> l.Id)
        let payments = w.Get<Payment list> "orderPayments" |> List.map (fun p -> p.Id)
        equal (w.Get<int> "lines") lines.Length "lines"
        equal (w.Get<int> "payments") payments.Length "payments"
        equal lines (List.distinct lines) "line identities"
        equal payments (List.distinct payments) "payment identities"

    // Scenario: Relationship loading batches across parents

    [<StepDefinition("a query returns {int} Orders")>]
    member _.ManyOrders(n: int) =
        task {
            let! p = Seed.product w.Db "SKU"

            let! _ =
                w.Db.Exec(
                    $"insert into orders(status, total, placed_at) select 'published', g, now() from generate_series(1, {n}) g"
                )

            let! _ =
                w.Db.Exec(
                    "insert into order_lines(order_id, product_id, quantity, unit_price) select o.id, @p, k, 1 from orders o, generate_series(1, 2) k",
                    [ "p", box (let (ProductId v) = p in v) ]
                )

            w.Set("count", n)
        }
        |> unit

    [<StepDefinition("I read them, each with the OrderLines it owns")>]
    member _.ReadWithLines() =
        task {
            w.MarkCommands()
            let! os = loadOrders (Query.from (tableOf<Order>))
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("the library fetches OrderLines in batches across those Orders")>]
    member _.Batched() =
        equal (w.Get<int> "count") (w.Get<Order list> "orders").Length "orders"
        equal 1 (commandsOn "order_lines" w.CommandsSinceMark).Length "OrderLine commands"

    [<StepDefinition("it does not issue one OrderLine query per Order")>]
    member _.NotPerOrder() = equal 2 w.CommandsSinceMark.Length "total commands"

    [<StepDefinition("it associates each OrderLine with its owning Order")>]
    member _.Associates() =
        for o in w.Get<Order list> "orders" do
            equal 2 o.Lines.Length "lines per order"
            check (o.Lines |> List.forall (fun l -> l.OrderId = o.Id)) "lines belong to their order"

    // Scenario: Pagination applies to parents before collection expansion

    [<StepDefinition("Orders have a deterministic ordering")>]
    member _.DeterministicOrdering() =
        task {
            let! p = Seed.product w.Db "SKU"
            let expected = Collections.Generic.List<OrderId * int>()

            for i in 1..30 do
                let! id = Seed.order w.Db None "published" (decimal i) (Seed.at (100 - i))
                let lines = i % 6

                for k in 1..lines do
                    let! _ = Seed.line w.Db id p k 1m
                    ()

                expected.Add((id, lines))

            // Placed-at descends with i, so ascending order is i = 30, 29, ...
            w.Set("expected", expected |> Seq.rev |> Seq.truncate 20 |> List.ofSeq)

            w.Set(
                "page",
                plasm {
                    for o in tableOf<Order> do
                        orderBy o.PlacedAt
                        take 20
                }
            )
        }
        |> unit

    [<StepDefinition("I request the first 20 Orders")>]
    member _.RequestPage() =
        task {
            let! os = loadOrders (w.Get "page")
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("exactly the first 20 matching Orders are returned")>]
    member _.First20() =
        let expected: (OrderId * int) list = w.Get "expected"
        equal (expected |> List.map fst) (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "page"

    [<StepDefinition("their OrderLines are complete")>]
    member _.Complete() =
        let expected: (OrderId * int) list = w.Get "expected"

        for (id, lines), o in List.zip expected (w.Get<Order list> "orders") do
            equal id o.Id "order"
            equal lines o.Lines.Length $"lines of {id}"

    [<StepDefinition("child row counts do not change which Orders are selected")>]
    member _.ChildCountsIrrelevant() =
        task {
            let! ids =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.PlacedAt
                            take 20
                            select o.Id
                    }
                )

            equal (ok ids) (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "the same page, read without its lines"
        }
        |> unit

    // Scenario: Parent filtering through relationships is explicit

    [<StepDefinition("I query Orders having at least one successful Payment")>]
    member _.HavingSuccessful() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! _ = Seed.payment w.Db a 10m "succeeded" (Seed.at 4)
            let! _ = Seed.payment w.Db b 2m "failed" (Seed.at 6)
            w.Set("expected", [ a ])
            w.MarkCommands()

            let! os =
                loadOrders (
                    plasm {
                        for o in tableOf<Order> do
                            where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) }))
                    }
                )

            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("only Orders satisfying that relationship predicate are returned")>]
    member _.OnlySatisfying() =
        equal (w.Get<OrderId list> "expected") (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "orders"

    [<StepDefinition("their Payments are read only by a query of their own")>]
    member _.PaymentsSeparate() =
        // The filter is a subquery of the Orders' command (payments as t1);
        // no command reads payments as its own rows.
        equal 2 w.CommandsSinceMark.Length "commands: the Orders and their OrderLines"
        check (w.CommandsSinceMark |> List.forall (fun c -> not (c.Sql.Contains "FROM \"payments\" AS \"t0\""))) "no command reads Payments"

    // Scenario: Filtered relationship queries are reusable

    [<StepDefinition("a reusable query for successful Payments ordered by creation time")>]
    member _.ReusablePaymentQuery() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            // Inserted out of chronological order.
            let! _ = Seed.payment w.Db a 1m "succeeded" (Seed.at 30)
            let! _ = Seed.payment w.Db a 2m "succeeded" (Seed.at 10)
            let! _ = Seed.payment w.Db a 3m "failed" (Seed.at 20)
            let! _ = Seed.payment w.Db b 4m "succeeded" (Seed.at 5)
            w.Set("ids", [ a; b ])
        }
        |> unit

    [<StepDefinition("I use it for the Payments of a page of Orders")>]
    member _.UseForPage() =
        task {
            let! orders = loadOrders (plasm { for o in tableOf<Order> do orderBy o.Id })
            w.MarkCommands()
            let! ps = w.Repo.All(Loads.successfulPaymentsOf (orders |> List.map (fun o -> o.Id)))
            w.Set("orders", orders)
            w.Set("pagePayments", ok ps)
        }
        |> unit

    [<StepDefinition("the filter and ordering are applied in the database")>]
    member _.AppliedInDatabase() =
        let cmd = commandsOn "payments" w.CommandsSinceMark |> List.exactlyOne
        check (cmd.Sql.Contains "\"status\" = ") "filter in SQL"
        check (cmd.Sql.Contains "ORDER BY \"t0\".\"created_at\"") "ordering in SQL"
        equal 3 cmd.Rows "only successful payments transferred"

    [<StepDefinition("the application groups the Payments by their Order")>]
    member _.Grouped() =
        let orders: Order list = w.Get "orders"
        let byOrder = w.Get<Payment list> "pagePayments" |> List.groupBy (fun p -> p.OrderId) |> Map.ofList

        let amounts =
            orders
            |> List.map (fun o -> byOrder |> Map.tryFind o.Id |> Option.defaultValue [] |> List.map (fun p -> p.Amount))

        equal [ [ 2m; 1m ]; [ 4m ] ] amounts "payments per order in creation order"

    // Scenario: Provider parameter limits do not cause per-parent loading

    [<StepDefinition("a relationship load contains more parent keys than one command permits")>]
    member _.TooManyKeys() =
        task {
            let! p = Seed.product w.Db "SKU"

            for i in 1..10 do
                let! id = Seed.order w.Db None "published" (decimal i) (Seed.at i)

                for k in 1 .. (i % 3) + 1 do
                    let! _ = Seed.line w.Db id p k 1m
                    ()

            let q = plasm { for o in tableOf<Order> do orderBy o.Id }
            let limited = w.Db.RepoWith(options = { MaxKeysPerCommand = 3 })

            w.Pending <-
                fun () ->
                    task {
                        w.MarkCommands()
                        let! small = limited.All q
                        w.Set("limitedCommands", w.CommandsSinceMark)
                        let! full = w.Repo.All q
                        w.Set("limited", ok small)
                        w.Set("full", ok full)
                    }
        }
        |> unit

    [<StepDefinition("keys are divided into supported batches")>]
    member _.Divided() =
        let cmds: CommandEvent list = w.Get "limitedCommands"
        // 10 orders -> 4 batches of at most 3 keys.
        equal 4 (commandsOn "order_lines" cmds).Length "OrderLine batches"

    [<StepDefinition("the assembled result is equivalent to an unrestricted batch load")>]
    member _.Equivalent() =
        equal (w.Get<Order list> "full") (w.Get<Order list> "limited") "assembled aggregates"
