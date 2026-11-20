namespace Plasm

open System
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks

/// A playground's provider: one connection and one transaction, opened on first
/// use and kept across operations (and F# Interactive submissions) until the
/// playground is reset or closed, then rolled back. Operations inside it behave
/// as in a sandbox: each runs in a savepoint, and transactions become savepoints.
type internal PlaygroundProvider(inner: IProvider) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable current: (DbConnection * DbTransaction * IProvider) option = None
    let mutable closed = false

    let active (ct: CancellationToken) : Task<IProvider> =
        task {
            do! gate.WaitAsync ct

            try
                if closed then
                    return
                        raise (
                            RepositoryClosedException
                                "This playground was closed and its changes were rolled back. Open a new one with repo.Playground()."
                        )
                else
                    match current with
                    | Some(_, _, p) -> return p
                    | None ->
                        let! conn = inner.OpenConnection ct
                        let! tx = inner.BeginTransaction(conn, IsolationLevel.ReadCommitted, ct)
                        let p = SandboxProvider(inner, conn, tx) :> IProvider
                        current <- Some(conn, tx, p)
                        return p
            finally
                gate.Release() |> ignore
        }

    let quietly (f: unit -> unit) =
        try
            f ()
        with _ ->
            ()

    /// Rolls back everything done so far; the next operation starts afresh.
    member _.Discard() =
        gate.Wait()

        try
            match current with
            | Some(conn, tx, _) ->
                current <- None
                quietly tx.Rollback
                quietly tx.Dispose
                quietly conn.Dispose
            | None -> ()
        finally
            gate.Release() |> ignore

    /// True when the playground holds a connection that is no longer open.
    member _.Broken =
        match current with
        | Some(conn, _, _) -> conn.State <> ConnectionState.Open
        | None -> false

    member p.Close() =
        p.Discard()
        closed <- true

    // Its savepoints are taken in a transaction its repository's provider
    // began, which reports them.
    interface IReportsTransactions

    interface IProvider with
        member _.Dialect = inner.Dialect
        member p.Classify ex =
            // A lost connection can surface wrapped (e.g. the transaction
            // reports itself disposed, with the server's error inside).
            let rec classify (e: exn) =
                match inner.Classify e with
                | Some f -> Some f
                | None when not (isNull e.InnerException) -> classify e.InnerException
                | None -> None

            match Closed.failure ex with
            | Some f -> Some f
            | None ->
                let classified =
                    match classify ex with
                    | None when p.Broken ->
                        Some
                            { Kind = DbFailureKind.ConnectionFailure
                              SqlState = None
                              Constraint = None
                              Message = ex.Message
                              Exception = ex }
                    | other -> other

                match classified with
                | Some f when f.Kind = DbFailureKind.ConnectionFailure || p.Broken ->
                    // The transaction died with its connection: say so, and
                    // start afresh on the next operation.
                    p.Discard()

                    Some
                        { f with
                            Kind = DbFailureKind.ConnectionFailure
                            Message =
                                $"The playground's connection was lost, so its changes were discarded; the next operation starts afresh. ({f.Message})" }
                | other -> other

        member _.OpenConnection ct =
            task {
                let! p = active ct
                return! p.OpenConnection ct
            }

        member _.BeginTransaction(conn, isolation, ct) =
            task {
                let! p = active ct
                return! p.BeginTransaction(conn, isolation, ct)
            }

        member _.BeginSnapshot(conn, ct) =
            task {
                let! p = active ct
                return! p.BeginSnapshot(conn, ct)
            }

/// A repository for experiments in F# Interactive (create one with
/// `repo.Playground()`): its changes stay visible across submissions but are
/// never committed. `Reset` rolls them back and keeps the playground open;
/// `Close` (or disposing it) rolls them back and ends it. A database error
/// does not end it, and its transactions become savepoints, as in a sandbox.
/// It holds one connection while it has changes.
[<Sealed>]
type Playground internal (model: Model, provider: PlaygroundProvider, options: RepoOptions) =
    inherit Repo(model, provider, options)

    /// Rolls back every change made in the playground; it stays open.
    member _.Reset() = provider.Discard()

    /// Rolls back every change made in the playground and ends it.
    member _.Close() = provider.Close()

    interface IDisposable with
        member _.Dispose() = provider.Close()

[<AutoOpen>]
module PlaygroundExtensions =
    type Repo with
        /// A repository whose changes are kept across F# Interactive
        /// submissions and never committed (see `Playground`).
        member repo.Playground() =
            new Playground(repo.Model, PlaygroundProvider(repo.Reporting), repo.Options)

/// Helpers for F# Interactive and scripts: `open Plasm.Interactive`.
module Interactive =

    /// Waits for a repository call and returns its result, so F# Interactive
    /// displays the result rather than a task: `repo.All query |> wait`.
    let wait (work: Task<'T>) : 'T = work.GetAwaiter().GetResult()
