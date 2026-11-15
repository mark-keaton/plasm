namespace Plasm.Specs

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Reqnroll
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: Execution is observable and failures remain meaningful
[<Binding>]
type ObservabilitySteps(w: World) =

    // Scenario: Query execution exposes diagnostic information

    [<StepDefinition("I execute a query with relationship loads")>]
    member _.ExecuteWithLoads() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! p = Seed.product w.Db "SKU"
            let! _ = Seed.line w.Db id p 1 10m
            // An Order owns its OrderLines: reading Orders loads them.
            let q = plasm { for o in Schemas.orders do where (o.Total > 5m) }
            w.MarkCommands()
            let! r = w.Repo.All q
            ok r |> ignore
            w.Set("query", q)
        }
        |> unit

    [<StepDefinition("diagnostics identify the database commands and their durations")>]
    member _.Diagnostics() =
        let events = w.CommandsSinceMark
        equal 2 events.Length "commands"
        check (events.[0].Sql.Contains "FROM \"orders\"") "parent command"
        check (events.[1].Sql.Contains "FROM \"order_lines\"") "relationship command"

        for e in events do
            check (e.Duration > TimeSpan.Zero) "duration recorded"
            check e.Succeeded "success recorded"

    [<StepDefinition("sensitive parameter values are omitted unless explicitly enabled")>]
    member _.ParametersOmitted() =
        task {
            for e in w.CommandsSinceMark do
                check (not e.Parameters.IsEmpty) "parameters are listed"
                check (e.Parameters |> List.forall (fun (_, v) -> v.IsNone)) "values omitted by default"

            let repo = w.Db.RepoWith(includeParameterValues = true)
            w.MarkCommands()
            let! _ = repo.All(w.Get<Query<Order>> "query")
            let first = w.CommandsSinceMark.Head
            equal [ Some "5" ] (first.Parameters |> List.map snd) "values when enabled"
        }
        |> unit

    // Scenario: Cancellation reaches database execution

    [<StepDefinition("an executing database operation")>]
    member _.Executing() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! r = w.Repo.One(plasm { for o in Schemas.orders do where (o.Id = id) })
            let order = (ok r).Value
            // Another session holds the row lock, so the update blocks in the database.
            let blocker = new NpgsqlConnection(w.Db.ConnectionString)
            do! blocker.OpenAsync()
            let! tx = blocker.BeginTransactionAsync()
            use cmd = blocker.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- "select * from orders for update"
            let! _ = cmd.ExecuteNonQueryAsync()
            let cts = new CancellationTokenSource()
            let started = DateTime.UtcNow
            let running = w.Repo.Update(order |> Changeset.edit (fun o -> { o with Total = 11m }), cts.Token)
            w.Set("blocker", blocker)
            w.Set("blockerTx", tx)
            w.Set("cts", cts)
            w.Set("running", running)
            w.Set("started", started)
        }
        |> unit

    [<StepDefinition("its cancellation token is cancelled")>]
    member _.Cancel() =
        task {
            do! Task.Delay 300
            check (not (w.Get<Task<Result<Order, WriteError<Order>>>> "running").IsCompleted) "still blocked in the database"
            (w.Get<CancellationTokenSource> "cts").Cancel()
        }
        |> unit

    [<StepDefinition("cancellation is propagated to the provider")>]
    member _.Propagated() =
        task {
            let running: Task<Result<Order, WriteError<Order>>> = w.Get "running"

            let! outcome =
                task {
                    try
                        let! r = running
                        return Choice1Of2 r
                    with
                    | :? OperationCanceledException as e -> return Choice2Of2(e :> exn)
                    | e -> return Choice2Of2 e
                }

            w.Set("outcome", outcome)
            let elapsed = DateTime.UtcNow - w.Get<DateTime> "started"
            check (elapsed < TimeSpan.FromSeconds 5.0) $"returned promptly ({elapsed})"
            // The server-side statement was cancelled, not merely abandoned.
            let! active =
                w.Db.Scalar<int64>(
                    "select count(*) from pg_stat_activity where datname = current_database() and query like 'UPDATE \"orders\"%' and state = 'active'"
                )

            equal 0L active "UPDATE still running on the server"
        }
        |> unit

    [<StepDefinition("the operation does not return a successful result")>]
    member _.NotSuccessful() =
        task {
            match w.Get<Choice<Result<Order, WriteError<Order>>, exn>> "outcome" with
            | Choice2Of2(:? OperationCanceledException) -> ()
            | other -> failwithf "expected cancellation, got %A" other

            let tx: NpgsqlTransaction = w.Get "blockerTx"
            do! tx.RollbackAsync()
            (w.Get<NpgsqlConnection> "blocker").Dispose()
            let! total = w.Db.Scalar<decimal>("select total from orders")
            equal 10m total "update not applied"
        }
        |> unit

    // Scenario: Infrastructure failures remain distinct from domain errors

    [<StepDefinition("the database connection fails")>]
    member _.ConnectionFails() =
        task {
            let repo = Repo(w.Db.Model, Postgres.connect "Host=127.0.0.1;Port=1;Timeout=3;Database=none;Username=x;Password=y")
            let! read = repo.All(Query.from Schemas.orders)
            let! write = repo.Insert(Changeset.insert (New.order None Draft 1m))
            w.Set("read", read)
            w.Set("write", write)
        }
        |> unit

    [<StepDefinition("the failure is distinguishable from validation and concurrency errors")>]
    member _.Distinguishable() =
        match w.Get<Result<Order list, DbError>> "read" with
        | Error(DbError.Database f) -> equal DbFailureKind.ConnectionFailure f.Kind "read failure kind"
        | other -> failwithf "expected a database failure, got %A" other

        match w.Get<Result<Order, WriteError<Order>>> "write" with
        | Error(WriteError.Failed f) -> equal DbFailureKind.ConnectionFailure f.Kind "write failure kind"
        | Error(WriteError.Invalid _)
        | Error(WriteError.Stale _) -> failwith "infrastructure failure misreported as a domain error"
        | other -> failwithf "expected Failed, got %A" other

    [<StepDefinition("application code can handle it without parsing error messages")>]
    member _.HandleWithoutParsing() =
        let handle (r: Result<Order, WriteError<Order>>) =
            match r with
            | Error(WriteError.Failed { Kind = DbFailureKind.ConnectionFailure }) -> "retry later"
            | Error(WriteError.Failed _) -> "report"
            | Error(WriteError.Invalid _) -> "show form errors"
            | Error(WriteError.Stale _) -> "reload and retry"
            | Error(WriteError.Materialization _)
            | Error(WriteError.Translation _) -> "report"
            | Ok _ -> "done"

        equal "retry later" (handle (w.Get "write")) "handled by pattern matching"
