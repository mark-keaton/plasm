/// Queries for loads.feature checked by `repo.CheckQueries`, and the
/// relationship written once as a reflected function.
namespace Plasm.Specs.LoadQueries

open Plasm
open Plasm.TestDomain.Domain

module Relations =
    /// An Order's succeeded Payments: more than the tie, so written once and
    /// built on it; bound with let! and used in `exists`.
    [<ReflectedDefinition>]
    let succeededPaymentsOf (o: Order) =
        plasm {
            for p in referring<Payment> o do
            where (p.Status = Succeeded)
        }

open Relations

module LoadsGood =
    let withPayments =
        plasm {
            for o in tableOf<Order> do
            orderBy o.Total

            let! payments =
                plasm {
                    for p in referring<Payment> o do
                    orderBy p.CreatedAt
                }

            return {| Order = o; Payments = payments |}
        }

    let withPaymentsOfAtLeast =
        Query.prepare (fun (least: Arg<decimal>) ->
            plasm {
                for o in tableOf<Order> do
                orderBy o.Total

                let! payments =
                    plasm {
                        for p in referring<Payment> o do
                        where (p.Amount >= least.Value)
                        orderBy p.CreatedAt
                    }

                return {| Order = o; Payments = payments |}
            })

    /// Two loads, the second's records with loads of their own.
    let withPaymentsAndSameCustomer =
        plasm {
            for o in tableOf<Order> do
            orderBy o.Total
            let! payments = referring<Payment> o

            and! others =
                plasm {
                    for x in tableOf<Order> do
                    where (x.CustomerId = o.CustomerId)
                    orderBy x.Total
                    let! payments = referring<Payment> x
                    return {| Order = x; Payments = payments |}
                }

            return {| Order = o; Payments = payments; Others = others |}
        }

/// Not reflected: its body cannot be read, so it cannot be a load.
module Opaque =
    let paymentsOf (o: Order) = referring<Payment> o

namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Specs.LoadQueries
open Plasm.Specs.LoadQueries.Relations

