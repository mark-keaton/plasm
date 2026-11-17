namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module Joins =
    let withCustomers =
        plasm {
            for o in tableOf<Order> do
                join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
        }

    let withOptionalCustomers =
        plasm {
            for o in tableOf<Order> do
                leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
        }

    /// A reusable filter over a joined query: an ordinary function.
    let largeOrdersOfAda (q: Query<Order * Customer>) =
        plasm {
            for (o, c) in q do
                where (c.Name = "Ada" && o.Total > 15m)
        }

    /// An ordinary F# function the translator cannot see into.
    let customerKeyOf (o: Order) = o.CustomerId

/// Feature: Explicit, typed joins
[<Binding>]
type JoinSteps(w: World) =

    let ids (orders: Order list) = orders |> List.map (fun o -> o.Id)

    let run (q: Query<'T>) =
        task {
            w.MarkCommands()
            let! r = w.Repo.All q
            return ok r
        }

    let compiles (code: string) =
        task {
            let! r = CompileCheck.check code
            check r.Compiles ("expected to compile:\n" + code + "\n" + String.concat "\n" r.Errors)
        }

    let rejected (code: string) =
        task {
            let! r = CompileCheck.check code
            // `join` is overloaded (schema or query as the outer source), so a key
            // type mismatch is reported as FS0041 naming both key types.
            check
                (r.Errors |> List.exists (fun e -> e.StartsWith "FS0041" && e.Contains "Join"))
                $"expected FS0041 for:\n{code}\ngot: %A{r.Errors}"
        }

    // Shared data: Ada has orders 1 and 2, Bob has order 3, order 4 has no customer.
    [<StepDefinition("Orders with and without a Customer")>]
    member _.OrdersWithAndWithout() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! o1 = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db (Some ada) "published" 20m (Seed.at 2)
            let! o3 = Seed.order w.Db (Some bob) "published" 30m (Seed.at 3)
            let! o4 = Seed.order w.Db None "draft" 40m (Seed.at 4)
            w.Set("ada", ada)
            w.Set("bob", bob)
            w.Set("orders", [ o1; o2; o3; o4 ])
            w.Set("withoutCustomer", o4)
            w.Set("explain", fun () -> w.Repo.Explain Joins.withCustomers)
        }
        |> unit

    [<StepDefinition("Orders for several Customers")>]
    member this.OrdersForSeveral() = this.OrdersWithAndWithout()

    [<StepDefinition("a joined query of Orders and Customers")>]
    member this.JoinedQuery() =
        task {
            do! this.OrdersWithAndWithout()
            w.Set("joined", Joins.withCustomers)
            w.Set("before", (Joins.withCustomers.FilterCount, (w.Repo.Explain Joins.withCustomers |> ok).Sql))
        }
        |> unit

    // Scenario: An inner join returns matching pairs

    [<StepDefinition("I join Orders to Customers on the customer key")>]
    member _.InnerJoin() =
        task {
            let! pairs = run Joins.withCustomers
            w.Set("pairs", pairs)
        }
        |> unit

    [<StepDefinition("each result pairs an Order with its Customer")>]
    member _.Pairs() =
        let pairs: (Order * Customer) list = w.Get "pairs"
        equal 3 pairs.Length "pairs"

        for o, c in pairs do
            equal (Some c.Id) o.CustomerId $"customer of {o.Id}"
            check (o.Lines |> List.forall (fun l -> l.OrderId = o.Id)) "an Order comes with the lines it owns"

    [<StepDefinition("Orders without a Customer are not returned")>]
    member _.NoOrphans() =
        let pairs: (Order * Customer) list = w.Get "pairs"
        check (pairs |> List.forall (fun (o, _) -> o.Id <> w.Get "withoutCustomer")) "order without customer excluded"

    [<StepDefinition("the join runs as a single database command")>]
    member _.SingleCommand() =
        // The joined rows come from one command; the Orders' lines, which they
        // own, from one more.
        let cmd = w.CommandsSinceMark |> List.filter (fun c -> c.Sql.Contains " JOIN ") |> List.exactlyOne
        check (w.CommandsSinceMark |> List.forall (fun c -> c = cmd || c.Sql.Contains "FROM \"order_lines\"")) "only the owned lines besides the join"
        check (cmd.Sql.Contains "INNER JOIN \"customers\" AS \"j1\" ON \"t0\".\"customer_id\" = \"j1\".\"id\"") cmd.Sql

    // Scenario: A left join represents a missing match as None

    [<StepDefinition("I left join Orders to Customers on the customer key")>]
    member _.LeftJoin() =
        task {
            let! rows = run Joins.withOptionalCustomers
            w.Set("optionalPairs", rows)
        }
        |> unit

    [<StepDefinition("every Order is returned exactly once")>]
    member _.EveryOrderOnce() =
        let rows: (Order * Customer option) list = w.Get "optionalPairs"
        equal (w.Get<OrderId list> "orders") (rows |> List.map (fun (o, _) -> o.Id)) "orders"
        check ((w.QueryCommands |> List.exactlyOne).Sql.Contains "LEFT JOIN") "a LEFT JOIN was executed"

    [<StepDefinition("an Order without a Customer is paired with None")>]
    member _.PairedWithNone() =
        let rows: (Order * Customer option) list = w.Get "optionalPairs"
        let _, c = rows |> List.find (fun (o, _) -> o.Id = w.Get "withoutCustomer")
        equal None c "customer"

    [<StepDefinition("an Order with a Customer is paired with Some Customer")>]
    member _.PairedWithSome() =
        let rows: (Order * Customer option) list = w.Get "optionalPairs"

        for o, c in rows |> List.filter (fun (o, _) -> o.CustomerId.IsSome) do
            match c with
            | Some c -> equal o.CustomerId (Some c.Id) $"customer of {o.Id}"
            | None -> failwithf "order %A lost its customer" o.Id

    // Scenario: Join keys are checked at compile time

    [<StepDefinition("I attempt to join on keys of different types")>]
    member _.MismatchedKeys() =
        w.Set(
            "snippets",
            [ "let q = plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.CustomerId = c.Id); orderBy o.Id }"
              "let q = plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.Id = c.Id); orderBy o.Id }" ]
        )

    [<StepDefinition("the join is rejected at compile time")>]
    member _.JoinRejected() =
        task {
            do! compiles "let q = plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.CustomerId = Some c.Id); orderBy o.Id }"

            for code in w.Get<string list> "snippets" do
                do! rejected code
        }
        |> unit

    // Scenario: Filters on joined columns run in the database

    [<StepDefinition("I join Orders to Customers and filter on the Customer name")>]
    member _.FilterJoined() =
        task {
            let! pairs =
                run (
                    plasm {
                        for o in tableOf<Order> do
                            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                            where (c.Name = "Ada")
                    }
                )

            w.Set("pairs", pairs)
        }
        |> unit

    [<StepDefinition("the filter is part of the executed SQL")>]
    member _.FilterInSql() =
        let cmd = w.QueryCommands |> List.exactlyOne
        check (cmd.Sql.Contains "WHERE \"j1\".\"name\" = @p0") cmd.Sql

    [<StepDefinition("only matching rows are transferred")>]
    member _.OnlyMatching() =
        equal 2 (w.QueryCommands |> List.exactlyOne).Rows "rows transferred"

        for _, c in w.Get<(Order * Customer) list> "pairs" do
            equal "Ada" c.Name "customer"

    // Scenario: Left-joined rows are filtered with option semantics

    [<StepDefinition("I keep only left-joined Orders whose Customer is None")>]
    member _.KeepNone() =
        task {
            let! rows = run (plasm { for (o, c) in Joins.withOptionalCustomers do where c.IsNone })
            w.Set("kept", rows |> List.map (fun (o: Order, _) -> o))
            check ((w.QueryCommands |> List.exactlyOne).Sql.Contains "\"j1\".\"id\" IS NULL") "anti-join in SQL"
        }
        |> unit

    [<StepDefinition("only Orders without a Customer are returned")>]
    member _.OnlyWithout() =
        equal [ w.Get<OrderId> "withoutCustomer" ] (ids (w.Get "kept")) "orders"

    [<StepDefinition("I keep only left-joined Orders whose Customer satisfies a predicate")>]
    member _.KeepPredicate() =
        task {
            let! rows =
                run (
                    plasm {
                        for (o, c) in Joins.withOptionalCustomers do
                            where (c |> Option.exists (fun c -> c.Name = "Bob"))
                    }
                )

            w.Set("kept", rows |> List.map (fun (o: Order, _) -> o))
        }
        |> unit

    [<StepDefinition("only Orders whose Customer satisfies it are returned")>]
    member _.OnlySatisfying() =
        let bobs = w.Get<OrderId list> "orders" |> List.item 2
        equal [ bobs ] (ids (w.Get "kept")) "orders"

    // Scenario: A join returns one row per matching pair

    [<StepDefinition("an Order with {int} Payments")>]
    member _.OrderWithPayments(n: int) =
        task {
            let! target = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! other = Seed.order w.Db None "published" 20m (Seed.at 2)

            for i in 1..n do
                let! _ = Seed.payment w.Db target (decimal i) "succeeded" (Seed.at i)
                ()

            let! _ = Seed.payment w.Db other 1m "succeeded" (Seed.at 9)
            w.Set("target", target)
            w.Set("count", n)
        }
        |> unit

    [<StepDefinition("I join Orders to Payments")>]
    member _.JoinPayments() =
        task {
            let target: OrderId = w.Get "target"

            let! rows =
                run (
                    plasm {
                        for o in tableOf<Order> do
                            join p in tableOf<Payment> on (o.Id = p.OrderId)
                            where (o.Id = target)
                    }
                )

            w.Set("rows", rows)
        }
        |> unit

    [<StepDefinition("the Order appears once for each Payment")>]
    member _.OncePerPayment() =
        let rows: (Order * Payment) list = w.Get "rows"
        equal (w.Get<int> "count") rows.Length "rows"
        equal rows.Length (rows |> List.map (fun (_, p) -> p.Id) |> List.distinct).Length "distinct payments"

    [<StepDefinition("filtering through a relationship predicate still returns the Order once")>]
    member _.RelationshipOnce() =
        task {
            let target: OrderId = w.Get "target"

            let! orders =
                run (
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Id = target && exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) }))
                    }
                )

            equal [ target ] (ids orders) "orders"
        }
        |> unit

    // Scenario: Pagination over joined rows is stable

    [<StepDefinition("joined rows that tie on the requested ordering")>]
    member _.TyingRows() =
        task {
            for i in 1..3 do
                let! o = Seed.order w.Db None "published" (decimal i) (Seed.at i)

                for k in 1..2 do
                    let! _ = Seed.payment w.Db o 1m "succeeded" (Seed.at k)
                    ()
        }
        |> unit

    [<StepDefinition("I page through the joined rows")>]
    member _.PageThrough() =
        task {
            let page n =
                plasm {
                    for o in tableOf<Order> do
                        join p in tableOf<Payment> on (o.Id = p.OrderId)
                        orderBy p.Status // every row ties
                        skip (n * 4)
                        take 4
                }

            w.Set("pageSql", (w.Repo.Explain(page 0) |> ok).Sql)
            let! first = run (page 0)
            let! second = run (page 1)
            w.Set("paged", first @ second)
        }
        |> unit

    [<StepDefinition("the ordering includes the key of every joined source")>]
    member _.OrderingHasKeys() =
        let sql: string = w.Get "pageSql"
        check (sql.Contains "ORDER BY (CASE \"j1\".\"status\" WHEN 'pending' THEN 0 WHEN 'succeeded' THEN 1 WHEN 'failed' THEN 2 END), \"t0\".\"id\", \"j1\".\"id\"") sql

    [<StepDefinition("the pages are disjoint and together complete")>]
    member _.DisjointComplete() =
        let rows: (Order * Payment) list = w.Get "paged"
        let keys = rows |> List.map (fun (o, p) -> o.Id, p.Id)
        equal 6 keys.Length "rows across pages"
        equal 6 (List.distinct keys).Length "distinct rows across pages"

    [<StepDefinition("paging a join without an ordering is rejected")>]
    member _.UnorderedRejected() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            join p in tableOf<Payment> on (o.Id = p.OrderId)
                            take 4
                    }
                )

            match r with
            | Error(DbError.Translation(TranslationError.InvalidQuery msg)) -> check (msg.Contains "requires an explicit ordering") msg
            | other -> failwithf "expected InvalidQuery, got %A" other

            equal 0 w.CommandsSinceMark.Length "commands"
        }
        |> unit

    // Scenario: Several joins compose in one query

    [<StepDefinition("Orders with OrderLines for several Products")>]
    member _.LinesForProducts() =
        task {
            let! a = Seed.product w.Db "A"
            let! b = Seed.product w.Db "B"
            let! o1 = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! _ = Seed.line w.Db o1 a 1 1m
            let! _ = Seed.line w.Db o1 b 2 1m
            let! _ = Seed.line w.Db o2 a 3 1m
            ()
        }
        |> unit

    [<StepDefinition("I join Orders to OrderLines and OrderLines to Products")>]
    member _.TwoJoins() =
        task {
            let! rows =
                run (
                    plasm {
                        for o in tableOf<Order> do
                            join l in tableOf<OrderLine> on (o.Id = l.OrderId)
                            join p in tableOf<Product> on (l.ProductId = p.Id)
                            orderBy l.Id
                    }
                )

            w.Set("triples", rows)
        }
        |> unit

    [<StepDefinition("each result combines an Order, a line and its Product")>]
    member _.Triples() =
        let rows: (Order * OrderLine * Product) list = w.Get "triples"
        equal 3 rows.Length "rows"

        for o, l, p in rows do
            equal o.Id l.OrderId "line belongs to order"
            equal l.ProductId p.Id "product of line"

        equal [ "A"; "B"; "A" ] (rows |> List.map (fun (_, _, p) -> p.Sku)) "products in line order"

    [<StepDefinition("the joins run as a single database command")>]
    member _.JoinsSingleCommand() =
        let cmd = w.QueryCommands |> List.exactlyOne
        check (cmd.Sql.Contains "INNER JOIN \"order_lines\" AS \"j1\"") cmd.Sql
        check (cmd.Sql.Contains "INNER JOIN \"products\" AS \"j2\" ON \"j1\".\"product_id\" = \"j2\".\"id\"") cmd.Sql

    // Scenario: Projections over joins retrieve only the requested columns

    [<StepDefinition("I project Order identifiers with left-joined Customer names")>]
    member _.ProjectNames() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                            orderBy o.Id
                            select (o.Id, c |> Option.map (fun c -> c.Name))
                    }
                )

            w.Set("names", ok r)
        }
        |> unit

    [<StepDefinition("only the projected columns are retrieved")>]
    member _.OnlyProjectedColumns() =
        let sql = (w.QueryCommands |> List.exactlyOne).Sql
        let selectList = sql.Substring(0, sql.IndexOf " FROM ")
        equal "SELECT \"t0\".\"id\" AS \"c0\", \"j1\".\"name\" AS \"c1\"" selectList "select list"

    [<StepDefinition("a missing Customer name is represented as None")>]
    member _.MissingNameIsNone() =
        let names: (OrderId * string option) list = w.Get "names"
        equal [ Some "Ada"; Some "Ada"; Some "Bob"; None ] (names |> List.map snd) "names"

    // Scenario: Joined queries compose through ordinary functions

    [<StepDefinition("a function adds a filter over both sources")>]
    member _.AddFilter() =
        task {
            let derived = Joins.largeOrdersOfAda (w.Get "joined")
            w.Set("derived", derived)
            let! rows = run derived
            w.Set("pairs", rows)
        }
        |> unit

    [<StepDefinition("the original joined query is unchanged")>]
    member _.JoinedUnchanged() =
        let q: Query<Order * Customer> = w.Get "joined"
        equal (w.Get<int * string> "before") (q.FilterCount, (w.Repo.Explain q |> ok).Sql) "joined query"

    [<StepDefinition("the derived query applies the filter in the database")>]
    member _.DerivedFilters() =
        let rows: (Order * Customer) list = w.Get "pairs"
        equal [ (w.Get<OrderId list> "orders").[1] ] (rows |> List.map (fun (o, _) -> o.Id)) "orders"
        let cmd = w.QueryCommands |> List.exactlyOne
        check (cmd.Sql.Contains "\"j1\".\"name\" = @p0" && cmd.Sql.Contains "\"t0\".\"total\" > @p1") cmd.Sql
        equal 1 cmd.Rows "rows transferred"

    // Scenario: Joined queries can be inspected without database access

    [<StepDefinition("the plan shows the join and its condition")>]
    member _.PlanShowsJoin() =
        let plan: QueryPlan = w.Get "plan"
        check (plan.Sql.Contains "INNER JOIN \"customers\" AS \"j1\" ON \"t0\".\"customer_id\" = \"j1\".\"id\"") plan.Sql

    // Scenario: Unsupported join requests are explicit

    [<StepDefinition("I join on an expression that cannot be translated")>]
    member _.UntranslatableJoin() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            join c in tableOf<Customer> on (Joins.customerKeyOf o = Some c.Id)
                            orderBy o.Id
                    }
                )

            w.Set("failure", (match r with Error e -> Some e | Ok _ -> None))
            w.Set("expectedExpression", "customerKeyOf")
        }
        |> unit

    [<StepDefinition("the request is rejected with an explanation")>]
    member _.RejectedWithExplanation() =
        // Shared: a feature sets "expectedExplanation" to the phrases it expects.
        let expected = w.Get<string list> "expectedExplanation"

        match w.Get<DbError option> "loadFailure" with
        | Some(DbError.Translation(TranslationError.InvalidQuery msg)) ->
            for phrase in expected do
                check (msg.Contains phrase) $"expected '{phrase}' in: {msg}"
        | other -> failwithf "expected InvalidQuery, got %A" other

    // Scenario: Joined queries run inside transactions

    [<StepDefinition("a transaction step reads a joined query")>]
    member _.TxReadsJoin() =
        task {
            let! r = w.Repo.Transact(Tx.all Joins.withCustomers)
            w.Set("pairs", ok r)
        }
        |> unit

    [<StepDefinition("the step receives the joined rows")>]
    member _.StepReceivesRows() =
        let pairs: (Order * Customer) list = w.Get "pairs"
        equal 3 pairs.Length "pairs"

        for o, c in pairs do
            equal (Some c.Id) o.CustomerId $"customer of {o.Id}"
