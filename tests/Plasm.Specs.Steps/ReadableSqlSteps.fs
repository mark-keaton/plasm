namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: The SQL a query sends reads as a person would write it
[<Binding>]
type ReadableSqlSteps(w: World) =

    let explain (r: Result<QueryPlan, TranslationError>) =
        match r with
        | Ok plan -> w.Set("readablePlan", plan)
        | Error e -> failwithf "the query was refused: %A" e

    let plan () = w.Get<QueryPlan> "readablePlan"

    [<StepDefinition("^I explain the Customers named \"Ada\" whose email starts with \"a\", other than 3, by name and then email descending, taking 10$")>]
    member _.Entity() =
        explain (
            w.Repo.Explain(
                plasm {
                    for c in tableOf<Customer> do
                    where (c.Name = "Ada" && c.Email.StartsWith "a" && c.Id <> CustomerId 3L)
                    orderBy c.Name
                    thenByDescending c.Email
                    take 10
                }
            )
        )

    [<StepDefinition("I explain the ids and totals of the Orders over 10 or under 2 that are not drafts")>]
    member _.Or() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    where ((o.Total > 10m || o.Total < 2m) && not (o.Status = Draft))
                    select (o.Id, o.Total)
                }
            )
        )

    [<StepDefinition("I explain the Orders joined with their Customers, selecting a record of the total and the name")>]
    member _.Record() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                    select {| Total = o.Total; Name = c.Name |}
                }
            )
        )

    [<StepDefinition("I explain the Orders without a cancellation reason whose doubled total is over 10")>]
    member _.Arithmetic() =
        explain (w.Repo.Explain(plasm { for o in tableOf<Order> do where (o.CancellationReason.IsNone && o.Total * 2m > 10m) }))

    [<StepDefinition("I explain the Orders by total, binding their two newest Payments")>]
    member _.Derived() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    orderBy o.Total
                    let! payments = plasm { for p in referring<Payment> o do orderByDescending p.CreatedAt; take 2 }
                    return {| Order = o; Payments = payments |}
                }
            )
        )

    [<StepDefinition("I explain the Orders' sizes in words, through if, elif and else on the total")>]
    member _.Case() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    select (if o.Total > 100m then "big" elif o.Total > 10m then "medium" else "small")
                }
            )
        )

    [<StepDefinition("the SQL is:")>]
    member _.SqlIs(expected: string) = equal (expected.Trim()) (plan ()).Sql "SQL"

    [<StepDefinition("the Payments load's SQL is:")>]
    member _.LoadSqlIs(expected: string) =
        let load = (plan ()).Loads |> List.find (fun l -> l.Target = "Payment")
        equal (expected.Trim()) load.Sql "the Payments load's SQL"

    // -- Writes ------------------------------------------------------------------

    [<StepDefinition("^a Customer \"Ada\" with a published Order of 10$")>]
    member _.AdaOrder() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! id = Seed.order w.Db (Some ada) "published" 10m (Seed.at 0)
            w.Set("readableOrder", id)
        }
        |> unit

    [<StepDefinition("I change the Order to cancelled, then delete it")>]
    member _.ChangeThenDelete() =
        task {
            let id = w.Get<OrderId> "readableOrder"
            let! read = w.Repo.One(plasm { for o in tableOf<Order> do where (o.Id = id) })
            let order = (ok read).Value
            w.MarkCommands()
            let! changed = w.Repo.Transact(Tx.update (order, { order with Status = Cancelled }): Tx<Order, unit>)
            let! _ = w.Repo.Transact(Tx.delete (ok changed): Tx<Order, unit>)
            let commands = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
            w.Set("readableWrites", commands)
        }
        |> unit

    [<StepDefinition("^the update's first line is \"(.*)\" and it has no alias$")>]
    member _.Update(first: string) =
        let update = w.Get<string list> "readableWrites" |> List.find (fun q -> q.StartsWith "UPDATE")
        check (update.Split('\n').[0] = first && not (update.Contains " AS \"orders\"")) update

    [<StepDefinition("the delete is:")>]
    member _.Delete(expected: string) =
        let deletes = w.Get<string list> "readableWrites" |> List.filter (fun q -> q.StartsWith "DELETE" && q.Contains "\"orders\"")
        equal [ expected.Trim() ] deletes "the delete of the Order"

    [<StepDefinition("^I insert a Customer \"(.*)\"$")>]
    member _.Insert(name: string) =
        task {
            w.MarkCommands()
            let! r = w.Repo.Transact(Tx.insert (New.customer name "ada@example.com"): Tx<Customer, unit>)
            ok r |> ignore
            w.Set("readableInserts", w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.filter (fun q -> q.StartsWith "INSERT"))
        }
        |> unit

    [<StepDefinition("the insert is:")>]
    member _.InsertIs(expected: string) = equal [ expected.Trim() ] (w.Get<string list> "readableInserts") "the insert"

    // -- Nothing the result does not need ------------------------------------------

    [<StepDefinition("^I read the one Customer named \"Ada\"$")>]
    member _.One() =
        task {
            w.MarkCommands()
            let! r = w.Repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Ada") })
            ok r |> ignore
            w.Set("readableCommands", w.CommandsSinceMark |> List.map (fun e -> e.Sql))
        }
        |> unit

    [<StepDefinition("^I ask whether a Customer is named \"Ada\"$")>]
    member _.Exists() =
        task {
            w.MarkCommands()
            let! r = w.Repo.Exists(plasm { for c in tableOf<Customer> do where (c.Name = "Ada") })
            ok r |> ignore
            w.Set("readableCommands", w.CommandsSinceMark |> List.map (fun e -> e.Sql))
        }
        |> unit

    [<StepDefinition("the command is:")>]
    member _.CommandIs(expected: string) =
        equal [ expected.Trim() ] (w.Get<string list> "readableCommands") "commands"

    [<StepDefinition("I explain a page of the Customers by email")>]
    member _.UniquePage() =
        explain (w.Repo.Explain(plasm { for c in tableOf<Customer> do orderBy c.Email; take 10 }))

    [<StepDefinition("I explain a page of the Orders joined with their Customers, by Order id")>]
    member _.JoinedPage() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                    orderBy o.Id
                    take 10
                }
            )
        )

    [<StepDefinition("^the SQL ends \"(.*)\"$")>]
    member _.SqlEnds(tail: string) =
        let sql = (plan ()).Sql
        check (sql.EndsWith tail) sql

    [<StepDefinition("the SQL ends:")>]
    member _.SqlEndsLines(tail: string) =
        let sql = (plan ()).Sql
        check (sql.EndsWith(tail.Trim())) sql

    [<StepDefinition("^the SQL has the line \"(.*)\"$")>]
    member _.SqlLine(line: string) =
        let sql = (plan ()).Sql
        check (sql.Split('\n') |> Array.contains line) sql

    [<StepDefinition("^the Payments load's SQL has the line \"(.*)\"$")>]
    member _.LoadLine(line: string) =
        let load = (plan ()).Loads |> List.find (fun l -> l.Target = "Payment")
        check (load.Sql.Split('\n') |> Array.contains line) load.Sql

    [<StepDefinition("I explain the Orders for which some Order is over 100, both read by o")>]
    member _.Shadowing() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    where (exists (plasm { for o in tableOf<Order> do where (o.Total > 100m) }))
                }
            )
        )

    [<StepDefinition("I explain the Orders over 10, read by ``an order``")>]
    member _.Quoted() =
        explain (w.Repo.Explain(plasm { for ``an order`` in tableOf<Order> do where (``an order``.Total > 10m) }))

    [<StepDefinition("I explain the Orders by total, binding their two newest Payments read by ranked")>]
    member _.Reserved() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    orderBy o.Total
                    let! payments = plasm { for ranked in referring<Payment> o do orderByDescending ranked.CreatedAt; take 2 }
                    return {| Order = o; Payments = payments |}
                }
            )
        )

    [<StepDefinition("I explain the Orders binding the Customer each refers to")>]
    member _.ByKey() =
        explain (
            w.Repo.Explain(
                plasm {
                    for o in tableOf<Order> do
                    let! customer = referredBy<Customer> o
                    return {| Order = o; Customer = customer |}
                }
            )
        )

    [<StepDefinition("the Customers load's SQL is:")>]
    member _.CustomersLoad(expected: string) =
        let load = (plan ()).Loads |> List.find (fun l -> l.Target = "Customer")
        equal (expected.Trim()) load.Sql "the Customers load's SQL"

    [<StepDefinition("^the update sets (\".*)$")>]
    member _.UpdateSets(text: string) =
        let update = w.Get<string list> "readableWrites" |> List.find (fun q -> q.StartsWith "UPDATE")
        check (update.Contains text) update

    [<StepDefinition("I explain the Orders all of whose lines cost more than 5")>]
    member _.ForAll() =
        explain (w.Repo.Explain(plasm { for o in tableOf<Order> do where (o.Lines |> List.forall (fun l -> l.UnitPrice > 5m)) }))

    [<StepDefinition("I explain the Orders with a cancellation reason, ordered by it")>]
    member _.Present() =
        explain (w.Repo.Explain(plasm { for o in tableOf<Order> do where o.CancellationReason.IsSome; orderBy o.CancellationReason }))

    [<StepDefinition("I explain the Orders ordered by their cancellation reason")>]
    member _.NotProven() =
        explain (w.Repo.Explain(plasm { for o in tableOf<Order> do orderBy o.CancellationReason }))

