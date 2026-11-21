namespace Plasm.Specs

open System
open System.Threading.Tasks
open Npgsql
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// What Npgsql reports sending, from its own log: the isolation level of each
/// transaction it began, and the text of each command it executed (a SET sent
/// by a provider reaches the server without passing Plasm's OnCommand).
type NpgsqlSent() =
    let begun = Collections.Concurrent.ConcurrentQueue<string>()
    let commands = Collections.Concurrent.ConcurrentQueue<string>()

    let logger =
        { new Microsoft.Extensions.Logging.ILogger with
            member _.IsEnabled _ = true
            member _.BeginScope _ = null

            member _.Log(_, eventId, state, _, _) =
                match box state with
                | :? Collections.Generic.IReadOnlyList<Collections.Generic.KeyValuePair<string, obj>> as values ->
                    for kv in values do
                        if kv.Key = "IsolationLevel" then begun.Enqueue(string kv.Value)
                        elif kv.Key = "CommandText" && eventId.Name = "CommandExecutionCompleted" then commands.Enqueue(string kv.Value)
                | _ -> () }

    member _.Begun = List.ofSeq begun
    member _.Commands = List.ofSeq commands

    interface Microsoft.Extensions.Logging.ILoggerFactory with
        member _.CreateLogger _ = logger
        member _.AddProvider _ = ()
        member _.Dispose() = ()

