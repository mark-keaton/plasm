namespace Plasm.Specs

open System
open System.Data.Common
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module CancellationSql =
    /// A count to a hundred million: many seconds on either database.
    [<Literal>]
    let Slow = "(with recursive n(i) as (select 1 union all select i + 1 from n where i < 100000000) select count(*) from n)"

    /// A count to ten million: about a second.
    [<Literal>]
    let TenMillion = "(with recursive n(i) as (select 1 union all select i + 1 from n where i < 10000000) select count(*) from n)"

    [<Literal>]
    let SlowCondition = "{0} <> '' and " + Slow + " > 0"

    /// Zed's row only after the count, every other at once.
    [<Literal>]
    let SlowForZed = "(case when {0} = 'Zed' then " + Slow + " else 1 end) > 0"

/// Feature: A cancelled token stops a statement the database is running
[<Binding>]
type CancellationSteps(w: World) =

    static let slow = CancellationSql.Slow

    /// What a call ended with, and how long it took.
    let outcome (call: CancellationToken -> Task) =
        task {
            use cts = new CancellationTokenSource(300)
            let watch = Stopwatch.StartNew()

            let! ended =
                task {
                    try
                        do! call cts.Token
                        return "completed"
                    with
                    | :? OperationCanceledException -> return "cancelled"
                    | e -> return $"{e.GetType().Name}: {e.Message}"
                }

            w.Set("ended", ended)
            w.Set("took", watch.Elapsed)
        }
        :> Task

    let cancelledWithin (seconds: float) =
        let ended = w.Get<string> "ended"
        let took = w.Get<TimeSpan> "took"
        check (ended = "cancelled") $"expected OperationCanceledException, the call ended {ended} after {took}"
        check (took < TimeSpan.FromSeconds seconds) $"cancelled, but only after {took}"

    /// A file in rollback-journal mode holding the customers table, its
    /// provider leaving the mode as it is, and another connection holding a
    /// lock on it until the scenario ends.
    let rollbackJournal (lockWith: string) =
        task {
            let file = IO.Path.Combine(sqliteDirectory, $"journal-{Guid.NewGuid():N}.db")
            w.Set("journalFile", file)

            let c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False")
            c.Open()
            let make = c.CreateCommand()
            make.CommandText <- "PRAGMA journal_mode = DELETE; create table customers (id INTEGER not null primary key, name TEXT not null, email TEXT not null); insert into customers (name, email) values ('Ada', 'ada@example.com')"
            make.ExecuteNonQuery() |> ignore
            make.Dispose()
            c.Dispose()

            let holder = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False")
            holder.Open()
            use hold = holder.CreateCommand()
            hold.CommandText <- lockWith
            hold.ExecuteNonQuery() |> ignore
            w.Set("holder", holder)

            let provider = Plasm.Sqlite.Sqlite.connectWith { Plasm.Sqlite.SqliteOptions.Default with JournalMode = Plasm.Sqlite.JournalMode.Keep } $"Data Source={file}"
            w.Set("journalRepo", Repo(Schemas.model, provider))
        }
        :> Task

    [<StepDefinition("I read the Customers through a condition that runs for seconds, cancelled at 300 ms")>]
    member _.SlowRead() =
        outcome (fun ct ->
            task {
                let! r = w.Repo.All(plasm { for c in tableOf<Customer> do where (Raw.fragment<bool> CancellationSql.SlowCondition c.Name) }, cancellationToken = ct)
                ok r |> ignore
            })

    [<AfterScenario>]
    member _.ReleaseJournal() =
        if w.Has "holder" then
            (w.Get<Microsoft.Data.Sqlite.SqliteConnection> "holder").Dispose()
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            for f in sqliteFiles (w.Get<string> "journalFile") do
                if IO.File.Exists f then IO.File.Delete f

    [<StepDefinition("a rollback-journal database that another connection holds for writing")>]
    member _.HeldForWriting() = rollbackJournal "BEGIN EXCLUSIVE"

    [<StepDefinition("a rollback-journal database that another connection is reading")>]
    member _.BeingRead() = rollbackJournal "BEGIN; select count(*) from customers"

    [<StepDefinition("I read its Customers, cancelled at 300 ms")>]
    member _.ReadJournal() =
        outcome (fun ct ->
            task {
                let! r = (w.Get<Repo> "journalRepo").All(plasm { for c in tableOf<Customer> do select c.Name }, cancellationToken = ct)
                ok r |> ignore
            })

    [<StepDefinition("a transaction inserts a Customer \"Cy\" into it, cancelled at 300 ms")>]
    member _.InsertJournal() =
        outcome (fun ct ->
            task {
                let! r = (w.Get<Repo> "journalRepo").Transact(Tx.insert (New.customer "Cy" "cy@example.com"), cancellationToken = ct)
                r |> Result.mapError string |> ok |> ignore
            })

    [<StepDefinition("it has no Customer \"Cy\"")>]
    member _.JournalHasNoCy() =
        (w.Get<Microsoft.Data.Sqlite.SqliteConnection> "holder").Dispose()
        use c = new Microsoft.Data.Sqlite.SqliteConnection($"""Data Source={w.Get<string> "journalFile"};Pooling=False""")
        c.Open()
        use cmd = c.CreateCommand()
        cmd.CommandText <- "select count(*) from customers where name = 'Cy'"
        equal 0L (Convert.ToInt64(cmd.ExecuteScalar())) "Customers named Cy"

    [<StepDefinition("the read raises OperationCanceledException within 2 seconds")>]
    [<StepDefinition("the transaction raises OperationCanceledException within 2 seconds")>]
    member _.CancelledSoon() = cancelledWithin 2.0

    [<StepDefinition("the read raises OperationCanceledException within 5 seconds")>]
    [<StepDefinition("asking for it raises OperationCanceledException within 5 seconds")>]
    [<StepDefinition("the transaction raises OperationCanceledException within 5 seconds")>]
    member _.CancelledInTime() = cancelledWithin 5.0

    [<StepDefinition("I stream the Customers, many quick to find and then Zed slow to find, read the first and keep asking for the next, cancelled at 300 ms")>]
    member _.SlowStream() =
        task {
            // More quick rows than PostgreSQL holds back before it sends any:
            // the first row arrives while Zed's is still being found.
            let! _ =
                w.Db.Exec(
                    "with recursive k(k) as (select 1 union all select k + 1 from k where k < 2000) insert into customers(name, email) select 'Customer number ' || k || ' of the quick ones', 'q' || k || '@example.com' from k"
                )

            let! _ = Seed.customer w.Db "Zed" "zed@example.com"

            let q =
                plasm {
                    for c in tableOf<Customer> do
                        // No order: a sort would find every row before the first.
                        where (Raw.fragment<bool> CancellationSql.SlowForZed c.Name)
                }

            use cts = new CancellationTokenSource()
            let rows = w.Repo.Stream(q, cancellationToken = cts.Token).GetAsyncEnumerator(CancellationToken.None)
            let! first = rows.MoveNextAsync()
            check first "a first row is read"
            cts.CancelAfter 300

            do!
                outcome (fun _ ->
                    task {
                        let mutable more = true

                        while more do
                            let! next = rows.MoveNextAsync()
                            more <- next
                    })

            do! rows.DisposeAsync()
        }
        :> Task

    [<StepDefinition("a transaction inserts a Customer \"Cy\" and then runs a statement that runs for seconds, cancelled at 300 ms")>]
    member _.SlowTransaction() =
        let slowStep =
            Tx.run (fun ctx ->
                task {
                    use cmd = ctx.CreateCommand()
                    cmd.CommandText <- "select " + slow
                    let! _ = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                    return Ok()
                })

        outcome (fun ct ->
            task {
                let! r =
                    w.Repo.Transact(
                        transaction {
                            let! _ = Tx.insert (New.customer "Cy" "cy@example.com")
                            do! slowStep
                        },
                        cancellationToken = ct
                    )

                r |> Result.mapError string |> ok |> ignore
            })

    [<StepDefinition("there is no Customer \"Cy\"")>]
    member _.NoCy() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from customers where name = 'Cy'")
            equal 0L n "Customers named Cy"
        }
        :> Task

    [<StepDefinition("a statement on a connection the provider opened is cancelled while it runs")>]
    member _.CancelledOnConnection() =
        task {
            let! conn = w.Db.Provider.OpenConnection CancellationToken.None
            w.Set("connection", conn)

            do!
                outcome (fun ct ->
                    task {
                        use cmd = conn.CreateCommand()
                        cmd.CommandText <- "select " + slow
                        let! _ = cmd.ExecuteScalarAsync ct
                        ()
                    })

            cancelledWithin 5.0
        }
        :> Task

    [<StepDefinition("a statement on a connection the provider opened finishes, and its token is cancelled while the next one runs")>]
    member _.FinishedThenCancelled() =
        task {
            use conn = w.Db.Provider.OpenConnection(CancellationToken.None).Result
            use cts = new CancellationTokenSource()

            let first = conn.CreateCommand()
            first.CommandText <- "select 1"
            let! _ = first.ExecuteScalarAsync(cts.Token)
            first.Dispose()

            // The next statement hears no token; the first's is cancelled while
            // it runs, and must not reach it.
            use next = conn.CreateCommand()
            next.CommandText <- "select " + CancellationSql.TenMillion
            cts.CancelAfter 300

            let! counted =
                task {
                    try
                        let! v = next.ExecuteScalarAsync CancellationToken.None
                        return string (Convert.ToInt64 v)
                    with e ->
                        return $"{e.GetType().Name}: {e.Message}"
                }

            w.Set("counted", counted)
        }
        :> Task

    [<StepDefinition("the next one runs to its end, counting to ten million")>]
    member _.Counted() = equal "10000000" (w.Get<string> "counted") "what the next statement gave"

    [<StepDefinition("the next statement on that connection gives 42")>]
    member _.Next() =
        task {
            use conn = w.Get<DbConnection> "connection"
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "select 41 + 1"
            let! v = cmd.ExecuteScalarAsync()
            equal 42L (Convert.ToInt64 v) "the next statement's value"
        }
        :> Task
