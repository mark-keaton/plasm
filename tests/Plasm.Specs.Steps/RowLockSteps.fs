namespace Plasm.Specs.RowLocks

open System
open System.Threading.Tasks
open Plasm

/// row-locks.feature: a job queue, each job with notes it owns.
type JobId = JobId of int64
type JobNoteId = JobNoteId of int64

[<Table "jobs">]
type Job =
    { Id: JobId
      Kind: string
      Queued: bool
      Notes: JobNote list }

and [<Table "job_notes">] JobNote = { Id: JobNoteId; Job: JobId; Text: string }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Job> ]

    let tables =
        "create table jobs (id bigint generated always as identity primary key, kind text not null, queued boolean not null);
         create table job_notes (id bigint generated always as identity primary key, job bigint not null references jobs (id), text text not null);"

    /// The first queued job, locked as `lock` says.
    let firstQueued (lock: RowLock) =
        plasm {
            for j in tableOf<Job> do
                where j.Queued
                orderBy j.Id
                take 1
                locking lock
        }

/// Locked queries that cannot be: checked by name (row-locks.feature).
module LocksBad =
    let lockedGroups =
        plasm {
            for j in tableOf<Job> do
                groupBy j.Kind
                locking RowLock.ForUpdate
        }

namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs.RowLocks
open Plasm.TestDomain

/// A provider like the database's, whose dialect takes no row locks; with
/// `turns`, its writers take turns (one at a time, as SQLite's do).
type NoRowLocks(inner: IProvider, turns: bool) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "NoLocksSQL"
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
            member _.UnboundedLimit = inner.Dialect.UnboundedLimit
            member _.NullSafeComparison negated = inner.Dialect.NullSafeComparison negated
            member _.LikeEscape = inner.Dialect.LikeEscape
            member _.FunctionName name = inner.Dialect.FunctionName name
            member _.Json = inner.Dialect.Json
            member _.Begin isolation = inner.Dialect.Begin isolation
            member _.Capabilities = { inner.Dialect.Capabilities with RowLocking = false; WritersTakeTurns = turns } }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

