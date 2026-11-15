namespace Plasm

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks

/// Where a transaction step is written, and what it does: Plasm finds a step
/// by its file and line, so a step needs no name (typed-transactions.feature).
type TxStep =
    { File: string
      Line: int
      /// "insert Customer", "all Order", "count Customer", "run", "commit", ...
      Action: string }

    override s.ToString() =
        if s.Line = 0 then s.Action else $"{IO.Path.GetFileName s.File}:{s.Line} ({s.Action})"

/// Why a transaction failed. `Rejected` carries the transaction's own reason,
/// of its error type 'E; the other cases are Plasm's. Cases are qualified
/// (`TxFailure.Rejected`) so they never collide with domain unions.
[<RequireQualifiedAccess>]
type TxFailure<'E> =
    /// `Tx.fail`, `Tx.orReject`, `Tx.ofResult`, or a `Tx.run` step returning Error.
    | Rejected of 'E
    /// Validation or declared-constraint errors of the step's changeset.
    | InvalidChanges of entity: string * errors: FieldError list
    /// Optimistic concurrency conflict (or the row no longer exists).
    | Conflict of entity: string
    /// The step could not be translated, the provider could not perform it,
    /// the database failed, or a row could not be read back.
    | Failed of DbError
    /// A `Tx.run` step's command tried to control the transaction.
    | Misused of message: string

/// A failed transaction. Every write in it was rolled back.
[<StructuredFormatDisplay("{Display}")>]
type TransactionError<'E> =
    { /// The step that failed (its Action is "commit" if the commit itself failed).
      Step: TxStep
      Failure: TxFailure<'E>
      /// Steps that had completed before the failure; their effects were rolled back.
      RolledBack: TxStep list
      /// Attempts made (more than one only with a retry policy).
      Attempts: int }

    /// What failed, in words, then where (F# Interactive and %A show this):
    /// `Customer: Email has already been taken (customers_email_key), at Shop.fs:12 (insert Customer)`.
    member e.Display =
        let reason =
            match e.Failure with
            | TxFailure.Rejected r -> $"rejected: %A{r}"
            | TxFailure.InvalidChanges(entity, errors) ->
                let named (f: FieldError) =
                    match f.Kind with
                    | FieldErrorKind.UniqueConstraint n
                    | FieldErrorKind.ForeignKeyConstraint n
                    | FieldErrorKind.CheckConstraint n -> $"{f.Field} {f.Message} ({n})"
                    | _ -> $"{f.Field} {f.Message}"

                let all = errors |> List.map named |> String.concat "; "
                $"{entity}: {all}"
            | TxFailure.Conflict entity -> $"{entity}: changed or deleted by another writer since it was read"
            | TxFailure.Failed d -> $"failed: {d}"
            | TxFailure.Misused m -> $"misused: {m}"

        let rolledBack = if e.RolledBack.IsEmpty then "" else $"; rolled back {e.RolledBack.Length} earlier step(s)"
        $"{reason}, at {e.Step}{rolledBack}"

    override e.ToString() = e.Display

/// Raised when a `Tx.run` step's command would end or nest the transaction.
exception internal TransactionControlException of string with
    override e.Message = e.Data0

