namespace Plasm.Specs

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: elegance.feature, "A retried transaction waits before trying
/// again": the first Order's transaction is held prepared (PREPARE
/// TRANSACTION), committed in PostgreSQL's eyes but not yet visible, while
/// the second Order's transaction runs and is retried.
[<Binding>]
type RetryWaitSteps(w: World) =

    let mutable prepared: string option = None

    let exec (conn: NpgsqlConnection) (sql: string) (args: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            for name, value in args do
                cmd.Parameters.AddWithValue(name, value) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let commitPrepared () =
        task {
            match prepared with
            | Some gid ->
                use conn = new NpgsqlConnection(w.Db.ConnectionString + ";Pooling=false")
                do! conn.OpenAsync()
                do! exec conn $"COMMIT PREPARED '{gid}'" []
                prepared <- None
            | None -> ()
        }

    /// The second Order cancelled after reading the published Orders, as the
    /// first was.
    let cancelSecond (policy: RetryPolicy) (ct: CancellationToken) =
        let second = w.Get<OrderId> "second"
        let published = plasm { for o in tableOf<Order> do where (o.Status = Published) }

        let tx: Tx<Order, string> =
            transaction {
                let! _ = Tx.count published
                let! o = Tx.one (plasm { for o in tableOf<Order> do where (o.Id = second) }) |> Tx.orReject "no second Order"
                return! Tx.update (o, { o with Status = Cancelled })
            }

        w.Repo.Transact(tx, IsolationLevel.Serializable, policy, ct)

    let waiting (attempts: int) (ms: float) =
        { RetryPolicy.OnSerializationFailure attempts with Wait = fun _ -> TimeSpan.FromMilliseconds ms }

    [<AfterScenario>]
    member _.Release() =
        task {
            match prepared with
            | Some gid ->
                use conn = new NpgsqlConnection(w.Db.ConnectionString + ";Pooling=false")
                do! conn.OpenAsync()
                do! exec conn $"ROLLBACK PREPARED '{gid}'" []
                prepared <- None
            | None -> ()
        }
        :> Task

    [<StepDefinition("two published Orders")>]
    member _.TwoOrders() =
        task {
            let! first = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! second = Seed.order w.Db None "published" 20m (Seed.at 2)
            w.Set("first", first)
            w.Set("second", second)
        }
        |> unit

    [<StepDefinition("the first Order cancelled by a serializable transaction that read the published Orders, prepared but not yet committed")>]
    member _.PreparedFirst() =
        task {
            let (OrderId first) = w.Get<OrderId> "first"
            use conn = new NpgsqlConnection(w.Db.ConnectionString + ";Pooling=false")
            do! conn.OpenAsync()
            do! exec conn "BEGIN ISOLATION LEVEL SERIALIZABLE" []
            do! exec conn "select count(*) from orders where status = 'published'" []
            do! exec conn "update orders set status = 'cancelled' where id = @id" [ "id", box first ]
            let gid = $"plasm_{Guid.NewGuid():N}"
            do! exec conn $"PREPARE TRANSACTION '{gid}'" []
            prepared <- Some gid
        }
        |> unit

    [<StepDefinition("the second Order is cancelled the same way, retried up to {int} times without waiting")>]
    member _.AtOnce(attempts: int) =
        task {
            let! r = cancelSecond (waiting attempts 0.0) CancellationToken.None
            w.Set("outcome", r)
        }
        |> unit

    [<StepDefinition("the second Order is cancelled the same way, retried up to {int} times waiting {int} ms between attempts, while the first commits {int} ms later")>]
    member _.WaitingWhileCommitted(attempts: int, ms: int, later: int) =
        task {
            let commit = task {
                do! Task.Delay later
                do! commitPrepared ()
            }

            let! r = cancelSecond (waiting attempts (float ms)) CancellationToken.None
            do! commit
            w.Set("outcome", r)
        }
        |> unit

    [<StepDefinition("the second Order is cancelled the same way, retried up to {int} times waiting {int} ms between attempts")>]
    member _.Waiting(attempts: int, ms: int) =
        task {
            let! r = cancelSecond (waiting attempts (float ms)) CancellationToken.None
            w.Set("outcome", r)
        }
        |> unit

    [<StepDefinition("the second Order is cancelled the same way, retried waiting {int} seconds between attempts, and cancelled after {int} ms")>]
    member _.Cancelled(seconds: int, ms: int) =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(float ms))
            let sw = Diagnostics.Stopwatch.StartNew()

            let! thrown =
                task {
                    try
                        let! _ = cancelSecond (waiting 5 (float seconds * 1000.0)) cts.Token
                        return false
                    with :? OperationCanceledException ->
                        return true
                }

            w.Set("cancelled", (thrown, sw.Elapsed))
        }
        |> unit

    [<StepDefinition("it fails as a serialization failure after {int} attempts")>]
    member _.FailsAfter(attempts: int) =
        match w.Get<Result<Order, TransactionError<string>>> "outcome" with
        | Ok _ -> failwith "the transaction committed"
        | Error e ->
            match e.Failure with
            | TxFailure.Failed(DbError.Database f) when f.Kind = DbFailureKind.SerializationFailure -> equal attempts e.Attempts "attempts"
            | other -> failwithf "expected a serialization failure, got %s" e.Display

    [<StepDefinition("it fails as a serialization failure after {int} attempts, having waited at least {int} ms")>]
    member this.FailsAfterWaiting(attempts: int, ms: int) =
        this.FailsAfter attempts
        let e = match w.Get<Result<Order, TransactionError<string>>> "outcome" with Error e -> e | Ok _ -> failwith "committed"
        check (e.Waited >= TimeSpan.FromMilliseconds(float ms)) $"waited {e.Waited.TotalMilliseconds} ms"

    [<StepDefinition("the second Order's change is committed")>]
    member _.Committed() =
        match w.Get<Result<Order, TransactionError<string>>> "outcome" with
        | Ok o -> equal Cancelled o.Status "the second Order's status"
        | Error e -> failwithf "the transaction failed: %s after %d attempts" e.Display e.Attempts

    [<StepDefinition("both Orders are cancelled")>]
    member _.BothCancelled() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from orders where status = 'cancelled'")
            equal 2L n "cancelled Orders"
        }
        |> unit

    [<StepDefinition("the transaction ends as cancelled within {int} seconds")>]
    member _.EndsCancelled(seconds: int) =
        let thrown, took = w.Get<bool * TimeSpan> "cancelled"
        check thrown "the transaction was not cancelled"
        check (took < TimeSpan.FromSeconds(float seconds)) $"it took {took.TotalMilliseconds} ms"

    [<StepDefinition("I ask RetryPolicy.OnSerializationFailure {int} for its waits, many times over")>]
    member _.Waits(attempts: int) =
        let policy = RetryPolicy.OnSerializationFailure attempts
        w.Set("waits", [ for _ in 1..200 -> [ for n in 1 .. attempts - 1 -> n, policy.Wait n ] ])

    [<StepDefinition("each wait after attempt n lies between half of and the whole of {int} ms doubled n - 1 times")>]
    member _.WaitsWithin(ms: int) =
        for drawn in w.Get<(int * TimeSpan) list list> "waits" do
            for n, wait in drawn do
                let whole = float ms * Math.Pow(2.0, float (n - 1))
                check (wait.TotalMilliseconds >= whole / 2.0 && wait.TotalMilliseconds <= whole) $"after attempt {n}: {wait.TotalMilliseconds} ms, not within {whole / 2.0}..{whole}"

    [<StepDefinition("the waits are not all the same")>]
    member _.Jittered() =
        let firsts = w.Get<(int * TimeSpan) list list> "waits" |> List.map (List.head >> snd) |> List.distinct
        check (firsts.Length > 1) "every first wait was the same"
