namespace Plasm.Specs

open System
open System.Diagnostics
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Sqlite
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: On SQLite writers take turns (providers.feature)
[<Binding>]
type WriterSteps(w: World) =
    let insert (repo: Repo) (name: string) : Task<Result<Customer, TransactionError<unit>>> =
        repo.Transact(Tx.insert (New.customer name $"{name.ToLowerInvariant()}@example.com"): Tx<_, unit>)

    let stored (name: string) =
        w.Db.Scalar<int64>("select count(*) from customers where name = @n", [ "n", box name ])

    [<StepDefinition("^another session holds the database's write lock$")>]
    member _.Hold() =
        task {
            let! release = w.Db.HoldWrites "customers"
            w.Set("release", release)
        }
        |> unit

    [<StepDefinition("^I insert a Customer \"(\\w+)\", and the other session lets go of the lock (\\d+) ms later$")>]
    member _.InsertThenRelease(name: string, after: int) =
        task {
            let release = w.Get<unit -> Task> "release"
            let watch = Stopwatch.StartNew()
            let inserting = insert w.Repo name
            do! Task.Delay after
            do! release ()
            let! r = inserting
            w.Set("writer", (r |> Result.map ignore, watch.Elapsed))
        }
        |> unit

    [<StepDefinition("^I insert a Customer \"(\\w+)\" through a repository whose busy timeout is (\\d+) ms, the other session holding the lock for (\\d+) seconds$")>]
    member _.InsertTimedOut(name: string, busy: int, held: int) =
        task {
            let release = w.Get<unit -> Task> "release"
            let provider = Sqlite.connectWith { SqliteOptions.Default with BusyTimeout = TimeSpan.FromMilliseconds(float busy) } w.Db.ConnectionString
            let repo = Repo(w.Db.Model, provider)
            let watch = Stopwatch.StartNew()
            let inserting = insert repo name

            try
                let! r = Task.WhenAny(inserting, Task.Delay(held * 1000))

                if not (obj.ReferenceEquals(r, inserting)) then
                    failwith $"the insert still waited after {held} seconds"

                let! outcome = inserting
                w.Set("writer", (outcome |> Result.map ignore, watch.Elapsed))
            finally
                (release ()).Wait()
        }
        |> unit

    [<StepDefinition("^a transaction retrying serialization failures (\\d+) times, with a step of its own, inserts a Customer \"(\\w+)\" through a repository whose busy timeout is (\\d+) ms, the other session holding the lock for (\\d+) seconds$")>]
    member _.RetryingTimedOut(attempts: int, name: string, busy: int, held: int) =
        task {
            let release = w.Get<unit -> Task> "release"
            let provider = Sqlite.connectWith { SqliteOptions.Default with BusyTimeout = TimeSpan.FromMilliseconds(float busy) } w.Db.ConnectionString
            let repo = Repo(w.Db.Model, provider)
            let mutable ran = 0

            let tx: Tx<unit, unit> =
                transaction {
                    do! Tx.run (fun _ -> task { ran <- ran + 1; return Ok() })
                    let! _ = Tx.insert (New.customer name $"{name.ToLowerInvariant()}@example.com")
                    return ()
                }

            let inserting = repo.Transact(tx, retry = RetryPolicy.OnSerializationFailure attempts)

            try
                let! r = Task.WhenAny(inserting, Task.Delay(held * 1000))

                if not (obj.ReferenceEquals(r, inserting)) then
                    failwith $"the transaction still waited after {held} seconds"

                let! outcome = inserting
                w.Set("writer", (outcome, TimeSpan.Zero))
                w.Set("ownStepRan", ran)
            finally
                (release ()).Wait()
        }
        |> unit

    [<StepDefinition("^the insert of \"\\w+\" fails as a timeout at its BEGIN, after (\\d+) attempts?$")>]
    member _.TimedOutAfter(attempts: int) =
        match w.Get<Result<unit, TransactionError<unit>> * TimeSpan> "writer" with
        | Error({ Failure = TxFailure.Failed(DbError.Database f); Step = step } as e), _ ->
            equal DbFailureKind.Timeout f.Kind "kind"
            equal "begin" step.Action "step"
            equal attempts e.Attempts "attempts"
        | other, _ -> failwithf "expected a timeout at begin, got %A" other

    [<StepDefinition("^the step of its own ran (\\d+) times$")>]
    member _.OwnStepRan(times: int) = equal times (w.Get<int> "ownStepRan") "runs of the step of its own"

    [<StepDefinition("^the insert of \"\\w+\" waited at its BEGIN for at least (\\d+) ms, and succeeded$")>]
    member _.Waited(least: int) =
        let r, elapsed = w.Get<Result<unit, TransactionError<unit>> * TimeSpan> "writer"
        ok r
        check (elapsed.TotalMilliseconds >= float least) $"it took {elapsed.TotalMilliseconds} ms"

    [<StepDefinition("^the insert of \"\\w+\" fails as a timeout at its BEGIN$")>]
    member _.TimedOut() =
        match w.Get<Result<unit, TransactionError<unit>> * TimeSpan> "writer" with
        | Error { Failure = TxFailure.Failed(DbError.Database f); Step = step }, _ ->
            equal DbFailureKind.Timeout f.Kind "kind"
            equal "begin" step.Action "step"
        | other, _ -> failwithf "expected a timeout at begin, got %A" other

    [<StepDefinition("^the Customer named \"(\\w+)\" is stored$")>]
    member _.Stored(name: string) =
        task {
            let! n = stored name
            equal 1L n "customers of the name"
        }
        |> unit

    [<StepDefinition("^no Customer named \"(\\w+)\" is stored$")>]
    member _.NotStored(name: string) =
        task {
            let! n = stored name
            equal 0L n "customers of the name"
        }
        |> unit