/// A command that refuses transaction-control statements (COMMIT, ROLLBACK,
/// END, BEGIN, START, ABORT, SAVEPOINT, RELEASE) before they reach the server.
[<Sealed; AllowNullLiteral>]
type internal GuardedCommand(inner: DbCommand) =
    inherit DbCommand()

    static let comments = Text.RegularExpressions.Regex(@"/\*.*?\*/|--[^\n]*", Text.RegularExpressions.RegexOptions.Singleline)

    static let control =
        set [ "COMMIT"; "ROLLBACK"; "END"; "BEGIN"; "START"; "ABORT"; "SAVEPOINT"; "RELEASE" ]

    let check () =
        let text = comments.Replace(inner.CommandText, " ")

        for statement in text.Split(';') do
            let first =
                statement.Trim().Split([| ' '; '\t'; '\r'; '\n'; '(' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryHead
                |> Option.map (fun w -> w.ToUpperInvariant())

            match first with
            | Some w when control.Contains w ->
                raise (
                    TransactionControlException
                        $"a Tx.run step cannot control the transaction ({w}); the transaction is committed or rolled back by Repo.Transact"
                )
            | _ -> ()

    override _.CommandText
        with get () = inner.CommandText
        and set v = inner.CommandText <- v

    override _.CommandTimeout
        with get () = inner.CommandTimeout
        and set v = inner.CommandTimeout <- v

    override _.CommandType
        with get () = inner.CommandType
        and set v = inner.CommandType <- v

    override _.DesignTimeVisible
        with get () = inner.DesignTimeVisible
        and set v = inner.DesignTimeVisible <- v

    override _.UpdatedRowSource
        with get () = inner.UpdatedRowSource
        and set v = inner.UpdatedRowSource <- v

    override _.DbConnection
        with get () = inner.Connection
        and set _ = invalidOp "a Tx.run step's command stays on the transaction's connection"

    override _.DbTransaction
        with get () = inner.Transaction
        and set _ = invalidOp "a Tx.run step's command stays in the transaction"

    override _.DbParameterCollection = inner.Parameters
    override _.Cancel() = inner.Cancel()
    override _.CreateDbParameter() = inner.CreateParameter()
    override _.Prepare() = inner.Prepare()

    override _.ExecuteNonQuery() =
        check ()
        inner.ExecuteNonQuery()

    override _.ExecuteScalar() =
        check ()
        inner.ExecuteScalar()

    override _.ExecuteDbDataReader(behavior) =
        check ()
        inner.ExecuteReader(behavior)

    override _.ExecuteNonQueryAsync(ct) =
        check ()
        inner.ExecuteNonQueryAsync(ct)

    override _.ExecuteScalarAsync(ct) =
        check ()
        inner.ExecuteScalarAsync(ct)

    override _.ExecuteDbDataReaderAsync(behavior, ct) =
        check ()
        inner.ExecuteReaderAsync(behavior, ct)

    override _.Dispose(disposing) =
        if disposing then inner.Dispose()
        base.Dispose disposing

/// What a `Tx.run` step may use: commands that run inside the transaction.
/// Only database work belongs here: anything else cannot be rolled back. The
/// transaction itself is not exposed, and a command that would end or nest it
/// (COMMIT, ROLLBACK, BEGIN, ...) is refused before it is sent.
[<Sealed>]
type TxContext internal (connection: DbConnection, transaction: DbTransaction, ct: CancellationToken) =
    /// A new command on the transaction's connection, enlisted in the transaction.
    member _.CreateCommand() : DbCommand =
        let cmd = connection.CreateCommand()
        cmd.Transaction <- transaction
        new GuardedCommand(cmd) :> DbCommand

    member _.CancellationToken = ct

type internal TxRuntime =
    { Session: Execution.Session
      Model: Model
      Completed: ResizeArray<TxStep> }

/// A description of database work to run atomically, giving a 'T or failing
/// with a rejection of type 'E (see TxFailure). Building one performs no
/// database access; run it with `Repo.Transact`.
type Tx<'T, 'E> =
    internal
    | Pure of 'T
    | Effect of (TxRuntime -> Task<Result<'T, TxStep * TxFailure<'E>>>)

/// Retry behaviour for transactions. The default is no retries: application
/// steps are never re-run silently.
type RetryPolicy =
    { MaxAttempts: int
      ShouldRetry: DbFailure -> bool }

    static member None =
        { MaxAttempts = 1
          ShouldRetry = fun _ -> false }

    /// Retries serialization failures and deadlocks, re-running the whole
    /// description (including its reads) in a fresh transaction.
    static member OnSerializationFailure(maxAttempts: int) =
        { MaxAttempts = maxAttempts
          ShouldRetry =
            fun f ->
                match f.Kind with
                | DbFailureKind.SerializationFailure
                | DbFailureKind.Deadlock -> true
                | _ -> false }

/// The internals shared by the steps.
module internal TxCore =

    let run' (rt: TxRuntime) (tx: Tx<'T, 'E>) =
        match tx with
        | Pure v -> Task.FromResult(Ok v)
        | Effect f -> f rt

    let bind (f: 'T -> Tx<'U, 'E>) (tx: Tx<'T, 'E>) : Tx<'U, 'E> =
        match tx with
        | Pure v -> f v
        | Effect run ->
            Effect(fun rt ->
                task {
                    match! run rt with
                    | Ok v -> return! run' rt (f v)
                    | Error e -> return Error e
                })

    let step (at: TxStep) (body: TxRuntime -> Task<Result<'T, TxFailure<'E>>>) : Tx<'T, 'E> =
        Effect(fun rt ->
            task {
                let! outcome =
                    task {
                        try
                            return! body rt
                        with
                        | :? OperationCanceledException as ex -> return Err.rethrow ex
                        | Execution.MaterializationFailure m -> return Error(TxFailure.Failed(DbError.Materialization m))
                        | Translate.TranslationFailure t -> return Error(TxFailure.Failed(DbError.Translation t))
                        | ex ->
                            match rt.Session.Provider.Classify ex with
                            | Some f -> return Error(TxFailure.Failed(DbError.Database f))
                            | None -> return Err.rethrow ex
                    }

                match outcome with
                | Ok v ->
                    rt.Completed.Add at
                    return Ok v
                | Error f -> return Error(at, f)
            })

    let at (file: string) (line: int) (action: string) = { File = file; Line = line; Action = action }

    let write (at: TxStep) (cs: Changeset<'T>) : Tx<'T, 'E> =
        step at (fun rt ->
            task {
                match! Writes.run rt.Model rt.Session cs.State with
                | Ok v -> return Ok(unbox<'T> v)
                | Error(Writes.Invalid s) ->
                    let c = Changeset<'T>(s)
                    return Error(TxFailure.InvalidChanges(typeof<'T>.Name, c.Errors))
                | Error(Writes.Stale _) -> return Error(TxFailure.Conflict typeof<'T>.Name)
                | Error(Writes.Failed f) -> return Error(TxFailure.Failed(DbError.Database f))
                | Error(Writes.Unsupported op) ->
                    return Error(TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, rt.Session.Provider.Dialect.Name))))
            })

    /// The rows of a query, inside the transaction.
    let rows (rt: TxRuntime) (query: Query<'T>) : Task<Result<'T list, TxFailure<'E>>> =
        let rt = { rt with Session = { rt.Session with Args = query.Def.Args } }

        task {
          if not query.Def.Joins.IsEmpty then
            match Translate.projection rt.Model (ref false) query.Def None with
            | Error e -> return Error(TxFailure.Failed(DbError.Translation e))
            | Ok(select, shape) ->
                let! rows = Execution.readProjection rt.Session rt.Model select shape query.Def.Root.Name
                return Ok(rows |> List.map unbox<'T>)
          else
            match Planner.plan rt.Session.Provider.Dialect rt.Model query.Def with
            | Error e -> return Error(TxFailure.Failed(DbError.Translation e))
            | Ok plan ->
                let! rows = Execution.readRoot rt.Session plan
                return Ok(rows |> List.map unbox<'T>)
        }

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// The steps of a transaction. Each is located by where it is written (its
/// caller's file and line), so none needs a name:
/// `transaction { let! o = Tx.insert (Changeset.insert order) ... }`.
[<AbstractClass; Sealed>]
type Tx =

    /// A transaction that gives `v` without database work.
    static member ret(v: 'T) : Tx<'T, 'E> = Pure v

    static member bind (f: 'T -> Tx<'U, 'E>) : Tx<'T, 'E> -> Tx<'U, 'E> = TxCore.bind f

    static member map (f: 'T -> 'U) : Tx<'T, 'E> -> Tx<'U, 'E> = TxCore.bind (f >> Pure)

    /// Inserts a record; gives it back as stored (with its generated key),
    /// with what it owns.
    static member insert
        (
            value: 'T,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T, 'E> =
        let at = TxCore.at file line $"insert {typeof<'T>.Name}"
        TxCore.write at (Changeset.insert value)

    /// Updates `original`, the record a change was made from, to `changed`:
    /// only the columns that differ are written, and what it owns is written by
    /// difference. `expectVersion` is the version a caller last saw, for a copy
    /// that came back from a client; without it the version (or, for a record
    /// without one, every column) of `original` is checked.
    static member update
        (
            original: 'T,
            changed: 'T,
            ?expectVersion: int,
            [<CallerFilePath>] ?file: string,
            [<CallerLineNumber>] ?line: int
        ) : Tx<'T, 'E> =
        let at = TxCore.at (defaultArg file "") (defaultArg line 0) $"update {typeof<'T>.Name}"
        let cs = original |> Changeset.edit (fun _ -> changed)

        let cs =
            match expectVersion with
            | Some v -> cs |> Changeset.expectVersion v
            | None -> cs

        TxCore.write at cs

    /// Deletes a record read (and what it owns).
    static member delete
        (
            value: 'T,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T, 'E> =
        let at = TxCore.at file line $"delete {typeof<'T>.Name}"
        TxCore.write at (Changeset.delete value)

    /// Every row a query matches, inside the transaction.
    static member all
        (
            query: Query<'T>,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T list, 'E> =
        TxCore.step (TxCore.at file line $"all {typeof<'T>.Name}") (fun rt -> TxCore.rows rt query)

    /// The one row a query matches, or None; more than one is a failure
    /// (DbError.MoreThanOneRow). Turn None into a rejection with Tx.orReject.
    static member one
        (
            query: Query<'T>,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T option, 'E> =
        TxCore.step (TxCore.at file line $"one {typeof<'T>.Name}") (fun rt ->
            task {
                let limited = Query<'T>(OneRow.limit rt.Model query.Def)

                match! TxCore.rows rt limited with
                | Ok [] -> return Ok None
                | Ok [ row ] -> return Ok(Some row)
                | Ok _ -> return Error(TxFailure.Failed DbError.MoreThanOneRow)
                | Error e -> return Error e
            })

    /// How many rows a query matches, counted by the database.
    static member count
        (
            query: Query<'T>,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<int64, 'E> =
        TxCore.step (TxCore.at file line $"count {typeof<'T>.Name}") (fun rt ->
            task {
                let session = { rt.Session with Args = query.Def.Args }

                match Translate.countSelect rt.Model (ref false) query.Def with
                | Error e -> return Error(TxFailure.Failed(DbError.Translation e))
                | Ok select ->
                    let! rows = Execution.query session (Sql.renderSelect session.Provider.Dialect select) (fun r -> Convert.ToInt64(r.GetValue 0))
                    return Ok rows.[0]
            })

    /// Runs a bulk write (Bulk.update / delete / insert / upsert) as a step.
    static member execute
        (
            write: BulkWrite<'R>,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'R, 'E> =
        TxCore.step (TxCore.at file line "execute") (fun rt ->
            task {
                match BulkExec.commands rt.Model rt.Session.Provider.Dialect write.Def with
                | Error e -> return Error(TxFailure.Failed(DbError.Translation e))
                | Ok cmds ->
                    let args =
                        match write.Def with
                        | UpdateAll(q, _)
                        | DeleteAll q -> q.Args
                        | InsertAll _ -> [||]

                    let! v = BulkExec.run { rt.Session with Args = args } write.Def cmds
                    return Ok(write.Finish v)
            })

    /// Runs hand-written SQL as a step, reading rows as `'R` (see `Repo.Sql`).
    static member sql<'R, 'E>
        (
            sql: FormattableString,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'R list, 'E> =
        TxCore.step (TxCore.at file line "sql") (fun rt ->
            task {
                let cmd = Execution.rawCommand rt.Model rt.Session.Provider.Dialect sql
                let! rows = Execution.rawRecords rt.Session rt.Model typeof<'R> cmd
                return Ok(rows |> Seq.map unbox<'R> |> List.ofSeq)
            })

    /// Runs a hand-written command as a step, returning the affected row count.
    static member executeSql
        (
            sql: FormattableString,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<int, 'E> =
        TxCore.step (TxCore.at file line "executeSql") (fun rt ->
            task {
                let cmd = Execution.rawCommand rt.Model rt.Session.Provider.Dialect sql
                let! n = Execution.execute rt.Session cmd
                return Ok n
            })

    /// Fails the transaction here with `reason`, its own error type.
    static member fail
        (
            reason: 'E,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T, 'E> =
        TxCore.step (TxCore.at file line "fail") (fun _ -> Task.FromResult(Error(TxFailure.Rejected reason)))

    /// The value, or this rejection when there is none:
    /// `Tx.one query |> Tx.orReject (Missing "board")`.
    static member orReject
        (
            reason: 'E,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T option, 'E> -> Tx<'T, 'E> =
        TxCore.bind (function
            | Some v -> Pure v
            | None -> Tx.fail (reason, file, line))

    /// A decision's value, or its Error as a rejection:
    /// `Card.start ... |> Tx.ofResult Refused`.
    static member ofResult
        (
            reject: 'D -> 'E,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Result<'T, 'D> -> Tx<'T, 'E> =
        function
        | Ok v -> Pure v
        | Error d -> Tx.fail (reject d, file, line)

    /// A custom database step. It must only perform database work through the
    /// given context; external effects cannot be rolled back. Its Error is a
    /// rejection.
    static member run
        (
            f: TxContext -> Task<Result<'T, 'E>>,
            [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
            [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
        ) : Tx<'T, 'E> =
        TxCore.step (TxCore.at file line "run") (fun rt ->
            task {
                let ctx =
                    TxContext(rt.Session.Connection, Sandboxes.unwrap rt.Session.Transaction.Value, rt.Session.Ct)

                try
                    match! f ctx with
                    | Ok v -> return Ok v
                    | Error r -> return Error(TxFailure.Rejected r)
                with TransactionControlException m ->
                    return Error(TxFailure.Misused m)
            })

    /// Runs `f` for each item, in order.
    static member traverse (f: 'a -> Tx<'b, 'E>) : 'a list -> Tx<'b list, 'E> =
        let rec go acc items =
            match items with
            | [] -> Pure(List.rev acc)
            | x :: rest -> f x |> TxCore.bind (fun v -> go (v :: acc) rest)

        go []

type TxBuilder() =
    member _.Return(v: 'T) : Tx<'T, 'E> = Pure v
    member _.ReturnFrom(tx: Tx<'T, 'E>) = tx
    member _.Bind(tx: Tx<'T, 'E>, f: 'T -> Tx<'U, 'E>) = TxCore.bind f tx
    member _.Zero() : Tx<unit, 'E> = Pure()
    member _.Delay(f: unit -> Tx<'T, 'E>) = f ()
    member _.Combine(a: Tx<unit, 'E>, b: Tx<'T, 'E>) = TxCore.bind (fun () -> b) a

[<AutoOpen>]
module TxBuilderInstance =
    /// Describes a transaction: `transaction { let! o = Tx.insert cs ... }`.
    let transaction = TxBuilder()
