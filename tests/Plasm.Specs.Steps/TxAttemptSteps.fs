namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// tx-attempt.feature: a step tried under a savepoint.
[<Binding>]
type TxAttemptSteps(w: World) =

    /// A statement that fails as a serialization failure, as PostgreSQL
    /// reports one (SQLSTATE 40001), through a function made for it.
    let serializationFailure = "select plasm_serialization_failure()"

    let makeFailure () =
        w.Db.Exec
            "create or replace function plasm_serialization_failure() returns void language plpgsql as $$ begin raise exception 'serialization failure' using errcode = 'serialization_failure'; end $$"

    let customers (email: string) =
        w.Db.Scalar<int64>("select count(*) from customers where email = @e", [ "e", box email ])

    let allCustomers () = w.Db.Scalar<int64>("select count(*) from customers")

    let mark () =
        w.MarkCommands()
        w.Set("eventsFrom", w.Db.Recorder.All.Length)

    let attempted () =
        match w.Get<Result<Result<Customer, TxFailure<unit>>, TransactionError<unit>>> "attempted" with
        | Ok tried -> tried
        | Error e -> failwithf "the transaction failed: %A" e

    [<StepDefinition("^a transaction tries to insert another Customer with the email \"(.*)\", and on its failure inserts one with \"(.*)\"$")>]
    member _.TryThenOther(email: string, other: string) =
        task {
            let! before = allCustomers ()
            w.Set("customersBefore", before)
            mark ()

            let tx: Tx<Result<Customer, TxFailure<unit>>, unit> =
                transaction {
                    let! tried = Tx.attempt (Tx.insert (New.customer "Ada again" email))

                    match tried with
                    | Ok _ -> return tried
                    | Error _ ->
                        let! _ = Tx.insert (New.customer "Ada two" other)
                        return tried
                }

            let! r = w.Repo.Transact tx
            w.Set("attempted", r)
        }
        :> Task

    [<StepDefinition("the attempt gave the Email field's error")>]
    member _.EmailError() =
        match attempted () with
        | Error(TxFailure.InvalidChanges(_, errors)) -> equal [ "Email" ] (errors |> List.map (fun e -> e.Field)) "fields"
        | other -> failwithf "expected the Email field's error, got %A" other

    [<StepDefinition("^the transaction committed, with the Customer \"(.*)\" and no other new one$")>]
    member _.Committed(email: string) =
        task {
            attempted () |> ignore
            let! n = customers email
            equal 1L n email
            let! all = allCustomers ()
            equal (w.Get<int64> "customersBefore" + 1L) all "customers"
        }
        :> Task

    [<StepDefinition("^a transaction tries to insert a Customer with the email \"(.*)\"$")>]
    member _.TryInsert(email: string) =
        task {
            let! r = w.Repo.Transact(Tx.attempt (Tx.insert (New.customer "Bo" email)): Tx<_, unit>)
            w.Set("attempted", r)
        }
        :> Task

    [<StepDefinition("the attempt gave the inserted Customer")>]
    member _.Inserted() =
        match attempted () with
        | Ok c -> check (c.Id <> CustomerId 0L) $"the customer has no key: %A{c}"
        | Error f -> failwithf "expected the customer, got %A" f

    [<StepDefinition("^the Customer \"(.*)\" is stored$")>]
    member _.Stored(email: string) =
        task {
            let! n = customers email
            equal 1L n email
        }
        :> Task

    [<StepDefinition("OnCommand reported a SAVEPOINT before the failed INSERT and a ROLLBACK TO that savepoint after it, of Kinds Begin and Rollback")>]
    member _.Savepoint() =
        let events = w.Db.Recorder.All |> List.skip (w.Get<int> "eventsFrom")
        let shown = events |> List.map (fun e -> $"{e.Kind} {e.Sql}") |> String.concat "\n"
        let failed = events |> List.findIndex (fun e -> e.Kind = CommandKind.Statement && not e.Succeeded)
        let save = events |> List.take failed |> List.findBack (fun e -> e.Sql.StartsWith "SAVEPOINT ")
        equal CommandKind.Begin save.Kind $"the SAVEPOINT's kind in\n{shown}"
        let name = save.Sql.Substring "SAVEPOINT ".Length
        let back = events |> List.skip failed |> List.find (fun e -> e.Sql.StartsWith "ROLLBACK TO SAVEPOINT")
        equal CommandKind.Rollback back.Kind $"the ROLLBACK TO's kind in\n{shown}"
        equal $"ROLLBACK TO SAVEPOINT {name}" back.Sql $"the savepoint rolled back to in\n{shown}"

    [<StepDefinition("an Order changed by another writer since it was read")>]
    member _.StaleOrder() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! read = w.Repo.One(plasm { for o in tableOf<Order> do where (o.Id = id) })
            w.Set("staleOrder", (ok read).Value)
            let! _ = w.Db.Exec("update orders set total = 11, version = version + 1 where id = @id", [ "id", box (let (OrderId v) = id in v) ])
            ()
        }
        :> Task

    [<StepDefinition("a transaction tries, in one attempt, to insert a Customer and then update the Order from the copy read before")>]
    member _.TryStale() =
        task {
            let order = w.Get<Order> "staleOrder"

            let tried: Tx<Order, unit> =
                transaction {
                    let! _ = Tx.insert (New.customer "Cy" "cy@example.com")
                    return! Tx.update (order, { order with Total = 99m })
                }

            let! r = w.Repo.Transact(Tx.attempt tried)
            w.Set("staleAttempt", r)
        }
        :> Task

    [<StepDefinition("the attempt gave a Conflict on the Order")>]
    member _.Conflict() =
        match w.Get<Result<Result<Order, TxFailure<unit>>, TransactionError<unit>>> "staleAttempt" with
        | Ok(Error(TxFailure.Conflict(name, _))) -> equal "Order" name "the conflict's record"
        | other -> failwithf "expected the attempt's Conflict, got %A" other

    [<StepDefinition("the Customer the attempt inserted is not stored")>]
    member _.NotStored() =
        task {
            let! n = customers "cy@example.com"
            equal 0L n "customers the attempt inserted"
        }
        :> Task

    [<StepDefinition("a transaction tries an attempt that inserts a Customer and then tries, inside it, a step that fails")>]
    member _.Nested() =
        task {
            let outer: Tx<Customer * Result<unit, TxFailure<string>>, string> =
                transaction {
                    let! c = Tx.insert (New.customer "Di" "di@example.com")
                    let! inner = Tx.attempt (Tx.fail "not this one")
                    return c, inner
                }

            let! r = w.Repo.Transact(Tx.attempt outer)
            w.Set("nested", r)
        }
        :> Task

    [<StepDefinition("the inner attempt gave its failure and the outer one succeeded")>]
    member _.NestedOutcome() =
        match w.Get<Result<Result<Customer * Result<unit, TxFailure<string>>, TxFailure<string>>, TransactionError<string>>> "nested" with
        | Ok(Ok(_, Error(TxFailure.Rejected "not this one"))) -> ()
        | other -> failwithf "expected the outer attempt to succeed around the inner one's failure, got %A" other

    [<StepDefinition("the Customer the outer attempt inserted is stored")>]
    member _.OuterStored() =
        task {
            let! n = customers "di@example.com"
            equal 1L n "customers the outer attempt inserted"
        }
        :> Task

    [<StepDefinition("a transaction tries a step that fails as a serialization failure")>]
    member _.TrySerialization() =
        task {
            let! _ = makeFailure ()
            let! r = w.Repo.Transact(Tx.attempt (Tx.executeSql $"select plasm_serialization_failure()"): Tx<_, unit>)
            w.Set("serialization", r)
        }
        :> Task

    [<StepDefinition("the transaction fails as a serialization failure, not as a value of the attempt")>]
    member _.NotCaught() =
        match w.Get<Result<Result<int, TxFailure<unit>>, TransactionError<unit>>> "serialization" with
        | Error { Failure = TxFailure.Failed(DbError.Database f) } -> equal DbFailureKind.SerializationFailure f.Kind "the failure's kind"
        | other -> failwithf "expected the transaction to fail as a serialization failure, got %A" other

    [<StepDefinition("a transaction with a retry policy inserts a Customer in a step of its own and fails as a serialization failure the first time it runs")>]
    member _.Retried() =
        task {
            let! _ = makeFailure ()
            let runs = ref 0

            let tx: Tx<unit, string> =
                transaction {
                    let! _ =
                        Tx.run (fun ctx ->
                            task {
                                runs.Value <- runs.Value + 1
                                use cmd = ctx.CreateCommand()
                                cmd.CommandText <- "insert into customers (name, email) values ('Retry', 'retry@example.com')"
                                let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                return (Ok(): Result<unit, string>)
                            })

                    let! _ =
                        Tx.run (fun ctx ->
                            task {
                                if runs.Value = 1 then
                                    use cmd = ctx.CreateCommand()
                                    cmd.CommandText <- serializationFailure
                                    let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                    ()

                                return (Ok(): Result<unit, string>)
                            })

                    return ()
                }

            let! r = w.Repo.Transact(tx, retry = RetryPolicy.OnSerializationFailure 3)
            w.Set("retried", r)
            w.Set("runs", runs.Value)
        }
        :> Task

    [<StepDefinition("the transaction committed on its second attempt")>]
    member _.SecondAttempt() =
        ok (w.Get<Result<unit, TransactionError<string>>> "retried")
        equal 2 (w.Get<int> "runs") "attempts"

    [<StepDefinition("the step of its own ran twice")>]
    member _.RanTwice() = equal 2 (w.Get<int> "runs") "times the step ran"

    [<StepDefinition("one Customer was stored by it, not two")>]
    member _.OnceStored() =
        task {
            let! n = customers "retry@example.com"
            equal 1L n "customers the step stored"
        }
        :> Task
