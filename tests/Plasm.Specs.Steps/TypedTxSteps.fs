namespace Plasm.Specs

open System.IO
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas

/// The rejections the transactions of typed-transactions.feature can give.
type Refusal =
    | OutOfStock of string
    | Missing of string
    | Refused of string

/// Feature: Transactions are typed, and their steps need no names
[<Binding>]
type TypedTxSteps(w: World) =

    let transact (tx: Tx<'T, Refusal>) =
        task {
            let! r = w.Repo.Transact tx
            w.Set("typedResult", r |> Result.map box)
            return r
        }

    let failure () =
        match w.Get<Result<obj, TransactionError<Refusal>>> "typedResult" with
        | Error e -> e
        | Ok v -> failwithf "expected the transaction to fail, it gave %A" v

    let named (name: string) =
        plasm { for c in tableOf<Customer> do where (c.Name = name) }

    let refusal (case: string) (text: string) =
        match case with
        | "OutOfStock" -> OutOfStock text
        | "Missing" -> Missing text
        | "Refused" -> Refused text
        | other -> failwith $"not a Refusal case: {other}"

    let compiled (key: string) (code: string) =
        task {
            let! r = CompileCheck.check code
            w.Set(key, r)
        }

    // Rule: A failure says where its step is written

    [<StepDefinition("a transaction inserts a Customer and then fails at a later step")>]
    member _.InsertThenFail() =
        task {
            // The steps below are written one and two lines after this one.
            let at = int __LINE__

            let tx: Tx<unit, Refusal> =
                transaction {
                    let! _ = Tx.insert (New.customer "Ada" "ada@example.com")
                    return! Tx.fail (OutOfStock "widget")
                }

            w.Set("lines", (at + 4, at + 5))
            let! _ = transact tx
            ()
        }
        |> unit

    [<StepDefinition("the error names the file and line of the failing step")>]
    member _.FailingLine() =
        let e = failure ()
        let _, failLine = w.Get<int * int> "lines"
        equal "TypedTxSteps.fs" (Path.GetFileName e.Step.File) "file"
        equal failLine e.Step.Line "line"
        equal "fail" e.Step.Action "action"

    [<StepDefinition("the insert is reported as rolled back, at its own line")>]
    member _.InsertRolledBack() =
        let e = failure ()
        let insertLine, _ = w.Get<int * int> "lines"
        equal [ "insert Customer", insertLine ] (e.RolledBack |> List.map (fun s -> s.Action, s.Line)) "rolled back"

    // Rule: Rejections keep their type

    [<StepDefinition("a transaction rejects with OutOfStock \"(.*)\"")>]
    member _.Rejects(item: string) =
        transact (Tx.fail (OutOfStock item): Tx<unit, Refusal>) |> unit

    [<StepDefinition("a custom step returns Error \\(OutOfStock \"(.*)\"\\)")>]
    member _.CustomRejects(item: string) =
        transact (Tx.run (fun _ -> Task.FromResult(Error(OutOfStock item))): Tx<unit, Refusal>) |> unit

    [<StepDefinition("a transaction takes a decision that returns Error \"(.*)\" through Tx.ofResult")>]
    member _.Decision(reason: string) =
        let decision: Result<int, string> = Error reason

        transact (
            transaction {
                let! n = decision |> Tx.ofResult Refused
                return n
            }
        )
        |> unit

    [<StepDefinition("the failure is the rejection (\\w+) \"(.*)\", read without a cast")>]
    member _.Rejection(case: string, text: string) =
        match (failure ()).Failure with
        | TxFailure.Rejected r -> equal (refusal case text) r "rejection"
        | other -> failwithf "expected a rejection, got %A" other

    [<StepDefinition("I compile code that reads a transaction's rejection as another type")>]
    member _.CompileOtherType() =
        compiled
            "compiled"
            """
type Refusal = Missing of string
let reason (e: TransactionError<Refusal>) =
    match e.Failure with
    | TxFailure.Rejected (text: string) -> text
    | _ -> ""
"""
        |> unit

    [<StepDefinition("the code does not compile")>]
    member _.DoesNotCompile() =
        check (not (w.Get<CompileCheck.CompileResult> "compiled").Compiles) "the code compiled"

    [<StepDefinition("the code compiles")>]
    member _.Compiles() =
        let r = w.Get<CompileCheck.CompileResult> "compiled"
        check r.Compiles $"the code does not compile: %A{r}"

    // Rule: Reading one row and counting are steps

    [<StepDefinition("a Customer named \"(.*)\"")>]
    member _.Customer(name: string) =
        task {
            let! c = w.Repo.Transact(Tx.insert (New.customer name $"{name.ToLower()}@example.com"): Tx<_, unit>)
            w.Set("customer", ok c)
        }
        |> unit

    [<StepDefinition("^Customers named \"([^\"]*)\", \"([^\"]*)\" and \"([^\"]*)\"$")>]
    member _.Customers(a: string, b: string, c: string) =
        task {
            for name in [ a; b; c ] do
                let! r = w.Repo.Transact(Tx.insert (New.customer name $"{name.ToLower()}@example.com"): Tx<_, unit>)
                ok r |> ignore
        }
        |> unit

    [<StepDefinition("a transaction reads the Customer named \"(.*)\" with Tx.one$")>]
    member _.One(name: string) =
        transact (Tx.one (named name)) |> unit

    [<StepDefinition("it gives that Customer")>]
    member _.GivesCustomer() =
        let r = w.Get<Result<obj, TransactionError<Refusal>>> "typedResult"
        equal (Some(w.Get<Customer> "customer")) (ok r |> unbox<Customer option>) "customer"

    [<StepDefinition("it gives None")>]
    member _.GivesNone() =
        let r = w.Get<Result<obj, TransactionError<Refusal>>> "typedResult"
        equal None (ok r |> unbox<Customer option>) "customer"

    [<StepDefinition("a transaction reads the Customer named \"(.*)\" with Tx.one and Tx.orReject \\(Missing \"(.*)\"\\)")>]
    member _.OneOrReject(name: string, what: string) =
        transact (Tx.one (named name) |> Tx.orReject (Missing what)) |> unit

    [<StepDefinition("a transaction counts the Customers with Tx.count")>]
    member _.Count() =
        task {
            w.MarkCommands()
            let! _ = transact (Tx.count (Query.from (tableOf<Customer>)))
            ()
        }
        |> unit

    [<StepDefinition("^it gives (\\d+)$")>]
    member _.Gives(n: int64) =
        let r = w.Get<Result<obj, TransactionError<Refusal>>> "typedResult"
        equal n (ok r |> unbox<int64>) "count"

    [<StepDefinition("the command it sent counts the rows")>]
    member _.Counted() =
        let selects = w.CommandsSinceMark |> List.filter (fun e -> e.Sql.StartsWith "SELECT")
        check (selects |> List.exists (fun e -> e.Sql.Contains "COUNT(*)")) $"the commands were %A{selects |> List.map (fun e -> e.Sql)}"

    // Rule: Steps need no names

    [<StepDefinition("I compile a transaction that inserts a Customer and reads it back, naming no step")>]
    member _.CompileUnnamed() =
        compiled
            "compiled"
            """
let readBack (name: string) : Tx<Customer option, string> =
    transaction {
        let! _ = Tx.insert (New.customer name "x@example.com")
        return! Tx.one (plasm { for c in tableOf<Customer> do where (c.Name = name) })
    }
"""
        |> unit

    // Rule: Steps write records

    [<StepDefinition("a transaction inserts the Customer \"(.*)\" as a record")>]
    member _.InsertRecord(name: string) =
        transact (Tx.insert (New.customer name "ada@example.com")) |> unit

    [<StepDefinition("it gives the Customer \"(.*)\" with its key")>]
    member _.GivesWithKey(name: string) =
        let c = ok (w.Get<Result<obj, TransactionError<Refusal>>> "typedResult") |> unbox<Customer>
        check (c.Name = name && c.Id <> CustomerId 0L) $"%A{c}"

    [<StepDefinition("a transaction updates that Customer's name to \"(.*)\" from the record read")>]
    member _.UpdateName(name: string) =
        task {
            let c = w.Get<Customer> "customer"
            w.MarkCommands()
            let! _ = transact (Tx.update (c, { c with Name = name }))
            ()
        }
        |> unit

    [<StepDefinition("the Customer is stored with the name \"(.*)\"")>]
    member _.StoredName(name: string) =
        task {
            let! r = w.Repo.All(Query.from (tableOf<Customer>))
            equal [ name ] (ok r |> List.map (fun c -> c.Name)) "names"
        }
        |> unit

    [<StepDefinition("the update sent sets only the name")>]
    member _.OnlyName() =
        let update = w.CommandsSinceMark |> List.find (fun e -> e.Sql.StartsWith "UPDATE")
        let set = update.Sql.Substring(0, update.Sql.IndexOf "\nWHERE ")
        check (set.Contains "\"name\"" && not (set.Contains "\"email\"")) update.Sql

    [<StepDefinition("a draft Order, inserted")>]
    member _.DraftInserted() =
        task {
            let! o = w.Repo.Transact(Tx.insert (New.order None Draft 10m): Tx<_, unit>)
            w.Set("order", ok o)
        }
        |> unit

    [<StepDefinition("a draft Order whose version is 2")>]
    member _.Version2() =
        task {
            let! o = w.Repo.Transact(Tx.insert (New.order None Draft 10m): Tx<_, unit>)
            let! o = w.Repo.Transact(ok o |> edit (fun o -> { o with Total = 11m }))
            let o = ok o
            equal 2 o.Version "version"
            w.Set("order", o)
        }
        |> unit

    [<StepDefinition("^a transaction reads that Order as seen at version (\\d+) and updates its total to (\\d+)$")>]
    member _.ReadAsSeenAndUpdate(seen: int, total: decimal) =
        let o = w.Get<Order> "order"

        transact (
            transaction {
                let! read = Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order")
                return! Tx.update (read, { read with Total = total })
            }
        )
        |> unit

    [<StepDefinition("^the Order is stored with the total (\\d+) at version (\\d+)$")>]
    member _.StoredAt(total: decimal, version: int) =
        task {
            let! r = w.Repo.All(Query.from (tableOf<Order>))
            equal [ total, version ] (ok r |> List.map (fun o -> o.Total, o.Version)) "total and version"
        }
        |> unit

    /// The decision would refuse: were the copy not checked where it is read,
    /// the caller would be told Refused, decided on a record it has not seen.
    [<StepDefinition("^a transaction reads that Order as seen at version (\\d+) and would then refuse the change$")>]
    member _.ReadStaleWouldRefuse(seen: int) =
        let o = w.Get<Order> "order"

        transact (
            transaction {
                let! _ = Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order")
                return! (Tx.fail (Refused "not now"): Tx<Order, Refusal>)
            }
        )
        |> unit

    [<StepDefinition("^a transaction reads that Order as seen at version (\\d+), counting what it sends$")>]
    member _.ReadStaleCounted(seen: int) =
        let o = w.Get<Order> "order"
        w.MarkCommands()

        task {
            let! _ = transact (Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order"))
            // What the transaction sent, before later steps read the Order.
            w.Set("sentByRead", w.CommandsSinceMark)
        }
        |> unit

    [<StepDefinition("the read sent one SELECT, of the Order's own row, and none of its Lines")>]
    member _.OneSelect() =
        let selects =
            w.Get<CommandEvent list> "sentByRead" |> List.filter (fun c -> c.Kind = CommandKind.Statement && c.Sql.StartsWith "SELECT")

        match selects with
        | [ c ] -> check (c.Sql.Contains "FROM \"orders\"" && not (c.Sql.Contains "order_lines")) c.Sql
        | other -> failwithf "expected one SELECT, got %A" (other |> List.map (fun c -> c.Sql))

    [<StepDefinition("the failure is a conflict, and the Order is unchanged")>]
    member _.ConflictUnchanged() =
        task {
            match (failure ()).Failure with
            | TxFailure.Conflict _ -> ()
            | other -> failwithf "expected a conflict, got %A" other

            let! r = w.Repo.All(Query.from (tableOf<Order>))
            equal [ 11m ] (ok r |> List.map (fun o -> o.Total)) "totals"
        }
        |> unit

    /// The other writer commits on its own connection while this transaction
    /// holds only what it read.
    [<StepDefinition("^a transaction reads that Order as seen at version (\\d+), and another writer changes it before the update$")>]
    member _.ChangedBetween(seen: int) =
        let o = w.Get<Order> "order"

        transact (
            transaction {
                let! read = Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order")
                let other = w.Repo.Transact(o |> edit (fun x -> { x with Total = 42m })).Result
                ok other |> ignore
                return! Tx.update (read, { read with Total = 99m })
            }
        )
        |> unit

    [<StepDefinition("two draft Orders, the second at version 2")>]
    member _.TwoOrders() =
        task {
            let! first = w.Repo.Transact(Tx.insert (New.order None Draft 10m): Tx<_, unit>)
            let! second = w.Repo.Transact(Tx.insert (New.order None Draft 20m): Tx<_, unit>)
            let! second = w.Repo.Transact(ok second |> edit (fun o -> { o with Total = 21m }))
            equal 2 (ok second).Version "the second's version"
            w.Set("orders", [ ok first; ok second ])
        }
        |> unit

    [<StepDefinition("^a transaction reads both through one helper as seen at version (\\d+)$")>]
    member _.BothByHelper(seen: int) =
        // One line reads both: the step alone cannot say which was stale.
        let asSeen (o: Order) = Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order")

        match w.Get<Order list> "orders" with
        | [ first; second ] ->
            transact (
                transaction {
                    let! _ = asSeen first
                    return! asSeen second
                }
            )
            |> unit
        | other -> failwithf "expected two orders, got %A" other

    [<StepDefinition("^a transaction reads both with Tx.traverse as seen at version (\\d+)$")>]
    member _.BothByTraverse(seen: int) =
        w.Get<Order list> "orders"
        |> Tx.traverse (fun o -> Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order"))
        |> transact
        |> unit

    [<StepDefinition("the failure is a conflict naming the second Order's key")>]
    member _.NamesSecond() =
        let second = (w.Get<Order list> "orders").[1]
        let f = (failure ()).Failure
        check (f.IsConflict) $"expected a conflict, got %A{f}"
        equal (Some(box second.Id)) (conflictKey f) "the key the conflict names"

    [<StepDefinition("the failure is a conflict naming the Order's key")>]
    member _.NamesOrder() =
        let f = (failure ()).Failure
        check (f.IsConflict) $"expected a conflict, got %A{f}"
        equal (Some(box (w.Get<Order> "order").Id)) (conflictKey f) "the key the conflict names"

    [<StepDefinition("^a transaction reads that Order as seen at version (\\d+) at a known line$")>]
    member _.ReadAtKnownLine(seen: int) =
        let o = w.Get<Order> "order"
        let line = int __LINE__ + 1
        let read = Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = o.Id) }, seen) |> Tx.orReject (Missing "order")
        w.Set("knownLine", line)
        transact read |> unit

    [<StepDefinition("the failure's step is that Tx.oneAsSeen, at its file and line")>]
    member _.StaleStep() =
        let e = failure ()
        check e.Failure.IsConflict $"expected a conflict, got %A{e.Failure}"
        equal ("TypedTxSteps.fs", w.Get<int> "knownLine", "oneAsSeen Order") (System.IO.Path.GetFileName e.Step.File, e.Step.Line, e.Step.Action) "the step"

    [<StepDefinition("^a transaction updates that Order's placed time to (.+)$")>]
    member _.PlacedTime(written: string) =
        let o = w.Get<Order> "order"
        let at = System.DateTime.Parse(written, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)

        task {
            let! r = transact (Tx.update (o, { o with PlacedAt = at }))
            w.Set("givenBack", ok r)
        }
        |> unit

    [<StepDefinition("^the Order it gives back was placed at (.+)$")>]
    member _.GivenBackAt(stored: string) =
        let at = System.DateTime.Parse(stored, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
        equal at.Ticks (w.Get<Order> "givenBack").PlacedAt.Ticks "the placed time given back, in ticks"

    [<StepDefinition("the Order read again is the Order it gave back")>]
    member _.ReadAgain() =
        task {
            let given = w.Get<Order> "givenBack"
            let! r = w.Repo.One(plasm { for x in tableOf<Order> do where (x.Id = given.Id) })
            equal (Some given) (ok r) "the Order read again"
        }
        |> unit

    [<StepDefinition("the failure is a conflict, and the Order keeps the other writer's change")>]
    member _.ConflictKeepsOther() =
        task {
            match (failure ()).Failure with
            | TxFailure.Conflict _ -> ()
            | other -> failwithf "expected a conflict, got %A" other

            let! r = w.Repo.All(Query.from (tableOf<Order>))
            equal [ 42m ] (ok r |> List.map (fun o -> o.Total)) "totals"
        }
        |> unit

    [<StepDefinition("^a transaction reads an Order that does not exist as seen at version (\\d+), with Tx.orReject \\(Missing \"(.*)\"\\)$")>]
    member _.MissingAsSeen(seen: int, what: string) =
        let missing = OrderId 987654L

        transact (Tx.oneAsSeen (plasm { for x in tableOf<Order> do where (x.Id = missing) }, seen) |> Tx.orReject (Missing what))
        |> unit

    [<StepDefinition("^a transaction reads that Customer as seen at version (\\d+)$")>]
    member _.UnversionedAsSeen(seen: int) =
        let c = w.Get<Customer> "customer"
        transact (Tx.oneAsSeen (plasm { for x in tableOf<Customer> do where (x.Id = c.Id) }, seen)) |> unit

    [<StepDefinition("the failure says Customer has no version field, and to read it with Tx.one")>]
    member _.NoVersionField() =
        match (failure ()).Failure with
        | TxFailure.Misused message -> check (message.Contains "Customer has no version field" && message.Contains "Tx.one") message
        | other -> failwithf "expected the read to be refused, got %A" other

    [<StepDefinition("a transaction deletes that Customer")>]
    member _.DeleteRecord() =
        transact (Tx.delete (w.Get<Customer> "customer")) |> unit

    [<StepDefinition("no Customer is stored")>]
    member _.NoCustomer() =
        task {
            let! r = w.Repo.All(Query.from (tableOf<Customer>))
            equal [] (ok r) "customers"
        }
        |> unit
