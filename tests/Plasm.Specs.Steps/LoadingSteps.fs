namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A provider whose dialect lacks per-parent limits (to test rejection).
type LimitedProvider(inner: IProvider) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "LimitedSQL"
            member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
            member _.ParameterName i = inner.Dialect.ParameterName i
            member _.ColumnTypes t = inner.Dialect.ColumnTypes t

            member _.Capabilities =
                { inner.Dialect.Capabilities with
                    PerParentLimit = false
                    SnapshotReads = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(_, _) = Task.FromResult None
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

module Loads =
    let withLinesAndProducts (q: Query<Order>) =
        plasm { for o in q do preload o.Lines linesWithProduct }

    let withPayments (q: Query<Order>) = plasm { for o in q do preload o.Payments }

    let withSuccessfulPayments (q: Query<Order>) =
        plasm { for o in q do preload o.Payments successfulPayments }

    let withFailedPayments (q: Query<Order>) =
        plasm {
            for o in q do
                preload o.Payments (plasm { for p in Schemas.payments do where (p.Status = Failed) })
        }

    let latestPayments n =
        plasm {
            for p in Schemas.payments do
                orderByDescending p.CreatedAt
                limitPerParent n
        }

/// Rule: Relationship loading is explicit, composable, and batched
[<Binding>]
type LoadingSteps(w: World) =

    let byId (id: OrderId) = plasm { for o in Schemas.orders do where (o.Id = id) }

    let loadOrders (q: Query<Order>) =
        task {
            let! r = w.Repo.All q
            return ok r
        }

    // Scenario: Unloaded and empty relationships are distinguishable

    [<StepDefinition("an Order with no Payments")>]
    member _.OrderWithoutPayments() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            w.Set("id", id)
        }
        |> unit

    [<StepDefinition("I read the Order without loading Payments")>]
    member _.ReadWithoutPayments() =
        task {
            let! os = loadOrders (byId (w.Get "id"))
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("Payments is represented as not loaded")>]
    member _.NotLoaded() =
        equal NotLoaded (w.Get<Order> "order").Payments "payments"

    [<StepDefinition("I explicitly load Payments")>]
    member _.LoadPayments() =
        task {
            let! os = loadOrders (byId (w.Get "id") |> Loads.withPayments)
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("Payments is represented as loaded and empty")>]
    member _.LoadedEmpty() =
        equal (Loaded []) (w.Get<Order> "order").Payments "payments"

    // Scenario: Reading a relationship never triggers hidden database access

    [<StepDefinition("an Order with an unloaded Payments relationship")>]
    member _.UnloadedPayments() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! _ = Seed.payment w.Db id 10m "succeeded" (Seed.at 2)
            let! os = loadOrders (byId id)
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("application code inspects that relationship")>]
    member _.Inspect() =
        w.MarkCommands()
        let o: Order = w.Get "order"

        let described =
            match o.Payments with
            | NotLoaded -> "not loaded"
            | Loaded ps -> $"{ps.Length} payments"

        w.Set("described", described)
        w.Set("tryGet", Assoc.tryGet o.Payments)

        w.Set(
            "getFails",
            try
                Assoc.get o.Payments |> ignore
                false
            with :? InvalidOperationException ->
                true
        )

    [<StepDefinition("its unloaded state remains observable")>]
    member _.StillUnloaded() =
        equal "not loaded" (w.Get<string> "described") "pattern match"
        equal None (w.Get<Payment list option> "tryGet") "Assoc.tryGet"
        check (w.Get<bool> "getFails") "Assoc.get fails instead of loading"
        equal NotLoaded (w.Get<Order> "order").Payments "value"

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

    [<StepDefinition("I request both collections using separate-query loading")>]
    member _.RequestBoth() =
        task {
            w.MarkCommands()
            let! os = loadOrders (byId (w.Get "id") |> Loads.withPayments |> fun q -> plasm { for o in q do preload o.Lines })
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("the library loads both collections automatically")>]
    member _.BothLoaded() =
        let o: Order = w.Get "order"
        equal (w.Get<int> "lines") (Assoc.get o.Lines).Length "lines"
        equal (w.Get<int> "payments") (Assoc.get o.Payments).Length "payments"

    [<StepDefinition("it does not transfer their {int}-row cross product")>]
    member _.NoCrossProduct(product: int) =
        let rows = rowsOf w.CommandsSinceMark
        equal 3 w.CommandsSinceMark.Length "commands"
        equal (1 + w.Get<int> "lines" + w.Get<int> "payments") rows "rows transferred"
        check (rows < product) $"{rows} rows < {product}"

    [<StepDefinition("each related record appears once in its collection")>]
    member _.AppearsOnce() =
        let o: Order = w.Get "order"
        let lines = Assoc.get o.Lines |> List.map (fun l -> l.Id)
        let payments = Assoc.get o.Payments |> List.map (fun p -> p.Id)
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

    [<StepDefinition("I request their OrderLines")>]
    member _.RequestLines() =
        task {
            w.MarkCommands()
            let! os = loadOrders (plasm { for o in Schemas.orders do preload o.Lines })
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
            let lines = Assoc.get o.Lines
            equal 2 lines.Length "lines per order"
            check (lines |> List.forall (fun l -> l.OrderId = o.Id)) "lines belong to their order"

    // Scenario: Nested loads compose

    [<StepDefinition("a reusable load specification for OrderLines and their Products")>]
    member _.ReusableLineLoad() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! p = Seed.product w.Db "WIDGET"
            let! _ = Seed.line w.Db id p 2 5m
            let! _ = Seed.payment w.Db id 10m "succeeded" (Seed.at 2)
            w.Set("id", id)
        }
        |> unit

    [<StepDefinition("I combine it with a load specification for Payments")>]
    member _.Combine() =
        w.Set("query", byId (w.Get "id") |> Loads.withLinesAndProducts |> Loads.withPayments)

    [<StepDefinition("the resulting query loads both branches")>]
    member _.BothBranches() =
        let plan = w.Repo.Explain(w.Get<Query<Order>> "query") |> ok
        equal [ "Lines"; "Lines.Product"; "Payments" ] plan.LoadPaths "load paths"

    [<StepDefinition("nested relationships are populated automatically")>]
    member _.NestedPopulated() =
        task {
            let! os = loadOrders (w.Get "query")
            let o = List.exactlyOne os
            let line = Assoc.get o.Lines |> List.exactlyOne
            equal "WIDGET" (Assoc.get line.Product).Sku "nested product"
            equal 1 (Assoc.get o.Payments).Length "payments"
        }
        |> unit

    // Scenario: Pagination applies to parents before collection expansion

    [<StepDefinition("Orders have a deterministic ordering")>]
    member _.DeterministicOrdering() =
        task {
            let! p = Seed.product w.Db "SKU"
            let expected = Collections.Generic.List<OrderId * int * int>()

            for i in 1..30 do
                let! id = Seed.order w.Db None "published" (decimal i) (Seed.at (100 - i))
                let lines = i % 6
                let payments = (i * 7) % 4

                for k in 1..lines do
                    let! _ = Seed.line w.Db id p k 1m
                    ()

                for k in 1..payments do
                    let! _ = Seed.payment w.Db id 1m "succeeded" (Seed.at k)
                    ()

                expected.Add((id, lines, payments))

            // Placed-at descends with i, so ascending order is i = 30, 29, ...
            w.Set("expected", expected |> Seq.rev |> Seq.truncate 20 |> List.ofSeq)

            w.Set(
                "page",
                plasm {
                    for o in Schemas.orders do
                        orderBy o.PlacedAt
                        take 20
                }
            )
        }
        |> unit

    [<StepDefinition("I request the first 20 Orders with OrderLines and Payments")>]
    member _.RequestPage() =
        task {
            let page: Query<Order> = w.Get "page"
            let! os = loadOrders (page |> Loads.withPayments |> fun q -> plasm { for o in q do preload o.Lines })
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("exactly the first 20 matching Orders are returned")>]
    member _.First20() =
        let expected: (OrderId * int * int) list = w.Get "expected"
        equal (expected |> List.map (fun (id, _, _) -> id)) (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "page"

    [<StepDefinition("their requested collections are complete")>]
    member _.Complete() =
        let expected: (OrderId * int * int) list = w.Get "expected"

        for (id, lines, payments), o in List.zip expected (w.Get<Order list> "orders") do
            equal id o.Id "order"
            equal lines (Assoc.get o.Lines).Length $"lines of {id}"
            equal payments (Assoc.get o.Payments).Length $"payments of {id}"

    [<StepDefinition("child row counts do not change which Orders are selected")>]
    member _.ChildCountsIrrelevant() =
        task {
            let! plain = loadOrders (w.Get "page")
            equal (plain |> List.map (fun o -> o.Id)) (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "same page without loads"
        }
        |> unit

    // Scenario: A child filter does not implicitly filter parents

    [<StepDefinition("I load Orders with only successful Payments")>]
    member _.LoadSuccessful() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! c = Seed.order w.Db None "published" 30m (Seed.at 3)
            let! _ = Seed.payment w.Db a 10m "succeeded" (Seed.at 4)
            let! _ = Seed.payment w.Db a 1m "failed" (Seed.at 5)
            let! _ = Seed.payment w.Db b 2m "failed" (Seed.at 6)
            ignore c
            let! os = loadOrders (plasm { for o in Schemas.orders do orderBy o.Id } |> Loads.withSuccessfulPayments)
            w.Set("orders", os)
            w.Set("ids", [ a; b; c ])
        }
        |> unit

    [<StepDefinition("all Orders matching the parent query are returned")>]
    member _.AllParents() =
        equal (w.Get<OrderId list> "ids") (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "orders"

    [<StepDefinition("each loaded Payments collection contains only successful Payments")>]
    member _.OnlySuccessful() =
        let orders: Order list = w.Get "orders"
        equal [ 1; 0; 0 ] (orders |> List.map (fun o -> (Assoc.get o.Payments).Length)) "payment counts"

        for o in orders do
            check (Assoc.get o.Payments |> List.forall (fun p -> p.Status = Succeeded)) "only successful"

    // Scenario: Parent filtering through relationships is explicit

    [<StepDefinition("I query Orders having at least one successful Payment")>]
    member _.HavingSuccessful() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! _ = Seed.payment w.Db a 10m "succeeded" (Seed.at 4)
            let! _ = Seed.payment w.Db b 2m "failed" (Seed.at 6)
            w.Set("expected", [ a ])

            let! os =
                loadOrders (
                    plasm {
                        for o in Schemas.orders do
                            where (o.Payments |> Rel.any (fun p -> p.Status = Succeeded))
                    }
                )

            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("only Orders satisfying that relationship predicate are returned")>]
    member _.OnlySatisfying() =
        equal (w.Get<OrderId list> "expected") (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "orders"

    [<StepDefinition("loading Payments remains a separate explicit choice")>]
    member _.LoadingSeparate() =
        for o in w.Get<Order list> "orders" do
            equal NotLoaded o.Payments "payments not loaded by the filter"

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

    [<StepDefinition("I use it as the Payments load for Orders")>]
    member _.UseAsLoad() =
        task {
            w.MarkCommands()
            let! os = loadOrders (plasm { for o in Schemas.orders do orderBy o.Id } |> Loads.withSuccessfulPayments)
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("the filter and ordering are applied in the database")>]
    member _.AppliedInDatabase() =
        let cmd = commandsOn "payments" w.CommandsSinceMark |> List.exactlyOne
        check (cmd.Sql.Contains "\"status\" = @") "filter in SQL"
        check (cmd.Sql.Contains "ORDER BY \"t0\".\"created_at\" ASC") "ordering in SQL"
        equal 3 cmd.Rows "only successful payments transferred"

    [<StepDefinition("Payments are assembled into the corresponding Orders")>]
    member _.Assembled() =
        let orders: Order list = w.Get "orders"
        let amounts = orders |> List.map (fun o -> Assoc.get o.Payments |> List.map (fun p -> p.Amount))
        equal [ [ 2m; 1m ]; [ 4m ] ] amounts "payments per order in creation order"

        for o in orders do
            check (Assoc.get o.Payments |> List.forall (fun p -> p.OrderId = o.Id)) "owned payments"

    // Scenario: Per-parent limits preserve their stated meaning

    [<StepDefinition("I request the latest {int} Payments for each Order")>]
    member _.LatestPerOrder(n: int) =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! c = Seed.order w.Db None "published" 30m (Seed.at 3)

            for i in 1..5 do
                let! _ = Seed.payment w.Db a (decimal i) "succeeded" (Seed.at i)
                ()

            for i in 1..2 do
                let! _ = Seed.payment w.Db b (decimal (10 + i)) "succeeded" (Seed.at i)
                ()

            for i in 1..4 do
                let! _ = Seed.payment w.Db c (decimal (20 + i)) "succeeded" (Seed.at i)
                ()

            let! os =
                loadOrders (
                    plasm {
                        for o in Schemas.orders do
                            orderBy o.Id
                            preload o.Payments (Loads.latestPayments n)
                    }
                )

            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("each Order receives at most its own latest {int} Payments")>]
    member _.AtMostOwn(n: int) =
        let orders: Order list = w.Get "orders"
        let amounts = orders |> List.map (fun o -> Assoc.get o.Payments |> List.map (fun p -> p.Amount))
        equal [ [ 5m; 4m; 3m ]; [ 12m; 11m ]; [ 24m; 23m; 22m ] ] amounts "latest payments per order"
        check (amounts |> List.forall (fun a -> a.Length <= n)) "at most n"

    [<StepDefinition("the limit is not applied globally across all Orders")>]
    member _.NotGlobal() =
        let total = w.Get<Order list> "orders" |> List.sumBy (fun o -> (Assoc.get o.Payments).Length)
        equal 8 total "total payments loaded"

    // Scenario: Unsupported loading semantics are rejected

    [<StepDefinition("the provider cannot implement a requested per-parent operation")>]
    member _.LimitedProviderStep() =
        let repo =
            Repo(w.Db.Model, LimitedProvider(w.Db.Provider), { RepoOptions.Default with OnCommand = Some w.Db.Recorder.Record })

        let q =
            plasm {
                for o in Schemas.orders do
                    preload o.Payments (Loads.latestPayments 3)
            }

        w.Pending <-
            fun () ->
                task {
                    w.MarkCommands()
                    let! r = repo.All q
                    w.Set("result", r)
                }

    [<StepDefinition("the library reports the unsupported operation")>]
    member _.ReportsUnsupported() =
        match w.Get<Result<Order list, DbError>> "result" with
        | Error(Translation(UnsupportedByProvider(op, provider))) ->
            check (op.Contains "per-parent limit") op
            equal "LimitedSQL" provider "provider"
        | other -> failwithf "expected UnsupportedByProvider, got %A" other

    [<StepDefinition("it does not substitute a global operation")>]
    member _.NoSubstitute() = equal 0 w.CommandsSinceMark.Length "commands"

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

            let q = plasm { for o in Schemas.orders do orderBy o.Id; preload o.Lines linesWithProduct }
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
        // 10 orders -> 4 batches of at most 3 keys; 1 product -> 1 batch.
        equal 4 (commandsOn "order_lines" cmds).Length "OrderLine batches"
        equal 1 (commandsOn "products" cmds).Length "Product batches"

    [<StepDefinition("the assembled result is equivalent to an unrestricted batch load")>]
    member _.Equivalent() =
        equal (w.Get<Order list> "full") (w.Get<Order list> "limited") "assembled graphs"

    // Scenario: Conflicting relationship customizations are explicit

    [<StepDefinition("two load specifications customize the same relationship differently")>]
    member _.TwoSpecs() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! _ = Seed.payment w.Db id 10m "succeeded" (Seed.at 2)
            let! _ = Seed.payment w.Db id 1m "failed" (Seed.at 3)
            ()
        }
        |> unit

    [<StepDefinition("I combine those specifications")>]
    member _.CombineSpecs() =
        // Combination is an ordinary, total function application.
        let combined = plasm { for o in Schemas.orders do orderBy o.Id } |> Loads.withSuccessfulPayments |> Loads.withFailedPayments
        w.Set("combined", combined)

    [<StepDefinition("the library reports a composition conflict")>]
    member _.ReportsConflict() =
        task {
            let combined: Query<Order> = w.Get "combined"

            match w.Repo.Explain combined with
            | Error(LoadConflict(path, _)) -> equal "Payments" path "conflict path"
            | other -> failwithf "expected LoadConflict, got %A" other

            w.MarkCommands()
            let! r = w.Repo.All combined

            match r with
            | Error(Translation(LoadConflict("Payments", _))) -> ()
            | other -> failwithf "expected LoadConflict, got %A" other

            equal 0 w.CommandsSinceMark.Length "no command before reporting"
        }
        |> unit

    [<StepDefinition("I must explicitly choose replacement or another supported resolution")>]
    member _.ExplicitResolution() =
        task {
            let combined: Query<Order> = w.Get "combined"
            let resolved = plasm { for o in combined do replacePreload o.Payments successfulPayments }
            let! os = loadOrders resolved
            let payments = Assoc.get (List.exactlyOne os).Payments
            equal [ Succeeded ] (payments |> List.map (fun p -> p.Status)) "replacement wins"
        }
        |> unit

    // Scenario: Loading onto existing values preserves the originals

    [<StepDefinition("immutable Orders already held by application code")>]
    member _.HeldOrders() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! _ = Seed.payment w.Db a 10m "succeeded" (Seed.at 3)
            ignore b
            let! os = loadOrders (plasm { for o in Schemas.orders do orderBy o.Id })
            w.Set("originals", os)
        }
        |> unit

    [<StepDefinition("I load their Payments")>]
    member _.LoadTheirPayments() =
        task {
            let originals: Order list = w.Get "originals"
            let! loaded = w.Repo.Preload(plasm { for o in Schemas.orders do preload o.Payments }, originals)
            w.Set("loaded", ok loaded)
        }
        |> unit

    [<StepDefinition("new values containing the loaded relationships are returned")>]
    member _.NewValues() =
        let loaded: Order list = w.Get "loaded"
        equal [ 1; 0 ] (loaded |> List.map (fun o -> (Assoc.get o.Payments).Length)) "payments"

        equal
            (w.Get<Order list> "originals" |> List.map (fun o -> o.Id))
            (loaded |> List.map (fun o -> o.Id))
            "same orders, same order"

    [<StepDefinition("the original values remain unchanged")>]
    member _.OriginalsUnchanged() =
        for o in w.Get<Order list> "originals" do
            equal NotLoaded o.Payments "original payments"