/// Feature: A query binds what it reads for each record with let!, and builds its row with return
[<Binding>]
type LoadsSteps(w: World) =

    let total (d: decimal) = d.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
    let amounts (xs: decimal list) = xs |> List.map total |> String.concat ", "

    let payment (p: Payment) = $"{total p.Amount} {p.Status}"
    let payments (ps: Payment list) = ps |> List.map payment |> String.concat ", "

    /// Reads `q` and keeps each row as its Order and the one list it bound.
    let read (q: Projection<'R>) (row: 'R -> Order * obj list) =
        task {
            w.MarkCommands()
            let! r = w.Repo.All q
            let rows: Result<(Order * obj list) list, DbError> = r |> Result.map (List.map row)
            w.Set("loads", rows)
        }
        |> unit

    /// Reads `q` expecting a refusal.
    let refuse (q: Projection<'R>) =
        task {
            let! r = w.Repo.All q
            w.Set("loadsRefusal", r |> Result.map ignore)
        }
        |> unit

    let rows () =
        match w.Get<Result<(Order * obj list) list, DbError>> "loads" with
        | Ok r -> r
        | Error e -> failwithf "the read failed: %A" e

    let compiled (code: string) =
        task {
            let! r = CompileCheck.check code
            w.Set("compiled", r)
        }
        |> unit

    let orderOf (t: decimal) =
        (w.Get<Map<decimal, OrderId>> "loadsOrders").[t]

    // -- Background -------------------------------------------------------------

    [<StepDefinition("^the Customers \"Ada\" and \"Bob\"$")>]
    member _.Customers() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            w.Set("loadsCustomers", (ada, bob))
        }
        |> unit

    [<StepDefinition("Ada's Orders of 10 and 50, Bob's Order of 200, and an Order of 5 with no Customer")>]
    member _.Orders() =
        task {
            let ada, bob = w.Get<CustomerId * CustomerId> "loadsCustomers"
            let mutable orders = Map.empty

            for customer, t in [ Some ada, 10m; Some ada, 50m; Some bob, 200m; None, 5m ] do
                let! id = Seed.order w.Db customer "published" t (Seed.at 0)
                orders <- orders.Add(t, id)

            w.Set("loadsOrders", orders)
        }
        |> unit

    [<StepDefinition("the Order of 10 has one line")>]
    member _.Line() =
        task {
            let! pen = Seed.product w.Db "PEN"
            let! _ = Seed.line w.Db (orderOf 10m) pen 1 10m
            ()
        }
        |> unit

    [<StepDefinition("the Payments, by order and minute:")>]
    member _.Payments(table: DataTable) =
        task {
            for row in table.Rows do
                let! _ =
                    Seed.payment w.Db (orderOf (decimal row.["order"])) (decimal row.["amount"]) (row.["status"].ToLowerInvariant()) (Seed.at (int row.["minute"]))

                ()
        }
        |> unit

    // -- One load ----------------------------------------------------------------

    [<StepDefinition("I read the Orders by total, binding their Payments by minute")>]
    member _.WithPayments() =
        read LoadsGood.withPayments (fun r -> r.Order, List.map box r.Payments)

    [<StepDefinition("I read the Orders by total, each row with the Order's total, whether it is large, and its Payments")>]
    member _.Columns() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total
                        let! payments = referring<Payment> o
                        return {| Order = o; Total = o.Total; Large = o.Large; Payments = payments |}
                    }
                )

            match r with
            | Ok rows -> w.Set("loadsColumns", rows |> List.map (fun x -> x.Order.Total, x.Total, x.Large))
            | Error e -> failwithf "the read failed: %A" e
        }
        |> unit

    [<StepDefinition("each row's Total is its Order's total, and Large says whether it is 50 or more")>]
    member _.ColumnsAre() =
        for orderTotal, t, large in w.Get<(decimal * decimal * bool) list> "loadsColumns" do
            equal orderTotal t "Total"
            equal (orderTotal >= 50m) large $"Large of the Order of {total orderTotal}"

    [<StepDefinition("I read the Orders by total, binding what succeededPaymentsOf finds for each")>]
    member _.Succeeded() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total
                let! succeeded = plasm { for p in succeededPaymentsOf o do orderBy p.CreatedAt }
                return {| Order = o; Succeeded = succeeded |}
            })
            (fun r -> r.Order, List.map box r.Succeeded)

    [<StepDefinition("I read the Orders that succeededPaymentsOf finds something for, in exists")>]
    member _.Exists() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do where (exists (succeededPaymentsOf o)); orderBy o.Total })
            w.Set("loadsExists", ok r)
        }
        |> unit

    [<StepDefinition("the Orders found are those of 10 and 50")>]
    member _.Found() =
        equal [ "10"; "50" ] (w.Get<Order list> "loadsExists" |> List.map (fun o -> total o.Total)) "orders"

    [<StepDefinition("a transaction reads the Orders by total, binding their Payments by minute")>]
    member _.InTransaction() =
        task {
            let! r = w.Repo.Transact(Tx.all LoadsGood.withPayments: Tx<_, unit>)

            let rows: Result<(Order * obj list) list, DbError> =
                match r with
                | Ok rows -> Ok(rows |> List.map (fun x -> x.Order, List.map box x.Payments))
                | Error e -> failwithf "the transaction failed: %A" e

            w.Set("loads", rows)
        }
        |> unit

    // -- Per record ----------------------------------------------------------------

    [<StepDefinition("I read the Orders by total, binding each one's newest Payment")>]
    member _.Newest() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total

                let! newest =
                    plasm {
                        for p in referring<Payment> o do
                        orderByDescending p.CreatedAt
                        take 1
                    }

                return {| Order = o; Newest = newest |}
            })
            (fun r -> r.Order, List.map box r.Newest)

    [<StepDefinition("I read the Orders binding two of their Payments in no order")>]
    member _.Unordered() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = plasm { for p in referring<Payment> o do take 2 }
                return {| Order = o; Payments = payments |}
            }
        )

    [<StepDefinition("I read the Orders by total, binding the Orders of the same Customer")>]
    member _.SameCustomer() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total

                let! others =
                    plasm {
                        for x in tableOf<Order> do
                        where (x.CustomerId = o.CustomerId)
                        orderBy x.Total
                    }

                return {| Order = o; Others = others |}
            })
            (fun r -> r.Order, List.map box r.Others)

    [<StepDefinition("I read the Orders binding the Payments made after the Order was placed")>]
    member _.AfterPlaced() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.CreatedAt > o.PlacedAt) }
                return {| Order = o; Payments = payments |}
            }
        )

    // -- What a load selects -----------------------------------------------------

    [<StepDefinition("I read the Orders by total, binding the amounts of their Payments by minute")>]
    member _.Amounts() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total
                let! amounts = plasm { for p in referring<Payment> o do orderBy p.CreatedAt; select p.Amount }
                return {| Order = o; Amounts = amounts |}
            })
            (fun r -> r.Order, List.map box r.Amounts)

    [<StepDefinition("I read the Orders by total, binding the amount of each one's newest Payment")>]
    member _.NewestAmount() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total
                let! newest = plasm { for p in referring<Payment> o do orderByDescending p.CreatedAt; take 1; select p.Amount }
                return {| Order = o; Newest = newest |}
            })
            (fun r -> r.Order, List.map box r.Newest)

    [<StepDefinition("I read the Orders by total, binding the distinct statuses of their Payments")>]
    member _.DistinctStatuses() =
        read
            (plasm {
                for o in tableOf<Order> do
                orderBy o.Total
                let! statuses = plasm { for p in referring<Payment> o do orderBy p.Status; selectDistinct p.Status }
                return {| Order = o; Statuses = statuses |}
            })
            (fun r -> r.Order, List.map box r.Statuses)

    [<StepDefinition("I read the Orders binding two distinct statuses of their Payments")>]
    member _.TwoDistinct() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! statuses = plasm { for p in referring<Payment> o do orderBy p.Status; take 2; selectDistinct p.Status }
                return {| Order = o; Statuses = statuses |}
            }
        )

    [<StepDefinition("the Payments command reads only the amount and the Order it belongs to")>]
    member _.OnlyAmount() =
        let payments = w.CommandsSinceMark |> List.last

        check
            (payments.Sql.Contains "\"amount\""
             && payments.Sql.Contains "\"order_id\""
             && not (payments.Sql.Contains "\"status\"")
             && not (payments.Sql.Contains "\"created_at\" AS \""))
            payments.Sql

    // -- Several loads, and loads of loads -----------------------------------------

    [<StepDefinition("I read the Orders by total, binding their Payments by minute and the Orders of the same Customer")>]
    member _.TwoLoads() =
        task {
            let q =
                plasm {
                    for o in tableOf<Order> do
                    orderBy o.Total
                    let! payments = plasm { for p in referring<Payment> o do orderBy p.CreatedAt }
                    and! others = plasm { for x in tableOf<Order> do where (x.CustomerId = o.CustomerId); orderBy x.Total }
                    return {| Order = o; Payments = payments; Others = others |}
                }

            w.MarkCommands()
            let! r = w.Repo.All q
            w.Set("loadsTwo", ok r |> List.map (fun x -> x.Order, x.Payments, x.Others))
            w.Set("loadsTwoPlan", ok (w.Repo.Explain q))
        }
        |> unit

    [<StepDefinition("the Orders, their Payments and the totals of the Orders they bind are:")>]
    member _.TwoLoadsAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["payments"].Trim(), r.["others"].Trim() ]

        let actual =
            [ for o, ps, xs in w.Get<(Order * Payment list * Order list) list> "loadsTwo" ->
                  total o.Total, payments ps, xs |> List.map (fun x -> total x.Total) |> String.concat ", " ]

        equal expected actual "orders, payments and the orders they bind"

    [<StepDefinition("5 commands are sent: the Orders, their lines, the Payments, and the Orders of the same Customer with their lines")>]
    member _.FlatCommands() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)

        let table (q: string) =
            [ "order_lines"; "payments"; "orders" ] |> List.find (fun t -> q.Contains $"\"{t}\"")

        equal [ "orders"; "order_lines"; "payments"; "orders"; "order_lines" ] (sql |> List.map table) $"commands: %A{sql}"

    [<StepDefinition("the plan lists the loads payments and others")>]
    member _.PlanTwo() =
        let plan = w.Get<QueryPlan> "loadsTwoPlan"
        let bound = plan.Loads |> List.filter (fun l -> l.Relationship = "binds") |> List.map (fun l -> l.Path, l.Target)
        equal [ "payments", "Payment"; "others", "Order" ] bound $"loads: %A{plan.Loads}"

    [<StepDefinition("I read the Orders by total, binding the amounts of their two newest Payments and of their succeeded Payments")>]
    member _.SameType() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total
                        let! newest = plasm { for p in referring<Payment> o do orderByDescending p.CreatedAt; take 2; select p.Amount }
                        and! succeeded = plasm { for p in referring<Payment> o do where (p.Status = Succeeded); orderBy p.CreatedAt; select p.Amount }
                        return {| Order = o; Newest = newest; Succeeded = succeeded |}
                    }
                )

            w.Set("loadsSameType", ok r |> List.map (fun x -> total x.Order.Total, amounts x.Newest, amounts x.Succeeded))
        }
        |> unit

    [<StepDefinition("the Orders and their two lists of amounts are:")>]
    member _.SameTypeAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["newest"].Trim(), r.["succeeded"].Trim() ]
        equal expected (w.Get<(string * string * string) list> "loadsSameType") "orders and their amounts"

    member private _.Nested(newestOnly: bool) =
        task {
            let q =
                if newestOnly then
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total

                        let! others =
                            plasm {
                                for x in tableOf<Order> do
                                where (x.CustomerId = o.CustomerId)
                                orderBy x.Total
                                let! payments = plasm { for p in referring<Payment> x do orderByDescending p.CreatedAt; take 1 }
                                return {| Order = x; Payments = payments |}
                            }

                        return {| Order = o; Others = others |}
                    }
                else
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total

                        let! others =
                            plasm {
                                for x in tableOf<Order> do
                                where (x.CustomerId = o.CustomerId)
                                orderBy x.Total
                                let! payments = plasm { for p in referring<Payment> x do orderBy p.CreatedAt }
                                return {| Order = x; Payments = payments |}
                            }

                        return {| Order = o; Others = others |}
                    }

            w.MarkCommands()
            let! r = w.Repo.All q

            w.Set(
                "loadsNested",
                ok r
                |> List.map (fun x ->
                    total x.Order.Total,
                    x.Others
                    |> List.map (fun y -> $"{total y.Order.Total} ({payments y.Payments})")
                    |> String.concat "; ")
            )
        }
        |> unit

    [<StepDefinition("I read the Orders by total, binding the Orders of the same Customer, each binding its Payments by minute")>]
    member this.NestedPayments() = this.Nested false

    [<StepDefinition("I read the Orders by total, binding the Orders of the same Customer, each binding its newest Payment")>]
    member this.NestedNewest() = this.Nested true

    [<StepDefinition("the Orders and the Orders they bind, with their Payments, are:")>]
    member _.NestedAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["others"].Trim() ]
        equal expected (w.Get<(string * string) list> "loadsNested") "orders, the orders they bind and their payments"

    [<StepDefinition("5 commands are sent: the Orders and their lines, the Orders of the same Customer and their lines, and the Payments")>]
    member _.FiveCommands() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 5 sql.Length $"commands: %A{sql}"

        let table (q: string) =
            [ "order_lines"; "payments"; "orders" ] |> List.find (fun t -> q.Contains $"\"{t}\"")

        equal [ "orders"; "order_lines"; "orders"; "order_lines"; "payments" ] (sql |> List.map table) $"commands: %A{sql}"

    [<StepDefinition("I read the Customers by name, binding their Orders by total, each binding its Payments by minute")>]
    member _.CustomersOrdersPayments() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for c in tableOf<Customer> do
                        orderBy c.Name

                        let! orders =
                            plasm {
                                for o in referring<Order> c do
                                orderBy o.Total
                                let! payments = plasm { for p in referring<Payment> o do orderBy p.CreatedAt }
                                return {| Order = o; Payments = payments |}
                            }

                        return {| Customer = c; Orders = orders |}
                    }
                )

            w.Set(
                "loadsCustomers3",
                ok r
                |> List.map (fun x ->
                    x.Customer.Name,
                    x.Orders |> List.map (fun y -> $"{total y.Order.Total} ({payments y.Payments})") |> String.concat "; ")
            )
        }
        |> unit

    [<StepDefinition("the Customers and their Orders, with their Payments, are:")>]
    member _.CustomersAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["customer"], r.["orders"].Trim() ]
        equal expected (w.Get<(string * string) list> "loadsCustomers3") "customers, their orders and their payments"

    [<StepDefinition("I read the Orders by total, binding seventeen lists: their Payments of at least 1 up to at least 17")>]
    member _.Seventeen() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total
                        let! a1 = plasm { for p in referring<Payment> o do where (p.Amount >= 1m) }
                        and! a2 = plasm { for p in referring<Payment> o do where (p.Amount >= 2m) }
                        and! a3 = plasm { for p in referring<Payment> o do where (p.Amount >= 3m) }
                        and! a4 = plasm { for p in referring<Payment> o do where (p.Amount >= 4m) }
                        and! a5 = plasm { for p in referring<Payment> o do where (p.Amount >= 5m) }
                        and! a6 = plasm { for p in referring<Payment> o do where (p.Amount >= 6m) }
                        and! a7 = plasm { for p in referring<Payment> o do where (p.Amount >= 7m) }
                        and! a8 = plasm { for p in referring<Payment> o do where (p.Amount >= 8m) }
                        and! a9 = plasm { for p in referring<Payment> o do where (p.Amount >= 9m) }
                        and! a10 = plasm { for p in referring<Payment> o do where (p.Amount >= 10m) }
                        and! a11 = plasm { for p in referring<Payment> o do where (p.Amount >= 11m) }
                        and! a12 = plasm { for p in referring<Payment> o do where (p.Amount >= 12m) }
                        and! a13 = plasm { for p in referring<Payment> o do where (p.Amount >= 13m) }
                        and! a14 = plasm { for p in referring<Payment> o do where (p.Amount >= 14m) }
                        and! a15 = plasm { for p in referring<Payment> o do where (p.Amount >= 15m) }
                        and! a16 = plasm { for p in referring<Payment> o do where (p.Amount >= 16m) }
                        and! a17 = plasm { for p in referring<Payment> o do where (p.Amount >= 17m) }

                        return
                            {| Order = o
                               A1 = a1
                               A2 = a2
                               A3 = a3
                               A4 = a4
                               A5 = a5
                               A6 = a6
                               A7 = a7
                               A8 = a8
                               A9 = a9
                               A10 = a10
                               A11 = a11
                               A12 = a12
                               A13 = a13
                               A14 = a14
                               A15 = a15
                               A16 = a16
                               A17 = a17 |}
                    }
                )

            let counts =
                ok r
                |> List.map (fun x ->
                    total x.Order.Total,
                    [ x.A1; x.A2; x.A3; x.A4; x.A5; x.A6; x.A7; x.A8; x.A9; x.A10; x.A11; x.A12; x.A13; x.A14; x.A15; x.A16; x.A17 ]
                    |> List.map (List.length >> string)
                    |> String.concat " ")

            w.Set("loadsSeventeen", counts)
        }
        |> unit

    [<StepDefinition("each Order comes with seventeen lists, the n-th holding its Payments of at least n:")>]
    member _.SeventeenAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["counts"].Trim() ]
        equal expected (w.Get<(string * string) list> "loadsSeventeen") "orders and the sizes of their seventeen lists"

    // -- The one record a tie can find -------------------------------------------

    [<StepDefinition("I read the Orders by total, binding the Customer each refers to")>]
    member _.OneCustomer() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total
                        let! customer = referredBy<Customer> o
                        return {| Order = o; Customer = customer |}
                    }
                )

            let shown: Result<(string * string option) list, DbError> =
                r |> Result.map (List.map (fun x -> total x.Order.Total, x.Customer |> Option.map (fun c -> c.Name)))

            w.Set("includingOne", shown)
        }
        |> unit

    [<StepDefinition("the Orders and their Customers are:")>]
    member _.OneCustomerAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], (match r.["customer"] with "none" -> None | n -> Some n) ]
        equal expected (ok (w.Get<Result<(string * string option) list, DbError>> "includingOne")) "orders and their customers"

    [<StepDefinition("3 commands are sent: the Orders, their lines, and the Customers")>]
    member _.OneCommands() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 3 sql.Length $"commands: %A{sql}"
        check (sql.[2].Contains "\"customers\"" && sql.[2].Contains "ANY(") $"commands: %A{sql}"

    [<StepDefinition("I read the Orders by total, binding the name of the Customer each refers to and their Payments")>]
    member _.OneNameAndPayments() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                        orderBy o.Total
                        let! name = plasm { for c in referredBy<Customer> o do select c.Name }
                        and! payments = referring<Payment> o
                        return {| Order = o; Name = name; Payments = payments |}
                    }
                )

            w.Set("loadsOneName", ok r |> List.map (fun x -> total x.Order.Total, defaultArg x.Name "none", string x.Payments.Length))
        }
        |> unit

    [<StepDefinition("the Orders, their Customers' names and how many Payments each has are:")>]
    member _.OneNameAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["customer"], r.["payments"] ]
        equal expected (w.Get<(string * string * string) list> "loadsOneName") "orders, customers' names and payments"

    [<StepDefinition("I compile code that passes the Orders with their Customer and their Payments to a function of a record with a Customer option")>]
    member _.CompileOne() =
        compiled
            """
type Row = { Order: Order; Customer: Customer option; Payments: Payment list }
let show (rows: Row list) = rows.Length
let q =
    plasm {
        for o in tableOf<Order> do
        let! customer = referredBy<Customer> o
        and! payments = referring<Payment> o
        return { Order = o; Customer = customer; Payments = payments }
    }
let shown (repo: Repo) = task { let! r = repo.All q in return r |> Result.map show }
"""

    // -- What return builds --------------------------------------------------------

    [<StepDefinition("I read the Orders binding the amounts of their newest and of their succeeded Payments, returned crossed")>]
    member _.Crossed() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! newest = plasm { for p in referring<Payment> o do orderByDescending p.CreatedAt; take 2; select p.Amount }
                and! succeeded = plasm { for p in referring<Payment> o do where (p.Status = Succeeded); select p.Amount }
                return {| Order = o; Newest = succeeded; Succeeded = newest |}
            }
        )

    [<StepDefinition("I read the Orders binding their Payments, returning how many there are")>]
    member _.Counted() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = referring<Payment> o
                return {| Order = o; Count = List.length payments |}
            }
        )

    [<StepDefinition("I read the Orders with their Payments bound by let, not let!")>]
    member _.PlainLet() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let payments = referring<Payment> o
                let! succeeded = succeededPaymentsOf o
                return {| Order = o; Payments = payments; Succeeded = succeeded |}
            }
        )

    [<StepDefinition("I read the Orders binding their Payments and their succeeded Payments' amounts, returning only the Payments")>]
    member _.NotReturned() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = referring<Payment> o
                and! succeeded = plasm { for p in referring<Payment> o do where (p.Status = Succeeded); select p.Amount }
                return {| Order = o; Payments = payments |}
            }
        )

    [<StepDefinition("I read the Orders binding their Payments, returning them in two fields")>]
    member _.Twice() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = referring<Payment> o
                return {| Order = o; Payments = payments; payments = payments |}
            }
        )

    [<StepDefinition("I read the Orders binding their Payments, returning a tuple")>]
    member _.Tuple() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = referring<Payment> o
                return (o, payments)
            }
        )

    // -- Checked and prepared ------------------------------------------------------

    [<StepDefinition("I read the Orders binding their Payments of at least 25, through a prepared query")>]
    member _.Prepared() =
        read (LoadsGood.withPaymentsOfAtLeast 25m) (fun r -> r.Order, List.map box r.Payments)

    [<StepDefinition("I run the Orders with their Payments twice through one repository")>]
    member _.RunTwice() =
        task {
            let repo = w.Db.RepoWith()
            let! _ = repo.All LoadsGood.withPayments
            let! _ = repo.All LoadsGood.withPayments
            w.Set("plans", repo.PlansMade)
        }
        |> unit

    [<StepDefinition("I check the queries of a module whose queries bind loads that translate")>]
    member _.Check() =
        w.Set("problems", w.Repo.CheckQueries(typeof<LoadsSteps>.Assembly, "Plasm.Specs.LoadQueries.LoadsGood"))

    // -- Refused ---------------------------------------------------------------------

    [<StepDefinition("I read the Orders binding their OrderLines")>]
    member _.Lines() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! lines = plasm { for l in tableOf<OrderLine> do where (l.OrderId = o.Id) }
                return {| Order = o; Lines = lines |}
            }
        )

    [<StepDefinition("I read the Orders joined with their Customers, binding their Payments")>]
    member _.Joined() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                let! payments = referring<Payment> o
                return {| Order = o; Payments = payments |}
            }
        )

    [<StepDefinition("I read the Orders binding their Payments through a function that is not reflected")>]
    member _.NotReflected() =
        refuse (
            plasm {
                for o in tableOf<Order> do
                let! payments = Opaque.paymentsOf o
                return {| Order = o; Payments = payments |}
            }
        )

    [<StepDefinition("^the read is refused, saying \"(.*)\"$")>]
    member _.Refused(text: string) =
        match w.Get<Result<unit, DbError>> "loadsRefusal" with
        | Error(DbError.Translation e) -> check ((string e).Contains text) $"%s{string e}"
        | other -> failwithf "expected the read to be refused, got %A" other

    [<StepDefinition("the read is refused, naming the condition that refers to the Order")>]
    member _.NamesCondition() =
        match w.Get<Result<unit, DbError>> "loadsRefusal" with
        | Error(DbError.Translation e) -> check ((string e).Contains "PlacedAt") (string e)
        | other -> failwithf "expected the read to be refused, got %A" other

    // -- Results ---------------------------------------------------------------------

    [<StepDefinition("the Orders and their Payments are:")>]
    member _.Are(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["payments"].Trim() ]
        let actual = [ for o, ps in rows () -> total o.Total, ps |> List.map (unbox<Payment> >> payment) |> String.concat ", " ]
        equal expected actual "orders and payments"

    [<StepDefinition("the Orders and the totals of the Orders they bind are:")>]
    member _.Others(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["others"].Trim() ]
        let actual = [ for o, xs in rows () -> total o.Total, xs |> List.map (fun x -> total (x :?> Order).Total) |> String.concat ", " ]
        equal expected actual "orders and the orders they bind"

    [<StepDefinition("the Orders and the values they bind are:")>]
    member _.Values(table: DataTable) =
        let text (v: obj) =
            match v with
            | :? decimal as d -> total d
            | v -> string v

        let expected = [ for r in table.Rows -> r.["order"], r.["values"].Trim() ]
        let actual = [ for o, xs in rows () -> total o.Total, xs |> List.map text |> String.concat ", " ]
        equal expected actual "orders and their values"

    [<StepDefinition("the Order of 10 still has its line")>]
    member _.StillHasLine() =
        let o = rows () |> List.map fst |> List.find (fun o -> o.Total = 10m)
        equal 1 o.Lines.Length "lines of the Order of 10"

    [<StepDefinition("the Order of 50 comes with 3 Payments")>]
    member _.FiftyHasThree() =
        let _, ps = rows () |> List.find (fun (o, _) -> o.Total = 50m)
        equal 3 ps.Length "payments of the Order of 50"

    [<StepDefinition("the Order of 50 comes with the Payments (.*)")>]
    member _.FiftyWith(text: string) =
        let _, ps = rows () |> List.find (fun (o, _) -> o.Total = 50m)
        equal text (ps |> List.map (unbox<Payment> >> payment) |> String.concat ", ") "payments of the Order of 50"

    [<StepDefinition("3 commands are sent: the Orders, their lines, and the Payments")>]
    member _.ThreeCommands() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 3 sql.Length $"commands: %A{sql}"
        check (sql.[0].Contains "\"orders\"" && sql.[1].Contains "\"order_lines\"" && sql.[2].Contains "\"payments\"") $"commands: %A{sql}"

    [<StepDefinition("the Payments command takes the Orders' ids as one parameter")>]
    member _.OneParameter() =
        let payments = w.CommandsSinceMark |> List.last
        check (payments.Sql.Contains "ANY(") payments.Sql
        equal 1 payments.Parameters.Length $"parameters of {payments.Sql}"

    [<StepDefinition("the plan lists the Payments as one more load, named payments")>]
    member _.Plan() =
        let plan = ok (w.Repo.Explain LoadsGood.withPayments)
        check (plan.Loads |> List.exists (fun l -> l.Path = "payments" && l.Target = "Payment" && l.Sql.Contains "ANY(")) $"loads: %A{plan.Loads}"

    // -- Compiling -------------------------------------------------------------------

    [<StepDefinition("I compile code that passes the Orders read with their Payments to a function of a record of them")>]
    member _.CompileWith() =
        compiled
            """
type Row = { Order: Order; Payments: Payment list }
let show (rows: Row list) = rows.Length
let q =
    plasm {
        for o in tableOf<Order> do
        let! payments = referring<Payment> o
        return { Order = o; Payments = payments }
    }
let shown (repo: Repo) = task { let! r = repo.All q in return r |> Result.map show }
"""

    [<StepDefinition("I compile code where a query filters after binding its Payments")>]
    member _.WhereAfter() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
        let! payments = referring<Payment> o
        where (o.Total > 10m)
        return {| Order = o; Payments = payments |}
    }
"""

    [<StepDefinition("I compile code where a query binds its Payments and its Customer with let! twice")>]
    member _.LetTwice() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
        let! payments = referring<Payment> o
        let! customer = referredBy<Customer> o
        return {| Order = o; Payments = payments; Customer = customer |}
    }
"""

    [<StepDefinition("I compile code where a query orders what referredBy finds")>]
    member _.OrderOne() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
        let! customer = plasm { for c in referredBy<Customer> o do orderBy c.Name }
        return {| Order = o; Customer = customer |}
    }
"""

    [<StepDefinition("I compile code where the Orders with their Payments are counted")>]
    member _.CountedCompile() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
        let! payments = referring<Payment> o
        return {| Order = o; Payments = payments |}
    }
let counted (repo: Repo) = repo.Count q
"""
