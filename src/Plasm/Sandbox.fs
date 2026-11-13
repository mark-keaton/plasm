/// Test sandboxes: a provider whose work happens on one connection inside one
/// transaction that is always rolled back (Ecto's SQL sandbox). Every operation
/// runs inside its own savepoint, so a database error does not abort the
/// sandbox, and transactions inside it become savepoints.
namespace Plasm

open System
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks

/// A savepoint presented as a transaction. Commit releases it; rollback (or
/// disposal without commit) rolls back to it.
[<Sealed; AllowNullLiteral>]
type internal SandboxTransaction(connection: DbConnection, inner: DbTransaction, name: string) =
    inherit DbTransaction()
    let mutable finished = false

    do inner.Save name

    member _.Inner = inner

    override _.DbConnection = connection
    override _.IsolationLevel = inner.IsolationLevel

    override _.Commit() =
        if not finished then
            finished <- true
            inner.Release name

    override _.Rollback() =
        if not finished then
            finished <- true
            inner.Rollback name

    // Named savepoints pass through, so a sandbox can run inside this one.
    override _.Save(savepoint: string) = inner.Save savepoint
    override _.Release(savepoint: string) = inner.Release savepoint
    override _.Rollback(savepoint: string) = inner.Rollback savepoint

    override this.Dispose(disposing: bool) =
        if disposing && not finished then
            try
                this.Rollback()
            with _ ->
                ()

        base.Dispose disposing

/// The sandbox connection handed to each operation. Disposing it ends the
/// operation (releasing its savepoint, or rolling back to it after an error)
/// and lets the next operation run. The real connection stays open.
[<Sealed>]
type internal SandboxConnection(inner: DbConnection, onDispose: unit -> unit) =
    inherit DbConnection()
    let mutable disposed = false

    member _.Inner = inner

    override _.ConnectionString
        with get () = inner.ConnectionString
        and set (_: string) = invalidOp "a sandbox connection cannot be reconfigured"

    override _.Database = inner.Database
    override _.DataSource = inner.DataSource
    override _.ServerVersion = inner.ServerVersion
    override _.State = inner.State
    override _.ChangeDatabase(_: string) = invalidOp "a sandbox connection cannot change database"
    override _.Open() = ()
    override _.Close() = ()

    override _.BeginDbTransaction(_: IsolationLevel) : DbTransaction =
        invalidOp "begin transactions in a sandbox through the repo (repo.Transact)"

    override _.CreateDbCommand() = inner.CreateCommand()

    override _.Dispose(disposing: bool) =
        if disposing && not disposed then
            disposed <- true
            onDispose ()

        base.Dispose disposing

type internal SandboxProvider(inner: IProvider, connection: DbConnection, transaction: DbTransaction) =
    // Operations share one connection, so they run one at a time.
    let gate = new SemaphoreSlim(1, 1)
    let mutable counter = 0
    let mutable ended = false

    let savepoint () =
        $"plasm_sandbox_{Interlocked.Increment(&counter)}"

    /// Called when the sandbox ends; later operations fail as closed.
    member _.End() = ended <- true

    interface IProvider with
        member _.Dialect = inner.Dialect
        member _.Classify ex = Closed.failure ex |> Option.orElse (inner.Classify ex)

        member _.OpenConnection(ct: CancellationToken) : Task<DbConnection> =
            task {
                if ended then
                    raise (RepositoryClosedException "This sandbox has ended and its changes were rolled back; its repository can no longer be used.")

                let! entered = gate.WaitAsync(TimeSpan.FromSeconds 30.0, ct)

                if not entered then
                    invalidOp
                        "a sandbox operation waited 30 s for another one on the same sandbox; operations are serialised, so a repo call made while another is running (for example inside a Tx.run step) cannot proceed. Use the step's context instead"

                let name = savepoint ()

                try
                    transaction.Save name
                with ex ->
                    gate.Release() |> ignore
                    let (): unit = Err.rethrow ex
                    ()

                let finish () =
                    try
                        try
                            transaction.Release name
                        with _ ->
                            // The operation failed and aborted the transaction:
                            // return to the state before it.
                            transaction.Rollback name
                    finally
                        gate.Release() |> ignore

                return new SandboxConnection(connection, finish) :> DbConnection
            }

        member _.BeginTransaction(conn: DbConnection, _: IsolationLevel, _: CancellationToken) : Task<DbTransaction> =
            Task.FromResult(new SandboxTransaction(conn, transaction, savepoint ()) :> DbTransaction)

        member _.BeginSnapshot(conn: DbConnection, _: CancellationToken) : Task<DbTransaction option> =
            Task.FromResult(Some(new SandboxTransaction(conn, transaction, savepoint ()) :> DbTransaction))

module internal Sandboxes =
    /// The real transaction for a command (sandbox transactions are savepoints
    /// of the sandbox's transaction).
    let rec unwrap (t: DbTransaction) =
        match t with
        | :? SandboxTransaction as s -> unwrap s.Inner
        | t -> t
