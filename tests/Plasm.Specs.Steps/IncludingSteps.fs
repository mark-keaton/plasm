/// Queries for including.feature checked by `repo.CheckQueries`, and the
/// relationship written once as a reflected function.
namespace Plasm.Specs.Including

open Plasm
open Plasm.TestDomain.Domain

module Relations =
    /// An Order's Payments, written once: used in `including` and in `exists`.
    [<ReflectedDefinition>]
    let paymentsOf (o: Order) =
        plasm {
            for p in tableOf<Payment> do
                where (p.OrderId = o.Id)
        }

module IncludingGood =
    let withPayments =
        plasm {
            for o in tableOf<Order> do
                orderBy o.Total

                including (
                    plasm {
                        for p in tableOf<Payment> do
                            where (p.OrderId = o.Id)
                            orderBy p.CreatedAt
                    }
                )
        }

    let withPaymentsOfAtLeast =
        Query.prepare (fun (least: Arg<decimal>) ->
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total

                    including (
                        plasm {
                            for p in tableOf<Payment> do
                                where (p.OrderId = o.Id && p.Amount >= least.Value)
                                orderBy p.CreatedAt
                        }
                    )
            })

namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Specs.Including
open Plasm.Specs.Including.Relations

/// Feature: A query can read each record with the records another query finds for it
[<Binding>]
type IncludingSteps(w: World) =

    let total (d: decimal) = d.ToString("0.##", Globalization.CultureInfo.InvariantCulture)

    let read (q: Projection<'T * 'U list>) =
        task {
            w.MarkCommands()
            let! r = w.Repo.All q
            w.Set("including", r |> Result.map (List.map (fun (o, xs) -> box o, List.map box xs)))
        }
        |> unit

    let rows () =
        ok (w.Get<Result<(obj * obj list) list, DbError>> "including")

    let payment (p: obj) =
        let p = p :?> Payment
        $"{total p.Amount} {p.Status}"

    let refusal () =
        match w.Get<Result<(obj * obj list) list, DbError>> "including" with
        | Error(DbError.Translation e) -> string e
        | other -> failwithf "expected the read to be refused, got %A" other

    let compiled (code: string) =
        task {
            let! r = CompileCheck.check code
            w.Set("compiled", r)
        }
        |> unit

    let orderOf (t: decimal) =
        (w.Get<Map<decimal, OrderId>> "includingOrders").[t]

    [<StepDefinition("^the Customers \"Ada\" and \"Bob\"$")>]
    member _.Customers() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            w.Set("includingCustomers", (ada, bob))
        }
        |> unit

    [<StepDefinition("Ada's Orders of 10 and 50, Bob's Order of 200, and an Order of 5 with no Customer")>]
    member _.Orders() =
        task {
            let ada, bob = w.Get<CustomerId * CustomerId> "includingCustomers"
            let mutable orders = Map.empty

            for customer, t in [ Some ada, 10m; Some ada, 50m; Some bob, 200m; None, 5m ] do
                let! id = Seed.order w.Db customer "published" t (Seed.at 0)
                orders <- orders.Add(t, id)

            w.Set("includingOrders", orders)
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

    // -- Reading ---------------------------------------------------------------

    [<StepDefinition("I read the Orders by total, including their Payments by minute")>]
    member _.WithPayments() = read IncludingGood.withPayments

    [<StepDefinition("I read the Orders by total, including their succeeded Payments through paymentsOf")>]
    member _.Succeeded() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total

                    including (
                        plasm {
                            for p in paymentsOf o do
                                where (p.Status = Succeeded)
                                orderBy p.CreatedAt
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders by total, including each one's newest Payment")>]
    member _.Newest() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total

                    including (
                        plasm {
                            for p in tableOf<Payment> do
                                where (p.OrderId = o.Id)
                                orderByDescending p.CreatedAt
                                take 1
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders including two of their Payments in no order")>]
    member _.Unordered() =
        read (
            plasm {
                for o in tableOf<Order> do
                    including (
                        plasm {
                            for p in tableOf<Payment> do
                                where (p.OrderId = o.Id)
                                take 2
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders by total, including the Orders of the same Customer")>]
    member _.SameCustomer() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total

                    including (
                        plasm {
                            for x in tableOf<Order> do
                                where (x.CustomerId = o.CustomerId)
                                orderBy x.Total
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders including the Payments made after the Order was placed")>]
    member _.AfterPlaced() =
        read (
            plasm {
                for o in tableOf<Order> do
                    including (
                        plasm {
                            for p in tableOf<Payment> do
                                where (p.OrderId = o.Id && p.CreatedAt > o.PlacedAt)
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders including their Payments of at least 25, through a prepared query")>]
    member _.Prepared() = read (IncludingGood.withPaymentsOfAtLeast 25m)

    [<StepDefinition("I read the Orders including their OrderLines")>]
    member _.Lines() =
        read (
            plasm {
                for o in tableOf<Order> do
                    including (
                        plasm {
                            for l in tableOf<OrderLine> do
                                where (l.OrderId = o.Id)
                        }
                    )
            }
        )

    [<StepDefinition("I read the Orders joined with their Customers, including their Payments")>]
    member _.Joined() =
        read (
            plasm {
                for o in tableOf<Order> do
                    join c in tableOf<Customer> on (o.CustomerId = Some c.Id)

                    including (
                        plasm {
                            for p in tableOf<Payment> do
                                where (p.OrderId = o.Id)
                        }
                    )
            }
        )

    [<StepDefinition("a transaction reads the Orders by total, including their Payments by minute")>]
    member _.InTransaction() =
        task {
            let! r = w.Repo.Transact(Tx.all IncludingGood.withPayments: Tx<_, unit>)

            let rows: Result<(obj * obj list) list, DbError> =
                match r with
                | Ok rows -> Ok(rows |> List.map (fun (o, ps) -> box o, List.map box ps))
                | Error e -> failwithf "the transaction failed: %A" e

            w.Set("including", rows)
        }
        |> unit

    [<StepDefinition("I read the Orders with a Payment through paymentsOf in exists")>]
    member _.Exists() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do where (exists (paymentsOf o)); orderBy o.Total })
            w.Set("includingExists", ok r)
        }
        |> unit

    [<StepDefinition("I read the Orders by total, including the amounts of their Payments by minute")>]
    member _.Amounts() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total
                    including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id); orderBy p.CreatedAt; select p.Amount })
            }
        )

    [<StepDefinition("I read the Orders by total, including the amount of each one's newest Payment")>]
    member _.NewestAmount() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total
                    including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id); orderByDescending p.CreatedAt; take 1; select p.Amount })
            }
        )

    [<StepDefinition("I read the Orders by total, including the distinct statuses of their Payments")>]
    member _.DistinctStatuses() =
        read (
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Total
                    including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id); orderBy p.Status; selectDistinct p.Status })
            }
        )

    [<StepDefinition("I read the Orders including two distinct statuses of their Payments")>]
    member _.TwoDistinct() =
        read (
            plasm {
                for o in tableOf<Order> do
                    including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id); orderBy p.Status; take 2; selectDistinct p.Status })
            }
        )

    [<StepDefinition("the Orders and the values they include are:")>]
    member _.Values(table: DataTable) =
        let text (v: obj) =
            match v with
            | :? decimal as d -> total d
            | v -> string v

        let expected = [ for r in table.Rows -> r.["order"], r.["values"].Trim() ]
        let actual = [ for o, xs in rows () -> total (o :?> Order).Total, xs |> List.map text |> String.concat ", " ]
        equal expected actual "orders and their values"

    [<StepDefinition("the read is refused, saying take and skip cannot be used with selectDistinct for each Order either")>]
    member _.NoDistinctPaging() =
        let e = refusal ()
        check (e.Contains "take and skip cannot be used with selectDistinct") e

    [<StepDefinition("the Payments command reads only the amount and the Order it belongs to")>]
    member _.OnlyAmount() =
        let payments = w.CommandsSinceMark |> List.last
        check (payments.Sql.Contains "\"amount\"" && payments.Sql.Contains "\"order_id\"" && not (payments.Sql.Contains "\"status\"") && not (payments.Sql.Contains "\"created_at\" AS \"")) payments.Sql

    // -- Results ---------------------------------------------------------------

    [<StepDefinition("the Orders and their Payments are:")>]
    member _.Are(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["payments"].Trim() ]
        let actual = [ for o, ps in rows () -> total (o :?> Order).Total, ps |> List.map payment |> String.concat ", " ]
        equal expected actual "orders and payments"

    [<StepDefinition("the Orders and the totals of the Orders they include are:")>]
    member _.Included(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["order"], r.["included"].Trim() ]
        let actual = [ for o, xs in rows () -> total (o :?> Order).Total, xs |> List.map (fun x -> total (x :?> Order).Total) |> String.concat ", " ]
        equal expected actual "orders and the orders they include"

    [<StepDefinition("the Order of 10 still has its line")>]
    member _.StillHasLine() =
        let o = rows () |> List.map (fst >> unbox<Order>) |> List.find (fun o -> o.Total = 10m)
        equal 1 o.Lines.Length "lines of the Order of 10"

    [<StepDefinition("the Order of 50 comes with 3 Payments")>]
    member _.FiftyHasThree() =
        let _, ps = rows () |> List.find (fun (o, _) -> (o :?> Order).Total = 50m)
        equal 3 ps.Length "payments of the Order of 50"

    [<StepDefinition("the Order of 50 comes with the Payments (.*)")>]
    member _.FiftyWith(text: string) =
        let _, ps = rows () |> List.find (fun (o, _) -> (o :?> Order).Total = 50m)
        equal text (ps |> List.map payment |> String.concat ", ") "payments of the Order of 50"

    [<StepDefinition("the Orders found are those of 10 and 50")>]
    member _.Found() =
        equal [ "10"; "50" ] (w.Get<Order list> "includingExists" |> List.map (fun o -> total o.Total)) "orders"

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

    [<StepDefinition("the plan lists the Payments as one more load")>]
    member _.Plan() =
        let plan = ok (w.Repo.Explain IncludingGood.withPayments)
        check (plan.Loads |> List.exists (fun l -> l.Target = "Payment" && l.Sql.Contains "ANY(")) $"loads: %A{plan.Loads}"

    [<StepDefinition("the read is refused, saying the inner query must be ordered to take")>]
    member _.MustOrder() =
        let e = refusal ()
        check (e.Contains "order") e

    [<StepDefinition("the read is refused, naming the condition that refers to the Order")>]
    member _.NamesCondition() =
        let e = refusal ()
        check (e.Contains "PlacedAt") e

    [<StepDefinition("the read is refused, saying OrderLines are loaded with their Order")>]
    member _.LoadedWithOwner() =
        let e = refusal ()
        check (e.Contains "OrderLine" && e.Contains "loaded with") e

    [<StepDefinition("the read is refused, saying including follows a query of one record")>]
    member _.OneRecord() =
        let e = refusal ()
        check (e.Contains "including" && e.Contains "one record") e

    [<StepDefinition("I run the Orders with their Payments twice through one repository")>]
    member _.RunTwice() =
        task {
            let repo = w.Db.RepoWith()
            let! _ = repo.All IncludingGood.withPayments
            let! _ = repo.All IncludingGood.withPayments
            w.Set("plans", repo.PlansMade)
        }
        |> unit

    [<StepDefinition("I check the queries of a module whose including queries translate")>]
    member _.Check() =
        w.Set("problems", w.Repo.CheckQueries(typeof<IncludingSteps>.Assembly, "Plasm.Specs.Including.IncludingGood"))

    // -- Compiling -------------------------------------------------------------

    [<StepDefinition("^I compile code that passes the Orders read with their Payments to a function of Order \\* Payment list$")>]
    member _.CompileWith() =
        compiled
            """
let show (rows: (Order * Payment list) list) = rows.Length
let q = plasm { for o in tableOf<Order> do including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) }) }
let shown (repo: Repo) = task { let! r = repo.All q in return r |> Result.map show }
"""

    [<StepDefinition("I compile code that passes the Orders read without them to that function")>]
    member _.CompileWithout() =
        compiled
            """
let show (rows: (Order * Payment list) list) = rows.Length
let q = plasm { for o in tableOf<Order> do orderBy o.Total }
let shown (repo: Repo) = task { let! r = repo.All q in return r |> Result.map show }
"""

    [<StepDefinition("I compile code where a query includes Payments and then includes Payments again")>]
    member _.Twice() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
            including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) })
            including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) })
    }
"""

    [<StepDefinition("I compile code where a query includes Payments and then selects the Order's total")>]
    member _.ThenSelect() =
        compiled
            """
let q =
    plasm {
        for o in tableOf<Order> do
            including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) })
            select o.Total
    }
"""

    [<StepDefinition("I compile code where the Orders with their Payments are counted")>]
    member _.Counted() =
        compiled
            """
let q = plasm { for o in tableOf<Order> do including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) }) }
let counted (repo: Repo) = repo.Count q
"""
