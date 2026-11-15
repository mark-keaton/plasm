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
        let set = update.Sql.Substring(0, update.Sql.IndexOf " WHERE ")
        check (set.Contains "\"name\"" && not (set.Contains "\"email\"")) update.Sql

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

    [<StepDefinition("a transaction updates it expecting the version 1")>]
    member _.ExpectOne() =
        let o = w.Get<Order> "order"
        transact (Tx.update (o, { o with Total = 99m }, expectVersion = 1)) |> unit

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

    [<StepDefinition("a transaction updates that Customer expecting the version 1")>]
    member _.ExpectUnversioned() =
        let c = w.Get<Customer> "customer"
        transact (Tx.update (c, { c with Name = "x" }, expectVersion = 1)) |> unit

    [<StepDefinition("the failure is an invalid change on the version, saying the record has no version field")>]
    member _.NoVersionField() =
        match (failure ()).Failure with
        | TxFailure.InvalidChanges(_, errors) -> check (errors |> List.exists (fun e -> e.Message.Contains "no version field")) $"%A{errors}"
        | other -> failwithf "expected invalid changes, got %A" other

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
