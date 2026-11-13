namespace Plasm.Specs

open System
open System.Threading.Tasks
open Npgsql
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module TxSteps =
    /// Reads the current transaction id through the escape hatch.
    let txid name =
        Tx.run name (fun ctx ->
            task {
                use cmd = ctx.Connection.CreateCommand()
                cmd.Transaction <- ctx.Transaction
                cmd.CommandText <- "select txid_current()"
                let! v = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                return Ok(unbox<int64> v)
            })

/// Rule: Transactions and concurrency are explicit
[<Binding>]
type TransactionSteps(w: World) =

    // Scenario: Named transaction steps compose

    [<StepDefinition("a transaction description with named dependent operations")>]
    member _.Dependent() =
        task {
            let! p = Seed.product w.Db "SKU"

            let tx =
                transaction {
                    let! before = TxSteps.txid "txid before"
                    let! customer = Tx.insert "customer" (Changeset.insert (New.customer "Ada" "ada@example.com"))
                    let! order = Tx.insert "order" (Changeset.insert (New.order (Some customer.Id) Draft 10m))
                    let! line = Tx.insert "line" (Changeset.insert { New.line p 1 10m with OrderId = order.Id })
                    let! after = TxSteps.txid "txid after"
                    return customer, order, line, before, after
                }

            w.Set("tx", tx)
        }
        |> unit

    [<StepDefinition("a later operation needs an earlier result")>]
    member _.LaterNeedsEarlier() =
        task {
            let! r = w.Repo.Transact(w.Get<Tx<Customer * Order * OrderLine * int64 * int64>> "tx")
            w.Set("result", ok r)
        }
        |> unit

    [<StepDefinition("it receives that result within the same transaction")>]
    member _.SameTransaction() =
        task {
            let customer, order, line, before, after = w.Get<Customer * Order * OrderLine * int64 * int64> "result"
            equal (Some customer.Id) order.CustomerId "order received the customer"
            equal order.Id line.OrderId "line received the order"
            equal before after "one database transaction"
            let! n = w.Db.Scalar<int64>("select count(*) from order_lines")
            equal 1L n "committed"
        }
        |> unit

    // Scenario: A failed transaction identifies its failed step

    [<StepDefinition("a transaction with multiple write steps")>]
    member _.MultipleWrites() =
        w.Set(
            "tx",
            transaction {
                let! customer = Tx.insert "customer" (Changeset.insert (New.customer "Ada" "ada@example.com"))
                let! first = Tx.insert "first order" (Changeset.insert (New.order (Some customer.Id) Draft 10m))
                // Violates orders_total_check in the database.
                let! second = Tx.insert "second order" (Changeset.insert (New.order (Some customer.Id) Draft -10m))
                return first, second
            }
        )

    [<StepDefinition("one step fails")>]
    member _.OneFails() =
        task {
            let! r = w.Repo.Transact(w.Get<Tx<Order * Order>> "tx")
            w.Set("error", err r)
        }
        |> unit

    [<StepDefinition("all writes in that transaction are rolled back")>]
    member _.RolledBack() =
        task {
            let! customers = w.Db.Scalar<int64>("select count(*) from customers")
            let! orders = w.Db.Scalar<int64>("select count(*) from orders")
            equal (0L, 0L) (customers, orders) "stored rows"
        }
        |> unit

    [<StepDefinition("the returned error identifies the failed step")>]
    member _.IdentifiesStep() =
        let e: TransactionError = w.Get "error"
        equal "second order" e.Step "failed step"

        match e.Failure with
        | DatabaseFailure f -> equal DbFailureKind.CheckViolation f.Kind "failure kind"
        | other -> failwithf "expected DatabaseFailure, got %A" other

    [<StepDefinition("no earlier step is reported as committed")>]
    member _.NothingCommitted() =
        let e: TransactionError = w.Get "error"
        // Earlier steps are reported only as rolled back; no values are returned.
        equal [ "customer"; "first order" ] e.RolledBack "rolled back steps"

    // Scenario: Stale updates do not overwrite newer state

    [<StepDefinition("an Order was read with a concurrency version")>]
    member _.ReadWithVersion() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! r = w.Repo.One(plasm { for o in Schemas.orders do where (o.Id = id) })
            let o = (ok r).Value
            equal 1 o.Version "read version"
            w.Set("order", o)
        }
        |> unit

    [<StepDefinition("another operation has changed that version")>]
    member _.OtherChanged() =
        task {
            let! r = w.Repo.Update(w.Get<Order> "order" |> Changeset.edit (fun o -> { o with Total = 99m }))
            equal 2 (ok r).Version "other writer's version"
        }
        |> unit

    [<StepDefinition("I submit an update expecting the original version")>]
    member _.SubmitStale() =
        task {
            let! r = w.Repo.Update(w.Get<Order> "order" |> Changeset.edit (fun o -> { o with Status = Cancelled }))
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the operation returns a concurrency conflict")>]
    member _.Conflict() =
        match w.Get<Result<Order, WriteError<Order>>> "result" with
        | Error(WriteError.Stale _) -> ()
        | other -> failwithf "expected Stale, got %A" other

    [<StepDefinition("the newer state is preserved")>]
    member _.NewerPreserved() =
        task {
            let! total = w.Db.Scalar<decimal>("select total from orders")
            let! status = w.Db.Scalar<string>("select status from orders")
            let! version = w.Db.Scalar<int>("select version from orders")
            equal (99m, "draft", 2) (total, status, version) "stored state"
        }
        |> unit

    // Scenario: Consistent multi-query reads can be requested

    [<StepDefinition("a relationship graph is loaded through multiple queries")>]
    member _.Graph() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! _ = Seed.payment w.Db id 10m "succeeded" (Seed.at 2)
            w.Set("id", id)
        }
        |> unit

    [<StepDefinition("I request a consistent snapshot")>]
    member _.RequestSnapshot() =
        task {
            let (OrderId id) = w.Get "id"

            // A concurrent writer commits a payment between the parent query and
            // the Payments query (triggered from the diagnostics hook).
            let repoInterleaved () =
                let mutable fired = false

                Repo(
                    w.Db.Model,
                    w.Db.Provider,
                    { RepoOptions.Default with
                        OnCommand =
                            Some(fun e ->
                                w.Db.Recorder.Record e

                                if not fired && e.Sql.Contains "FROM \"orders\"" then
                                    fired <- true
                                    use c = new NpgsqlConnection(w.Db.ConnectionString)
                                    c.Open()
                                    use cmd = c.CreateCommand()

                                    cmd.CommandText <-
                                        $"insert into payments(order_id, amount, status, created_at) values ({id}, 1, 'succeeded', now())"

                                    cmd.ExecuteNonQuery() |> ignore) }
                )

            let q = plasm { for o in Schemas.orders do preload o.Payments }
            let! snapshot = repoInterleaved().All(q, ReadConsistency.Snapshot)
            let! independent = repoInterleaved().All(q, ReadConsistency.Independent)
            w.Set("snapshot", ok snapshot)
            w.Set("independent", ok independent)
        }
        |> unit

    [<StepDefinition("the queries execute within a provider-supported consistency mechanism")>]
    member _.Consistent() =
        let count key = (Assoc.get (List.exactlyOne (w.Get<Order list> key)).Payments).Length
        // Snapshot: the concurrent payment is invisible to the second query.
        equal 1 (count "snapshot") "payments seen in snapshot"
        // ReadConsistency.Independent reads see it (proving the interleaving really happened).
        equal 3 (count "independent") "payments seen without snapshot"

    [<StepDefinition("Or the request is rejected as unsupported")>]
    member _.OrRejected() =
        task {
            let repo =
                Repo(w.Db.Model, LimitedProvider(w.Db.Provider), { RepoOptions.Default with OnCommand = Some w.Db.Recorder.Record })

            w.MarkCommands()
            let! r = repo.All(plasm { for o in Schemas.orders do preload o.Payments }, ReadConsistency.Snapshot)

            match r with
            | Error(Translation(UnsupportedByProvider(op, _))) -> equal "snapshot reads" op "operation"
            | other -> failwithf "expected UnsupportedByProvider, got %A" other
        }
        |> unit

    [<StepDefinition("the guarantee is not silently weakened")>]
    member _.NotWeakened() = equal 0 w.CommandsSinceMark.Length "no reads without the guarantee"

    // Scenario: Non-transactional effects are not presented as rollback-safe

    [<StepDefinition("a transaction plan includes sending an external message")>]
    member _.ExternalMessage() =
        let sent = Collections.Concurrent.ConcurrentQueue<string>()
        w.Set("sent", sent)

        // The only effects a Tx can describe are database steps, so the message is
        // recorded in a durable outbox table inside the transaction...
        let outbox topic payload =
            Tx.run $"outbox {topic}" (fun ctx ->
                task {
                    use cmd = ctx.Connection.CreateCommand()
                    cmd.Transaction <- ctx.Transaction
                    cmd.CommandText <- "insert into outbox(topic, payload) values (@t, @p)"
                    cmd.Parameters.Add(NpgsqlParameter("t", box topic)) |> ignore
                    cmd.Parameters.Add(NpgsqlParameter("p", box payload)) |> ignore
                    let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                    return Ok()
                })

        let plan (total: decimal) =
            transaction {
                let! order = Tx.insert "order" (Changeset.insert (New.order None Draft 10m))
                do! outbox "order-placed" (string order.Id)
                let! _ = Tx.insert "second order" (Changeset.insert (New.order None Draft total))
                return order
            }

        // ...and delivery happens only after Transact reports a commit.
        let run (total: decimal) =
            task {
                let! r = w.Repo.Transact(plan total)

                match r with
                | Ok order -> sent.Enqueue(string order.Id)
                | Error _ -> ()

                return r
            }

        w.Set("run", run)

    [<StepDefinition("that effect cannot participate in the database transaction")>]
    member _.CannotParticipate() =
        task {
            let run: decimal -> Task<Result<Order, TransactionError>> = w.Get "run"
            let! failed = run -1m
            w.Set("failed", failed)
        }
        |> unit

    [<StepDefinition("the API does not represent it as atomically committed with database writes")>]
    member _.NotAtomic() =
        task {
            match w.Get<Result<Order, TransactionError>> "failed" with
            | Error e -> check (List.contains "outbox order-placed" e.RolledBack) "the outbox step is reported as rolled back"
            | Ok _ -> failwith "expected the transaction to fail"

            let! pending = w.Db.Scalar<int64>("select count(*) from outbox")
            equal 0L pending "outbox rows after rollback"
            equal 0 (w.Get<Collections.Concurrent.ConcurrentQueue<string>> "sent").Count "messages sent"
        }
        |> unit

    [<StepDefinition("it requires an explicit post-commit or durable-outbox strategy")>]
    member _.PostCommit() =
        task {
            let run: decimal -> Task<Result<Order, TransactionError>> = w.Get "run"
            let! committed = run 5m
            let order = ok committed
            let! pending = w.Db.Scalar<string>("select payload from outbox")
            equal (string order.Id) pending "durable outbox row committed with the order"
            equal [ string order.Id ] (w.Get<Collections.Concurrent.ConcurrentQueue<string>> "sent" |> List.ofSeq) "sent after commit"
        }
        |> unit
