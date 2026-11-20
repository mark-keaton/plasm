/// The commands that begin and end a transaction, reported to
/// `RepoOptions.OnCommand` beside the statements (transaction-events.feature):
/// BEGIN with its isolation level, COMMIT, ROLLBACK, and savepoints, in the
/// text the database logs. A repository with an OnCommand begins every
/// transaction through `ReportingProvider`, whose transactions report each of
/// these as it is sent, so a sandbox's and a playground's savepoints are
/// reported by the transaction they are taken in.
namespace Plasm

open System
open System.Data
open System.Data.Common
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

module internal RoundTrips =
    let mutable private count = 0L

    /// A new round trip's number, which every command sent in it reports.
    let next () = Interlocked.Increment(&count)

/// A BEGIN or SAVEPOINT the provider has not sent yet: it travels with
/// whatever the connection sends next (ProviderCapabilities.DefersBegin).
type internal Waiting = { Kind: CommandKind; Sql: string }

module internal Waiting =
    let private waiting = ConditionalWeakTable<DbConnection, ResizeArray<Waiting>>()

    /// The connection a command is really sent on: a sandbox hands each
    /// operation a wrapper of its one connection.
    let real (c: DbConnection) =
        match c with
        | :? SandboxConnection as s -> s.Inner
        | c -> c

    let add (connection: DbConnection) (w: Waiting) =
        let list = waiting.GetValue(real connection, fun _ -> ResizeArray())
        lock list (fun () -> list.Add w)

    /// Reports what waited on this connection as sent in `roundTrip`, before
    /// the command that carried it.
    let flush (onCommand: CommandEvent -> unit) (connection: DbConnection) (roundTrip: int64) (duration: TimeSpan) =
        match waiting.TryGetValue(real connection) with
        | true, list ->
            let sent =
                lock list (fun () ->
                    let a = list.ToArray()
                    list.Clear()
                    a)

            for w in sent do
                onCommand
                    { Kind = w.Kind
                      Sql = w.Sql
                      Parameters = []
                      Duration = duration
                      Rows = 0
                      Succeeded = true
                      Error = None
                      RoundTrip = roundTrip }
        | _ -> ()

/// Reports a transaction's commands to a repository's OnCommand and
/// activities, while either wants them (Activities.sink).
type internal TxReporter(sink: unit -> (CommandEvent -> unit) option, defers: bool) =

    let report connection kind sql trip (elapsed: TimeSpan) ok =
        match sink () with
        | None -> ()
        | Some onCommand ->
            Waiting.flush onCommand connection trip elapsed

            onCommand
                { Kind = kind
                  Sql = sql
                  Parameters = []
                  Duration = elapsed
                  Rows = 0
                  Succeeded = ok
                  Error = if ok then None else Some "_OTHER"
                  RoundTrip = trip }

    let wait connection (w: Waiting) =
        if (sink ()).IsSome then Waiting.add connection w

    /// A command sent at once, in a round trip of its own.
    member _.Sent(connection: DbConnection, kind, sql, send: unit -> unit) =
        let trip = RoundTrips.next ()
        let clock = Stopwatch.StartNew()
        let mutable ok = false

        try
            send ()
            ok <- true
        finally
            report connection kind sql trip clock.Elapsed ok

    /// As Sent; `sql`, read once `send` has finished, is None when nothing
    /// was sent.
    member _.SentAsync(connection: DbConnection, kind, sql: unit -> string option, send: unit -> Task) : Task =
        task {
            let trip = RoundTrips.next ()
            let clock = Stopwatch.StartNew()
            let mutable ok = false

            try
                do! send ()
                ok <- true
            finally
                match sql () with
                | Some text -> report connection kind text trip clock.Elapsed ok
                | None -> ()
        }

    /// BEGIN or SAVEPOINT: sent with what follows when the provider defers it,
    /// else at once.
    member this.Began(connection: DbConnection, sql, send: unit -> unit) =
        if defers then
            send ()
            wait connection { Kind = CommandKind.Begin; Sql = sql }
        else
            this.Sent(connection, CommandKind.Begin, sql, send)

    member this.BeganAsync(connection: DbConnection, sql: unit -> string option, send: unit -> Task) : Task =
        if defers then
            task {
                do! send ()

                match sql () with
                | Some text -> wait connection { Kind = CommandKind.Begin; Sql = text }
                | None -> ()
            }
        else
            this.SentAsync(connection, CommandKind.Begin, sql, send)