[<Binding>]
type RowLockSteps(w: World) =

    let repo () = w.Get<Repo> "jobsRepo"

    let jobOf (r: Result<Job option, TransactionError<string>>) =
        match r with
        | Ok(Some j) -> j.Id
        | other -> failwithf "expected a job, got %A" other

    /// The first worker claims with `first` and holds its transaction open
    /// while the others claim with `others`, one after another; the first's
    /// outcome, then the others'.
    let workers (first: RowLock) (others: RowLock list) =
        task {
            let claimed = TaskCompletionSource()
            let secondDone = TaskCompletionSource()

            let holding: Tx<Job option, string> =
                transaction {
                    let! job = Tx.one (Schemas.firstQueued first)

                    let! _ =
                        Tx.run (fun _ ->
                            task {
                                claimed.SetResult()
                                do! secondDone.Task.WaitAsync(TimeSpan.FromSeconds 20.0)
                                return (Ok(): Result<unit, string>)
                            })

                    return job
                }

            let a = repo().Transact holding
            do! claimed.Task.WaitAsync(TimeSpan.FromSeconds 20.0)

            let! bs =
                task {
                    try
                        let results = ResizeArray()

                        for lock in others do
                            let! r = repo().Transact(Tx.one (Schemas.firstQueued lock): Tx<Job option, string>)
                            results.Add r

                        return List.ofSeq results
                    finally
                        secondDone.SetResult()
                }

            let! a = a
            return a, bs
        }

    let twoWorkers first second =
        task {
            let! a, bs = workers first [ second ]
            return a, List.exactlyOne bs
        }

    let notAvailable (r: Result<Job option, TransactionError<string>>) =
        match r with
        | Error { Failure = TxFailure.Failed(DbError.Database f) } -> equal DbFailureKind.LockNotAvailable f.Kind "the failure's kind"
        | other -> failwithf "expected a lock that is not available, got %A" other

    [<StepDefinition("jobs with their notes")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec Schemas.tables
            w.Set("jobsRepo", w.Db.RepoWith(model = Schemas.model))
        }
        :> Task

    [<StepDefinition("three queued jobs")>]
    member _.Jobs() =
        w.Db.Exec
            "insert into jobs (kind, queued) values ('mail', true), ('mail', true), ('report', true);
             insert into job_notes (job, text) values (1, 'first try'), (1, 'second try');"
        |> unit

    [<StepDefinition("two workers each claim the first queued job, skipping locked ones, the first holding its lock until the second has claimed")>]
    member _.SkipLocked() =
        task {
            let! a, b = twoWorkers RowLock.ForUpdate.SkipLocked RowLock.ForUpdate.SkipLocked
            w.Set("claims", (jobOf a, jobOf b))
        }
        :> Task

    [<StepDefinition("the workers claimed different jobs")>]
    member _.Different() =
        let a, b = w.Get<JobId * JobId> "claims"
        check (a <> b) $"both workers claimed {a}"

    [<StepDefinition("a worker holds the first queued job and a second claims it without waiting")>]
    member _.NoWait() =
        task {
            let! a, b = twoWorkers RowLock.ForUpdate RowLock.ForUpdate.NoWait
            jobOf a |> ignore
            w.Set("second", b)
        }
        :> Task

    [<StepDefinition("the second's transaction fails as a lock that is not available")>]
    member _.NotAvailable() =
        notAvailable (w.Get<Result<Job option, TransactionError<string>>> "second")

    [<StepDefinition("a worker holds the first queued job with a shared lock, a second takes a shared lock on it without waiting, and a third locks it for update without waiting")>]
    member _.Shared() =
        task {
            let! a, bs = workers RowLock.ForShare [ RowLock.ForShare.NoWait; RowLock.ForUpdate.NoWait ]
            w.Set("claims", (jobOf a, jobOf bs.[0]))
            w.Set("third", bs.[1])
        }
        :> Task

    [<StepDefinition("the first two read the job")>]
    member _.Both() =
        let a, b = w.Get<JobId * JobId> "claims"
        equal a b "the job each worker read"

    [<StepDefinition("the third's transaction fails as a lock that is not available")>]
    member _.ThirdRefused() =
        notAvailable (w.Get<Result<Job option, TransactionError<string>>> "third")

    [<StepDefinition("a transaction reads the first queued job locked for update, with its notes")>]
    member _.LockedWithNotes() =
        task {
            w.MarkCommands()
            let! r = repo().Transact(Tx.one (Schemas.firstQueued RowLock.ForUpdate): Tx<Job option, string>)
            equal 2 (ok r).Value.Notes.Length "notes read with the job"
        }
        :> Task

    [<StepDefinition("^the job's statement ends with (.*)$")>]
    member _.Ends(text: string) =
        let jobs = w.CommandsSinceMark |> List.find (fun c -> c.Sql.Contains "FROM \"jobs\"")
        check (jobs.Sql.TrimEnd().EndsWith text) jobs.Sql

    [<StepDefinition("the statement that reads its notes takes no lock")>]
    member _.NotesUnlocked() =
        let notes = w.CommandsSinceMark |> List.find (fun c -> c.Sql.Contains "FROM \"job_notes\"")
        check (not (notes.Sql.Contains "FOR ")) notes.Sql

    [<StepDefinition("I compile code that reads a locked query with repo.All")>]
    member _.CompileOutside() =
        task {
            let! r =
                CompileCheck.check
                    """
let claim = plasm { for c in tableOf<Customer> do locking RowLock.ForUpdate }
let read (repo: Repo) = repo.All claim
"""

            w.Set("lockCompile", r)
        }
        :> Task

    [<StepDefinition("the locked read does not compile")>]
    member _.DoesNotCompile() =
        let r = w.Get<CompileCheck.CompileResult> "lockCompile"
        check (not r.Compiles) "a locked query read outside a transaction compiled"

    [<StepDefinition("I check the locked queries that lock grouped rows")>]
    member _.CheckBad() =
        w.Set("lockProblems", repo().CheckQueries(typeof<RowLockSteps>.Assembly, "Plasm.Specs.RowLocks.LocksBad"))

    [<StepDefinition("^the problem is reported for \"(.*)\", saying \"(.*)\"$")>]
    member _.Problem(name: string, saying: string) =
        match w.Get<QueryProblem list> "lockProblems" with
        | [ p ] ->
            check (p.Query.EndsWith name) $"the problem is for {p.Query}"
            check ((string p.Error).Contains saying) $"the problem says {p.Error}"
        | other -> failwithf "expected one problem, got %A" other

    [<StepDefinition("a transaction reads the first queued job locked for update, through a provider without row locks")>]
    member _.NoLocks() =
        task {
            let unlocked = Repo(Schemas.model, NoRowLocks(w.Db.Provider, false), RepoOptions.Default)
            let! r = unlocked.Transact(Tx.one (Schemas.firstQueued RowLock.ForUpdate): Tx<Job option, string>)
            w.Set("unlocked", r)
        }
        :> Task

    [<StepDefinition("the first job is deleted through a provider without row locks, whose writers take turns")>]
    member _.DeleteTakingTurns() =
        task {
            let sent = ResizeArray<CommandEvent>()
            let turns = Repo(Schemas.model, NoRowLocks(w.Db.Provider, true), { RepoOptions.Default with OnCommand = Some sent.Add })
            let! read = turns.One(plasm { for j in tableOf<Job> do where (j.Id = JobId 1L) })
            sent.Clear()
            let! r = turns.Transact(Tx.delete (ok read).Value: Tx<Job, string>)
            w.Set("deleted", r |> Result.map ignore |> Result.mapError string)
            w.Set("replaceSent", sent |> Seq.map (fun e -> e.Sql) |> List.ofSeq)
        }
        :> Task

    [<StepDefinition("the first job and its notes are deleted")>]
    member _.Deleted() =
        task {
            equal (Ok()) (w.Get<Result<unit, string>> "deleted") "the delete"
            let! jobs = w.Db.Scalar<int64>("select count(*) from jobs where id = 1")
            let! notes = w.Db.Scalar<int64>("select count(*) from job_notes where job = 1")
            equal (0L, 0L) (jobs, notes) "the first job and its notes"
        }
        :> Task

    [<StepDefinition("no statement sent locks a row")>]
    member _.NoLock() =
        let sent = w.Get<string list> "replaceSent"
        check (not sent.IsEmpty && sent |> List.forall (fun s -> not (s.Contains "FOR UPDATE"))) (String.concat "\n---\n" sent)

    [<StepDefinition("the read is refused as unsupported by that provider")>]
    member _.Unsupported() =
        match w.Get<Result<Job option, TransactionError<string>>> "unlocked" with
        | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(_, provider))) } ->
            equal "NoLocksSQL" provider "the provider named"
        | other -> failwithf "expected a refusal by the provider, got %A" other