/// A provider whose dialect cannot read a consistent snapshot (to test rejection).
type LimitedProvider(inner: IProvider) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "LimitedSQL"
            member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
            member _.ParameterName i = inner.Dialect.ParameterName i
            member _.ColumnType t = inner.Dialect.ColumnType t
            member _.CatalogueTypes t = inner.Dialect.CatalogueTypes t
            member _.CastType t = inner.Dialect.CastType t
            member _.StoreCast(t, array) = inner.Dialect.StoreCast(t, array)
            member _.TextCast = inner.Dialect.TextCast
            member _.EmptyArray = inner.Dialect.EmptyArray
            member _.ListValue values = inner.Dialect.ListValue values
            member _.NotInList(w, operand, list, cast) = inner.Dialect.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = inner.Dialect.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = inner.Dialect.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = inner.Dialect.InList(w, operand, list, cast)
            member _.Catalogue = inner.Dialect.Catalogue
            member _.Ddl = inner.Dialect.Ddl
            member _.UnboundedLimit = inner.Dialect.UnboundedLimit
            member _.NullSafeComparison negated = inner.Dialect.NullSafeComparison negated
            member _.LikeEscape = inner.Dialect.LikeEscape
            member _.FunctionName name = inner.Dialect.FunctionName name
            member _.Json = inner.Dialect.Json
            member _.Exact = inner.Dialect.Exact
            member _.Begin isolation = inner.Dialect.Begin isolation

            member _.Capabilities =
                { inner.Dialect.Capabilities with
                    SnapshotReads = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(_, _) = Task.FromResult None
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

module TxSteps =
    /// Reads the current transaction id through the escape hatch.
    let txid name =
        Tx.run (fun ctx ->
            task {
                use cmd = ctx.CreateCommand()

                match cmd.Connection.GetType().Namespace with
                // SQLite has no transaction id: its connection's transaction
                // object stands for it, one per transaction begun (reached
                // through Sqlite.connectionOf, the provider's way to it).
                | "Plasm.Sqlite" ->
                    let sqlite = Plasm.Sqlite.Sqlite.connectionOf cmd.Connection
                    use own = sqlite.CreateCommand()
                    return Ok(int64 (Runtime.CompilerServices.RuntimeHelpers.GetHashCode own.Transaction))
                | _ ->
                    cmd.CommandText <- "select txid_current()"
                    let! v = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                    return Ok(unbox<int64> v)
            })

/// Rule: Transactions and concurrency are explicit
[<Binding>]
type TransactionSteps(w: World) =

    // Scenario: Named transaction steps compose

    [<StepDefinition("a transaction description with dependent operations")>]
    member _.Dependent() =
        task {
            let! p = Seed.product w.Db "SKU"

            let tx =
                transaction {
                    let! before = TxSteps.txid "txid before"
                    let! customer = Tx.insert (New.customer "Ada" "ada@example.com")
                    let! order = Tx.insert (New.order (Some customer.Id) Draft 10m)
                    let! line = Tx.insert { New.line p 1 10m with OrderId = order.Id }
                    let! after = TxSteps.txid "txid after"
                    return customer, order, line, before, after
                }

            w.Set("tx", (tx: Tx<Customer * Order * OrderLine * int64 * int64, string>))
        }
        |> unit

    [<StepDefinition("a later operation needs an earlier result")>]
    member _.LaterNeedsEarlier() =
        task {
            let! r = w.Repo.Transact(w.Get<Tx<Customer * Order * OrderLine * int64 * int64, string>> "tx")
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
            (transaction {
                let! customer = Tx.insert (New.customer "Ada" "ada@example.com")
                let! first = Tx.insert (New.order (Some customer.Id) Draft 10m)
                // Violates orders_total_check in the database.
                let! second = Tx.insert (New.order (Some customer.Id) Draft -10m)
                return first, second
            }: Tx<Order * Order, string>)
        )

    [<StepDefinition("one step fails")>]
    member _.OneFails() =
        task {
            let! r = w.Repo.Transact(w.Get<Tx<Order * Order, string>> "tx")
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

    [<StepDefinition("the returned error identifies where the failed step is written")>]
    member _.IdentifiesStep() =
        let e: TransactionError<string> = w.Get "error"
        equal "insert Order" e.Step.Action "failed step"
        check (IO.Path.GetFileName e.Step.File = "TransactionSteps.fs") $"failed step written in {e.Step.File}"
        check (e.RolledBack |> List.forall (fun s -> s.Line < e.Step.Line)) $"the failing step {e.Step} is after the steps before it: %A{e.RolledBack}"

        match e.Failure with
        | TxFailure.InvalidChanges("Order", errors) ->
            equal [ "Total", FieldErrorKind.CheckConstraint "orders_total_check" ] (errors |> List.map (fun e -> e.Field, e.Kind)) "the check's field"
        | other -> failwithf "expected the Total field's error, got %A" other

    [<StepDefinition("no earlier step is reported as committed")>]
    member _.NothingCommitted() =
        let e: TransactionError<string> = w.Get "error"
        // Earlier steps are reported only as rolled back; no values are returned.
        equal [ "insert Customer"; "insert Order" ] (e.RolledBack |> List.map (fun s -> s.Action)) "rolled back steps"

    // Scenario: Stale updates do not overwrite newer state

    [<StepDefinition("an Order was read with a concurrency version")>]
    member _.ReadWithVersion() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! r = w.Repo.One(plasm { for o in tableOf<Order> do where (o.Id = id) })
            let o = (ok r).Value
            equal 1 o.Version "read version"
            w.Set("order", o)
        }
        |> unit

    [<StepDefinition("another operation has changed that version")>]
    member _.OtherChanged() =
        task {
            let! r = w.Repo.Transact(w.Get<Order> "order" |> edit (fun o -> { o with Total = 99m }))
            equal 2 (ok r).Version "other writer's version"
        }
        |> unit

    [<StepDefinition("I submit an update expecting the original version")>]
    member _.SubmitStale() =
        task {
            let! r = w.Repo.Transact(w.Get<Order> "order" |> edit (fun o -> { o with Status = Cancelled }))
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the operation returns a concurrency conflict")>]
    member _.Conflict() =
        match w.Get<Result<Order, TransactionError<unit>>> "result" with
        | Error { Failure = TxFailure.Conflict _ } -> ()
        | other -> failwithf "expected a conflict, got %A" other

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
            let! p = Seed.product w.Db "SKU"
            let! _ = Seed.line w.Db id p 1 1m
            w.Set("id", id)
            w.Set("product", p)
        }
        |> unit

    [<StepDefinition("I request a consistent snapshot")>]
    member _.RequestSnapshot() =
        task {
            let (OrderId id) = w.Get "id"
            let (ProductId product) = w.Get "product"

            // A concurrent writer commits an OrderLine between the Orders' query
            // and the query for the lines they own (triggered from the
            // diagnostics hook).
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
                                    w.Db
                                        .Exec($"insert into order_lines(order_id, product_id, quantity, unit_price) values ({id}, {product}, 1, 1)")
                                        .Result
                                    |> ignore) }
                )

            let q = Query.from (tableOf<Order>)
            let! snapshot = repoInterleaved().All(q, ReadConsistency.Snapshot)
            let! independent = repoInterleaved().All(q, ReadConsistency.Independent)
            w.Set("snapshot", ok snapshot)
            w.Set("independent", ok independent)
        }
        |> unit

    [<StepDefinition("the queries execute within a provider-supported consistency mechanism")>]
    member _.Consistent() =
        let count key = (List.exactlyOne (w.Get<Order list> key)).Lines.Length
        // Snapshot: the concurrent line is invisible to the second query.
        equal 1 (count "snapshot") "lines seen in snapshot"
        // ReadConsistency.Independent reads see it (proving the interleaving really happened).
        equal 3 (count "independent") "lines seen without snapshot"

    [<StepDefinition("Or the request is rejected as unsupported")>]
    member _.OrRejected() =
        task {
            let repo =
                Repo(w.Db.Model, LimitedProvider(w.Db.Provider), { RepoOptions.Default with OnCommand = Some w.Db.Recorder.Record })

            w.MarkCommands()
            let! r = repo.All(Query.from (tableOf<Order>), ReadConsistency.Snapshot)

            match r with
            | Error(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) -> equal "snapshot reads" op "operation"
            | other -> failwithf "expected UnsupportedByProvider, got %A" other
        }
        |> unit

    [<StepDefinition("the guarantee is not silently weakened")>]
    member _.NotWeakened() = equal 0 w.CommandsSinceMark.Length "no reads without the guarantee"

    // Scenario: A snapshot read sends no SET, so a pooler need not pin its connection

    [<StepDefinition("I read it as a consistent snapshot, watching what Npgsql sends")>]
    member _.SnapshotWatched() =
        task {
            let sent = new NpgsqlSent()
            let b = NpgsqlDataSourceBuilder(w.Db.ConnectionString)
            b.UseLoggerFactory(sent) |> ignore
            use ds = b.Build()
            let repo = Repo(w.Db.Model, Plasm.Postgres.PostgresProvider(ds), RepoOptions.Default)
            let! r = repo.All(Query.from (tableOf<Order>), ReadConsistency.Snapshot)
            ok r |> ignore
            w.Set("sent", sent)
        }
        |> unit

    [<StepDefinition("the read begins one transaction at REPEATABLE READ")>]
    member _.BeganRepeatableRead() =
        equal [ "RepeatableRead" ] (w.Get<NpgsqlSent> "sent").Begun "isolation levels of the transactions begun"

    [<StepDefinition("no command Npgsql sends is a SET")>]
    member _.NoSet() =
        let sent = w.Get<NpgsqlSent> "sent"
        let commands = sent.Commands
        check (not commands.IsEmpty) "Npgsql reported no commands"
        let sets = commands |> List.filter (fun c -> c.TrimStart().StartsWith("SET", StringComparison.OrdinalIgnoreCase))
        equal [] sets "SET commands"

    // Scenario: Non-transactional effects are not presented as rollback-safe

    [<StepDefinition("a transaction plan includes sending an external message")>]
    member _.ExternalMessage() =
        let sent = Collections.Concurrent.ConcurrentQueue<string>()
        w.Set("sent", sent)

        // The only effects a Tx can describe are database steps, so the message is
        // recorded in a durable outbox table inside the transaction...
        let outbox topic payload =
            Tx.run (fun ctx ->
                task {
                    use cmd = ctx.CreateCommand()
                    cmd.CommandText <- "insert into outbox(topic, payload) values (@t, @p)"
                    for name, value in [ "t", topic; "p", payload ] do
                        let p = cmd.CreateParameter()
                        p.ParameterName <- name
                        p.Value <- value
                        cmd.Parameters.Add p |> ignore
                    let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                    return Ok()
                })

        let plan (total: decimal) : Tx<Order, string> =
            transaction {
                let! order = Tx.insert (New.order None Draft 10m)
                do! outbox "order-placed" (string order.Id)
                let! _ = Tx.insert (New.order None Draft total)
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
            let run: decimal -> Task<Result<Order, TransactionError<string>>> = w.Get "run"
            let! failed = run -1m
            w.Set("failed", failed)
        }
        |> unit

    [<StepDefinition("the API does not represent it as atomically committed with database writes")>]
    member _.NotAtomic() =
        task {
            match w.Get<Result<Order, TransactionError<string>>> "failed" with
            | Error e -> check (e.RolledBack |> List.exists (fun s -> s.Action = "run")) $"the outbox step is reported as rolled back: %A{e.RolledBack}"
            | Ok _ -> failwith "expected the transaction to fail"

            let! pending = w.Db.Scalar<int64>("select count(*) from outbox")
            equal 0L pending "outbox rows after rollback"
            equal 0 (w.Get<Collections.Concurrent.ConcurrentQueue<string>> "sent").Count "messages sent"
        }
        |> unit

    [<StepDefinition("it requires an explicit post-commit or durable-outbox strategy")>]
    member _.PostCommit() =
        task {
            let run: decimal -> Task<Result<Order, TransactionError<string>>> = w.Get "run"
            let! committed = run 5m
            let order = ok committed
            let! pending = w.Db.Scalar<string>("select payload from outbox")
            equal (string order.Id) pending "durable outbox row committed with the order"
            equal [ string order.Id ] (w.Get<Collections.Concurrent.ConcurrentQueue<string>> "sent" |> List.ofSeq) "sent after commit"
        }
        |> unit