/// A provider's transaction that reports what it sends. Commands are sent on
/// the transaction it wraps (Sandboxes.unwrap).
[<Sealed; AllowNullLiteral>]
type internal ReportedTransaction(inner: DbTransaction, connection: DbConnection, reporter: TxReporter) =
    inherit DbTransaction()
    let mutable finished = false

    member _.Inner = inner

    override _.DbConnection = connection
    override _.IsolationLevel = inner.IsolationLevel
    override _.SupportsSavepoints = inner.SupportsSavepoints

    override _.Commit() =
        finished <- true
        reporter.Sent(connection, CommandKind.Commit, "COMMIT", inner.Commit)

    override _.CommitAsync(ct: CancellationToken) =
        finished <- true
        reporter.SentAsync(connection, CommandKind.Commit, (fun () -> Some "COMMIT"), fun () -> inner.CommitAsync ct)

    override _.Rollback() =
        finished <- true
        reporter.Sent(connection, CommandKind.Rollback, "ROLLBACK", inner.Rollback)

    override _.RollbackAsync(ct: CancellationToken) =
        finished <- true
        reporter.SentAsync(connection, CommandKind.Rollback, (fun () -> Some "ROLLBACK"), fun () -> inner.RollbackAsync ct)

    // The asynchronous savepoint methods of DbTransaction call these.
    override _.Save(name: string) =
        reporter.Began(connection, $"SAVEPOINT {name}", fun () -> inner.Save name)

    override _.Release(name: string) =
        reporter.Sent(connection, CommandKind.Commit, $"RELEASE SAVEPOINT {name}", fun () -> inner.Release name)

    override _.Rollback(name: string) =
        reporter.Sent(connection, CommandKind.Rollback, $"ROLLBACK TO SAVEPOINT {name}", fun () -> inner.Rollback name)

    // Disposing a transaction that was neither committed nor rolled back rolls
    // it back, on a connection that is still open.
    override _.Dispose(disposing: bool) =
        if disposing then
            if not finished && connection.State = ConnectionState.Open then
                finished <- true
                reporter.Sent(connection, CommandKind.Rollback, "ROLLBACK", inner.Dispose)
            else
                finished <- true
                inner.Dispose()

        base.Dispose disposing

/// A provider whose transactions report their commands to `onCommand`.
[<Sealed>]
type internal ReportingProvider(inner: IProvider, onCommand: (CommandEvent -> unit) option) =
    let reporter = TxReporter((fun () -> Activities.sink onCommand inner.Dialect), inner.Dialect.Capabilities.DefersBegin)

    /// Begins with `start`, reporting the BEGIN at the level the transaction
    /// has (or `requested`, when beginning failed). None began nothing.
    let begun (connection: DbConnection) (requested: IsolationLevel) (start: unit -> Task<DbTransaction option>) =
        task {
            let mutable began = None
            let mutable failed = true

            let sql () =
                match began with
                | Some(t: DbTransaction) -> Some(inner.Dialect.Begin t.IsolationLevel)
                | None when failed -> Some(inner.Dialect.Begin requested)
                | None -> None

            do!
                reporter.BeganAsync(
                    connection,
                    sql,
                    fun () ->
                        task {
                            let! t = start ()
                            began <- t
                            failed <- false
                        }
                )

            return began |> Option.map (fun t -> new ReportedTransaction(t, connection, reporter) :> DbTransaction)
        }

    interface IReportsTransactions

    interface IProvider with
        member _.Dialect = inner.Dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex

        member _.BeginTransaction(connection, isolation, ct) =
            task {
                let! began =
                    begun connection isolation (fun () ->
                        task {
                            let! t = inner.BeginTransaction(connection, isolation, ct)
                            return Some t
                        })

                return began.Value
            }

        // A snapshot is REPEATABLE READ in both providers; the level reported
        // is the one the transaction has.
        member _.BeginSnapshot(connection, ct) =
            begun connection IsolationLevel.RepeatableRead (fun () -> inner.BeginSnapshot(connection, ct))

module internal Reporting =
    /// The provider a repository begins its transactions through: one that
    /// reports them when there is an OnCommand. A sandbox's and a playground's
    /// provider take their savepoints in a transaction that already reports.
    let wrap (onCommand: (CommandEvent -> unit) option) (provider: IProvider) =
        // Always: what it reports is decided per command, by whether an
        // OnCommand or an activity listener wants it (activities.feature).
        match provider with
        | :? IReportsTransactions -> provider
        | p -> ReportingProvider(p, onCommand) :> IProvider

module internal Sandboxes =
    /// The provider's own transaction, on which commands are sent: a sandbox's
    /// transactions are savepoints of the sandbox's transaction, and a
    /// reported transaction wraps the provider's.
    let rec unwrap (t: DbTransaction) =
        match t with
        | :? SandboxTransaction as s -> unwrap s.Inner
        | :? ReportedTransaction as r -> unwrap r.Inner
        | t -> t
