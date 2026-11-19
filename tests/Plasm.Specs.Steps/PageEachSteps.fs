namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: A program builds a query at run time from declared fields,
/// "A program pages each value of a field in one command".
[<Binding>]
type PageEachSteps(w: World) =

    let payments (orders: OrderId list) =
        Query.from tableOf<Payment> |> BuildQuery.where <@ fun p -> List.contains p.OrderId orders @>

    let newest (q: Query<Payment>) = q |> BuildQuery.orderByDescending <@ fun p -> p.CreatedAt @>

    let total = ReadField.value "total" <@ fun (o: Order) -> o.Total @>
    let lines = ReadField.via "lines" <@ fun (o: Order) -> o.Lines @> List.length

    member private _.Ids = w.Get<OrderId * OrderId> "orders"

    member private this.ReadPayments(q: Query<Payment>) =
        task {
            w.MarkCommands()
            let! rows = w.Repo.All q
            w.Set("payments", ok rows)
        }

    [<StepDefinition("an Order with Payments of 10, 20 and 30, made in that order, and another with one Payment of 5")>]
    member _.Payments() =
        task {
            let! first = Seed.order w.Db None "published" 1m (Seed.at 0)
            let! second = Seed.order w.Db None "published" 2m (Seed.at 1)

            for i, amount in List.indexed [ 10m; 20m; 30m ] do
                let! _ = Seed.payment w.Db first amount "succeeded" (Seed.at (10 + i))
                ()

            let! _ = Seed.payment w.Db second 5m "succeeded" (Seed.at 20)
            w.Set("orders", (first, second))
        }
        |> unit

    [<StepDefinition("I read the Payments of both Orders, newest first, two of each Order")>]
    member this.TwoEach() =
        let a, b = this.Ids
        this.ReadPayments(payments [ a; b ] |> newest |> BuildQuery.pageEach <@ fun p -> p.OrderId @> 0 2) |> unit

    [<StepDefinition("I read the Payments of both Orders, newest first, skipping one and taking two of each Order")>]
    member this.SkipEach() =
        let a, b = this.Ids
        this.ReadPayments(payments [ a; b ] |> newest |> BuildQuery.pageEach <@ fun p -> p.OrderId @> 1 2) |> unit

    [<StepDefinition("I read the Payments over 15 of both Orders, newest first, one of each Order")>]
    member this.WhereEach() =
        let a, b = this.Ids

        this.ReadPayments(
            payments [ a; b ]
            |> BuildQuery.where <@ fun p -> p.Amount > 15m @>
            |> newest
            |> BuildQuery.pageEach <@ fun p -> p.OrderId @> 0 1
        )
        |> unit

    [<StepDefinition("the Payments are {string}")>]
    member this.PaymentsAre(expected: string) =
        let a, b = this.Ids
        let rows = w.Get<Payment list> "payments"
        let of' o = String.Join(", ", rows |> List.filter (fun p -> p.OrderId = o) |> List.map (fun p -> p.Amount.ToString("0")))
        equal expected $"first: {of' a}; second: {of' b}" "payments"

    [<StepDefinition("the read sends one command, numbering the rows per Order")>]
    member _.Numbered() =
        match w.CommandsSinceMark |> List.map (fun c -> c.Sql) with
        | [ sql ] -> check (sql.Contains "ROW_NUMBER() OVER (PARTITION BY \"p\".\"order_id\" ORDER BY \"p\".\"created_at\" DESC" && sql.Contains "\"ranked\".\"__rn\"") sql
        | sent -> failwithf "commands sent: %A" sent

    [<StepDefinition("I build the Payments two of each Order, without an order")>]
    member _.Unordered() = w.Set("built", Query.from tableOf<Payment> |> BuildQuery.pageEach <@ fun p -> p.OrderId @> 0 2)

    [<StepDefinition("I build the Payments newest first, taking 10, and then two of each Order")>]
    member _.Twice() =
        w.Set("built", Query.from tableOf<Payment> |> newest |> BuildQuery.take 10 |> BuildQuery.pageEach <@ fun p -> p.OrderId @> 0 2)

    [<StepDefinition("Customers \"Ada\" and \"Grace\", Ada with Orders of 10, 20 and 30, and Grace with one of 40, each Order with one line")>]
    member _.CustomersOrders() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! grace = Seed.customer w.Db "Grace" "grace@example.com"
            let! p = Seed.product w.Db "P"

            for i, (c, t) in List.indexed [ ada, 10m; ada, 20m; ada, 30m; grace, 40m ] do
                let! o = Seed.order w.Db (Some c) "published" t (Seed.at i)
                let! _ = Seed.line w.Db o p 1 1m
                ()
        }
        |> unit

    [<StepDefinition("I read the totals and lines of each Customer's newest Order, chosen")>]
    member _.NewestOfEach() =
        task {
            w.MarkCommands()

            let query =
                Query.from tableOf<Order>
                |> BuildQuery.orderByDescending <@ fun o -> o.PlacedAt @>
                |> BuildQuery.pageEach <@ fun o -> o.CustomerId @> 0 1
                |> BuildQuery.select [ total; lines ]

            let! rows = w.Repo.All query
            w.Set("selected", ok rows)
        }
        |> unit

    [<StepDefinition("the totals are {string}, each with its line")>]
    member _.Totals(expected: string) =
        let rows = w.Get<Selected<Order> list> "selected"
        equal expected (String.Join(", ", rows |> List.map (fun r -> r.Get<decimal> total) |> List.sort |> List.map (fun t -> t.ToString("0")))) "totals"
        equal [ 1; 1 ] (rows |> List.map (fun r -> r.Get<int> lines)) "lines"

    [<StepDefinition("the read sends 2 commands: the Orders, numbered per Customer, and the lines of the two Orders kept")>]
    member _.OrdersAndLines() =
        match w.CommandsSinceMark with
        | [ orders; ls ] ->
            check (orders.Sql.Contains "PARTITION BY \"o\".\"customer_id\"") orders.Sql
            check (ls.Sql.Contains "\"order_lines\"") ls.Sql
            // One line each: the lines of the two Orders kept, no more.
            equal 2 ls.Rows "lines read"
        | sent -> failwithf "commands sent: %A" (sent |> List.map (fun c -> c.Sql))

    [<StepDefinition("the built Payments query is refused with {string}")>]
    member _.PaymentsRefused(message: string) =
        match w.Repo.Explain(w.Get<Query<Payment>> "built") with
        | Ok _ -> failwith "the built query was translated"
        | Error e -> check ((string e).Contains message) $"refusal: expected it to contain {message} but was {e}"
