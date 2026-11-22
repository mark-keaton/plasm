namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Prepared queries
/// (Data from "Orders for several Customers": Ada has orders 1 and 2, Bob order 3,
/// order 4 is a draft without a customer.)
[<Binding>]
type PreparedSteps(w: World) =

    let builds = ref 0

    let ordersOf =
        Query.prepare (fun (customer: Arg<CustomerId>) ->
            builds.Value <- builds.Value + 1

            plasm {
                for o in tableOf<Order> do
                    where (o.CustomerId = Some customer.Value)
                    orderBy o.Id
            })

    let orders () = w.Get<OrderId list> "orders"
    let ids (os: Order list) = os |> List.map (fun o -> o.Id)

    /// A repo that records parameter values.
    let repo () = w.Db.RepoWith(includeParameterValues = true)

    let prepareFails (f: unit -> unit) (phrase: string) =
        try
            f ()
            w.Set("prepareError", "")
        with :? InvalidOperationException as e ->
            w.Set("prepareError", e.Message)

        w.Set("expectedPrepareError", phrase)

    [<StepDefinition("Orders for several Customers with Payments")>]
    member _.WithPayments() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! o1 = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db (Some ada) "published" 20m (Seed.at 2)
            let! o3 = Seed.order w.Db (Some bob) "published" 30m (Seed.at 3)

            let! p = Seed.product w.Db "SKU"

            for o in [ o1; o2; o3 ] do
                let! _ = Seed.payment w.Db o 5m "succeeded" (Seed.at 1)
                let! _ = Seed.payment w.Db o 5m "failed" (Seed.at 2)
                let! _ = Seed.line w.Db o p 1 5m
                ()

            w.Set("ada", ada)
            w.Set("bob", bob)
            w.Set("orders", [ o1; o2; o3 ])
        }
        |> unit

    // Scenario: A prepared query is built once and runs with different arguments

    [<StepDefinition("I prepare a query for the Orders of a Customer")>]
    member _.Prepare() = w.Set("prepared", ordersOf)

    [<StepDefinition("I run it for each Customer")>]
    member _.RunEach() =
        task {
            let repo = repo ()
            w.MarkCommands()
            let! a = repo.All(ordersOf (w.Get "ada"))
            let! b = repo.All(ordersOf (w.Get "bob"))
            w.Set("runs", [ ok a; ok b ])
        }
        |> unit

    [<StepDefinition("each run returns that Customer's Orders")>]
    member _.EachRun() =
        let os = orders ()
        equal [ [ os.[0]; os.[1] ]; [ os.[2] ] ] (w.Get<Order list list> "runs" |> List.map ids) "orders per customer"

    [<StepDefinition("the query description was built once")>]
    member _.BuiltOnce() = equal 1 builds.Value "builds"

    [<StepDefinition("every run sends the same SQL text with different parameter values")>]
    member _.SameSql() =
        let cmds = w.QueryCommands
        equal 2 cmds.Length "commands"
        equal 1 (cmds |> List.map (fun c -> c.Sql) |> List.distinct |> List.length) "distinct SQL texts"
        let values = cmds |> List.map (fun c -> c.Parameters |> List.map snd)
        check (values.[0] <> values.[1]) $"parameter values differ: %A{values}"

    [<StepDefinition("I read the Payments of the Orders I hold with a prepared query bound to a status")>]
    member _.PreparedChild() =
        task {
            let paymentsWith =
                Query.prepare2 (fun (status: Arg<PaymentStatus>) (ids: Arg<OrderId list>) ->
                    plasm {
                        for p in tableOf<Payment> do
                            where (p.Status = status.Value && List.contains p.OrderId ids.Value)
                    })

            try
                let! held = w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id })
                let held = ok held
                let! ps = w.Repo.All(paymentsWith Succeeded (held |> List.map (fun o -> o.Id)))
                let byOrder = ok ps |> List.groupBy (fun p -> p.OrderId) |> Map.ofList
                let perOrder = held |> List.map (fun o -> o.Id, byOrder |> Map.tryFind o.Id |> Option.defaultValue [])
                w.Set("childLoads", (Choice1Of2 perOrder: Choice<(OrderId * Payment list) list, exn>))
            with ex ->
                w.Set("childLoads", (Choice2Of2 ex: Choice<(OrderId * Payment list) list, exn>))
        }
        |> unit

    [<StepDefinition("each Order has only the Payments with that status")>]
    member _.OnlyStatus() =
        match w.Get<Choice<(OrderId * Payment list) list, exn>> "childLoads" with
        | Choice1Of2 perOrder ->
            check (not perOrder.IsEmpty) "no Orders were read"

            for id, ps in perOrder do
                check (not ps.IsEmpty) $"Order {id} has no Payments"
                check (ps |> List.forall (fun p -> p.Status = Succeeded)) $"Order {id}: %A{ps}"
        | Choice2Of2 ex -> failwithf "an exception was thrown: %s" ex.Message

    // Rule: Prepared queries compose like any other query

    [<StepDefinition("I extend it with a filter, an ordering and a limit for one Customer")>]
    member _.Extend() =
        let ada: CustomerId = w.Get "ada"

        let extended =
            plasm {
                for o in ordersOf ada do
                    where (o.Total > 15m)
                    orderByDescending o.Total
                    take 1
            }

        w.Set("extended", extended)

    [<StepDefinition("reading, counting and explaining the extended query all apply the extension")>]
    member _.ExtensionApplied() =
        task {
            let q: Query<Order> = w.Get "extended"
            let! rows = w.Repo.All q
            let! n = w.Repo.Count q
            let sql = (w.Repo.Explain q |> ok).Sql
            equal [ (orders ()).[1] ] (ok rows |> ids) "rows read"
            equal 1L (ok n |> int64) "count"
            let total =
                match w.Db.Kind with
                | PostgreSQL -> "\"total\" >"
                | SQLite -> "plasm_decimal_compare(\"o\".\"total\""

            check (sql.Contains "LIMIT" && sql.Contains "DESC" && sql.Contains total) $"explained SQL lacks the extension: {sql}"
        }
        |> unit

    [<StepDefinition("the prepared query on its own is unchanged")>]
    member _.PreparedUnchanged() =
        task {
            let ada: CustomerId = w.Get "ada"
            let! rows = w.Repo.All(ordersOf ada)
            let os = orders ()
            equal [ os.[0]; os.[1] ] (ok rows |> ids) "prepared query rows"
            let sql = (w.Repo.Explain(ordersOf ada) |> ok).Sql
            check (not (sql.Contains "LIMIT")) $"prepared query gained a limit: {sql}"
        }
        |> unit

    [<StepDefinition("I extend it with a filter and run it for two different Customers")>]
    member _.ExtendTwice() =
        task {
            let repo = repo ()
            let extended c = plasm { for o in ordersOf c do where (o.Total >= 20m) }
            w.MarkCommands()
            let! a = repo.All(extended (w.Get "ada"))
            let! b = repo.All(extended (w.Get "bob"))
            w.Set("runs", [ ok a; ok b ])
        }
        |> unit

    [<StepDefinition("each run returns only that Customer's Orders that satisfy the filter")>]
    member _.FilteredRuns() =
        let os = orders ()
        equal [ [ os.[1] ]; [ os.[2] ] ] (w.Get<Order list list> "runs" |> List.map ids) "orders per customer"

    [<StepDefinition("the two runs send one SQL text")>]
    member _.OneText() =
        let texts = w.QueryCommands |> List.map (fun c -> c.Sql) |> List.distinct
        equal 1 texts.Length $"distinct SQL texts: %A{texts}"

    // Scenario: Arguments are typed and always parameters

    [<StepDefinition("I prepare a query with an optional Customer argument and a list of statuses")>]
    member _.PrepareOptional() =
        let q =
            Query.prepare2 (fun (customer: Arg<CustomerId option>) (statuses: Arg<OrderStatus list>) ->
                plasm {
                    for o in tableOf<Order> do
                        where (o.CustomerId = customer.Value && List.contains o.Status statuses.Value)
                        orderBy o.Id
                })

        w.Set("optionalQuery", q)
        w.MarkCommands()

    [<StepDefinition("running it with Some Customer returns that Customer's matching Orders")>]
    member _.RunSome() =
        task {
            let q: CustomerId option -> OrderStatus list -> Query<Order> = w.Get "optionalQuery"
            let! r = w.Repo.All(q (Some(w.Get "ada")) [ Published ])
            let os = orders ()
            equal [ os.[0]; os.[1] ] (ids (ok r)) "Ada's published orders"
        }
        |> unit

    [<StepDefinition("running it with None returns the matching Orders without a Customer")>]
    member _.RunNone() =
        task {
            let q: CustomerId option -> OrderStatus list -> Query<Order> = w.Get "optionalQuery"
            let! r = w.Repo.All(q None [ Draft; Cancelled ])
            equal [ (orders ()).[3] ] (ids (ok r)) "draft without a customer"
            let! empty = w.Repo.All(q None [])
            equal [] (ok empty) "no statuses match nothing"
        }
        |> unit

    [<StepDefinition("both runs send the same SQL text")>]
    member _.SameText() =
        let texts = w.QueryCommands |> List.map (fun c -> c.Sql) |> List.distinct
        equal 1 texts.Length $"distinct SQL texts: %A{texts}"
        // The list one parameter, as the dialect sends a list: an array, or
        // JSON text (SQLite).
        let list =
            match w.Db.Kind with
            | PostgreSQL -> "= ANY("
            | SQLite -> "IN (SELECT value FROM json_each("

        check (texts.Head.Contains list) texts.Head
        w.Set("optionalText", texts.Head)

    [<StepDefinition("the optional argument is compared as a value or as absent, so an index on the column can serve it")>]
    member _.IndexableOptional() =
        let text = w.Get<string> "optionalText"
        // PostgreSQL sends the argument once, SQLite once for each use.
        let absent = if w.Db.Kind = PostgreSQL then "@p0" else "@p1"
        check (text.Contains $"\"o\".\"customer_id\" = @p0 OR (\"o\".\"customer_id\" IS NULL AND {absent} IS NULL)") text

    // Scenario: Paging arguments

    [<StepDefinition("I prepare a paged query whose page size and offset are arguments")>]
    member _.PreparePaged() =
        task {
            let page =
                Query.prepare2 (fun (size: Arg<int>) (offset: Arg<int>) ->
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.PlacedAt
                            skip offset.Value
                            take size.Value
                    })

            w.MarkCommands()
            let! pages = [ 0; 3 ] |> List.map (fun offset -> w.Repo.All(page 3 offset)) |> Threading.Tasks.Task.WhenAll
            w.Set("pages", pages |> Array.map (ok >> ids) |> List.ofArray)
        }
        |> unit

    [<StepDefinition("each page is bound from the arguments")>]
    member _.PagesBound() =
        equal [ 3; 1 ] (w.Get<OrderId list list> "pages" |> List.map List.length) "page sizes"
        check (w.QueryCommands |> List.forall (fun c -> c.Sql.EndsWith "LIMIT @p0 OFFSET @p1")) "LIMIT and OFFSET are parameters"

    [<StepDefinition("the pages together contain every Order exactly once")>]
    member _.PagesComplete() =
        equal (orders ()) (w.Get<OrderId list list> "pages" |> List.concat) "all orders exactly once, in order"

    // Scenario: Prepared queries keep relationship loads

    [<StepDefinition("I prepare a query for the Orders of a Customer, which own their OrderLines")>]
    member _.PrepareOwning() =
        task {
            let ordersOfCustomer =
                Query.prepare (fun (customer: Arg<CustomerId>) ->
                    plasm {
                        for o in tableOf<Order> do
                            where (o.CustomerId = Some customer.Value)
                            orderBy o.Id
                    })

            w.MarkCommands()
            let! a = w.Repo.All(ordersOfCustomer (w.Get "ada"))
            let! b = w.Repo.All(ordersOfCustomer (w.Get "bob"))
            w.Set("runs", [ ok a; ok b ])
        }
        |> unit

    [<StepDefinition("each run loads the OrderLines with batched commands")>]
    member _.LoadsLines() =
        let runs: Order list list = w.Get "runs"
        equal [ 2; 1 ] (runs |> List.map List.length) "orders per run"

        for o in List.concat runs do
            equal 1 o.Lines.Length $"lines of {o.Id}"

        equal 4 w.CommandsSinceMark.Length "one parent command and one lines command per run"

    // Scenario: Prepared queries can be inspected and counted

    [<StepDefinition("its plan can be inspected for an argument without database access")>]
    member _.InspectPrepared() =
        w.MarkCommands()
        let plan = w.Repo.Explain(ordersOf (w.Get "ada")) |> ok
        check (plan.Sql.Contains "WHERE \"o\".\"customer_id\" = @p0") plan.Sql
        equal 0 w.CommandsSinceMark.Length "commands"

    [<StepDefinition("it can be counted for an argument")>]
    member _.CountPrepared() =
        task {
            let! n = w.Repo.Count(ordersOf (w.Get "ada"))
            equal 2L (ok n) "Ada's orders"
        }
        |> unit

    // Rule: Argument misuse is explicit

    [<StepDefinition("a prepared query reads an argument while it is being built")>]
    member _.ReadsEagerly() =
        prepareFails
            (fun () ->
                Query.prepare (fun (minimum: Arg<decimal>) ->
                    let doubled = minimum.Value * 2m

                    plasm {
                        for o in tableOf<Order> do
                            where (o.Total > doubled)
                    })
                |> ignore)
            "while the query was being built"

    [<StepDefinition("a prepared query orders by an argument")>]
    member _.OrdersByArg() =
        prepareFails
            (fun () ->
                Query.prepare (fun (factor: Arg<decimal>) ->
                    plasm {
                        for o in tableOf<Order> do
                            orderBy (o.Total * factor.Value)
                    })
                |> ignore)
            "cannot order by an argument"

    [<StepDefinition("preparation fails with an explanation")>]
    member _.PrepareFailed() =
        let message: string = w.Get "prepareError"
        check (message.Contains(w.Get<string> "expectedPrepareError")) $"message: '{message}'"
