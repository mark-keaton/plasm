/// The connection Plasm.Sqlite gives Plasm: Microsoft.Data.Sqlite's, its
/// commands made to hear their token while SQLite runs them (cancellation.feature).
/// Microsoft.Data.Sqlite runs each call on the caller's thread and hears no
/// token while a statement runs, and SqliteCommand.Cancel does not stop one:
/// so for as long as a statement is the caller's (a call, or a reader until it
/// is closed), a cancelled token interrupts it (sqlite3_interrupt on the
/// connection), and an interrupt the token caused is raised as
/// OperationCanceledException, as a cancelled Npgsql command raises. An
/// interrupt nothing cancelled stays SQLite's error.
namespace Plasm.Sqlite

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// The registrations a connection's statements hold, ended before the
/// connection closes: an interrupt is never sent to a closed handle.
type internal Interrupts(connection: SqliteConnection) =
    let held = HashSet<CancellationTokenRegistration>()
    let gate = obj ()

    /// While the returned registration is held, cancelling `ct` interrupts the
    /// statement the connection runs.
    member _.Hear(ct: CancellationToken) : IDisposable =
        if not ct.CanBeCanceled then
            { new IDisposable with
                member _.Dispose() = () }
        else
            let registration =
                ct.Register(fun () ->
                    lock gate (fun () ->
                        if connection.State = ConnectionState.Open then
                            SQLitePCL.raw.sqlite3_interrupt connection.Handle))

            lock gate (fun () -> held.Add registration |> ignore)

            { new IDisposable with
                member _.Dispose() =
                    // Dispose waits for a callback already running: when it
                    // returns, no interrupt from this token is to come.
                    registration.Dispose()
                    lock gate (fun () -> held.Remove registration |> ignore) }

    /// Ends every registration still held (a reader left open).
    member _.EndAll() =
        let all = lock gate (fun () -> let a = List.ofSeq held in held.Clear(); a)

        for r in all do
            r.Dispose()

module internal Interrupted =
    /// SQLITE_INTERRUPT while the token is cancelled, or SQLITE_BUSY or
    /// SQLITE_LOCKED that the token ended the wait for: the caller's
    /// cancellation. An interrupt nothing cancelled stays SQLite's error.
    let translate (ct: CancellationToken) (e: exn) : exn =
        match e with
        | :? SqliteException as s when (s.SqliteErrorCode = 9 || s.SqliteErrorCode = 5 || s.SqliteErrorCode = 6) && ct.IsCancellationRequested ->
            OperationCanceledException("The statement was cancelled: its token was cancelled while SQLite ran it or waited for the database.", s, ct) :> exn
        | e -> e

    /// `f` tried again while the database is busy (SQLITE_BUSY, or SQLITE_LOCKED
    /// in memory), until `busy` has passed or the token is cancelled.
    /// Microsoft.Data.Sqlite waits out each try itself, deaf to the token, for
    /// the timeout `setTimeout` gives it: a second, so the token is heard
    /// within one.
    let rec private attempt (ct: CancellationToken) (waited: Diagnostics.Stopwatch) (busy: TimeSpan) (f: unit -> 'T) : 'T =
        try
            f ()
        with :? SqliteException as e when (e.SqliteErrorCode = 5 || e.SqliteErrorCode = 6) && waited.Elapsed < busy && not ct.IsCancellationRequested ->
            attempt ct waited busy f

    let tries (ct: CancellationToken) (busy: TimeSpan) (setTimeout: int -> unit) (restore: unit -> unit) (f: unit -> 'T) : 'T =
        setTimeout 1

        try
            attempt ct (Diagnostics.Stopwatch.StartNew()) busy f
        finally
            restore ()

    let run (ct: CancellationToken) (hear: CancellationToken -> IDisposable) (f: unit -> 'T) : 'T =
        ct.ThrowIfCancellationRequested()

        try
            use _ = hear ct
            f ()
        with e ->
            match translate ct e with
            | :? OperationCanceledException as c when not (obj.ReferenceEquals(c, e)) -> raise c
            | _ -> reraise ()

/// A reader whose statement hears the token it was made with until it is
/// closed: stepping to a row (ReadAsync) is interrupted as a call is.
type internal HearingReader(inner: SqliteDataReader, ct: CancellationToken, heard: IDisposable) =
    inherit DbDataReader()
    let mutable ended = false

    let finish () =
        if not ended then
            ended <- true
            heard.Dispose()

    let step (f: unit -> 'T) : 'T =
        try
            f ()
        with e ->
            match Interrupted.translate ct e with
            | :? OperationCanceledException as c when not (obj.ReferenceEquals(c, e)) -> raise c
            | _ -> reraise ()

    member _.Inner = inner
    override _.Depth = inner.Depth
    override _.FieldCount = inner.FieldCount
    override _.HasRows = inner.HasRows
    override _.IsClosed = inner.IsClosed
    override _.RecordsAffected = inner.RecordsAffected
    override _.Item
        with get (i: int): obj = inner.[i]

    override _.Item
        with get (name: string): obj = inner.[name]
    override _.GetBoolean i = inner.GetBoolean i
    override _.GetByte i = inner.GetByte i
    override _.GetBytes(i, offset, buffer, bufferOffset, length) = inner.GetBytes(i, offset, buffer, bufferOffset, length)
    override _.GetChar i = inner.GetChar i
    override _.GetChars(i, offset, buffer, bufferOffset, length) = inner.GetChars(i, offset, buffer, bufferOffset, length)
    override _.GetDataTypeName i = inner.GetDataTypeName i
    // A time column's text in Plasm's form, as the instant in UTC (Times).
    override _.GetDateTime i = Times.dateTime (inner.GetName i) (inner.GetString i)
    override _.GetDecimal i = inner.GetDecimal i
    override _.GetDouble i = inner.GetDouble i
    override _.GetEnumerator() = (inner :> Collections.IEnumerable).GetEnumerator()
    [<return: Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields
                                                                   ||| Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)>]
    override _.GetFieldType i = inner.GetFieldType i
    override _.GetFloat i = inner.GetFloat i
    override _.GetGuid i = inner.GetGuid i
    override _.GetInt16 i = inner.GetInt16 i
    override _.GetInt32 i = inner.GetInt32 i
    override _.GetInt64 i = inner.GetInt64 i
    override _.GetName i = inner.GetName i
    override _.GetOrdinal name = inner.GetOrdinal name
    override _.GetString i = inner.GetString i
    override _.GetValue i = inner.GetValue i
    override _.GetValues values = inner.GetValues values
    override _.IsDBNull i = inner.IsDBNull i
    // As SQLite's reader reads them (a decimal from its text), not as the base
    // class would, through GetValue.
    override _.GetFieldValue<'T>(i: int) : 'T =
        // A list column (array-columns.feature): its JSON text read as the
        // array its type declares, each element as its scalar column would be.
        if typeof<'T>.IsArray && typeof<'T> <> typeof<byte[]> && not (inner.IsDBNull i) && inner.GetFieldType i = typeof<string> then
            unbox<'T> (box (Lists.parse typeof<'T> (inner.GetName i) (inner.GetString i)))
        elif typeof<'T> = typeof<DateTime> && not (inner.IsDBNull i) && inner.GetFieldType i = typeof<string> then
            unbox<'T> (box (Times.dateTime (inner.GetName i) (inner.GetString i)))
        elif typeof<'T> = typeof<DateTimeOffset> && not (inner.IsDBNull i) && inner.GetFieldType i = typeof<string> then
            unbox<'T> (box (Times.dateTimeOffset (inner.GetName i) (inner.GetString i)))
        else
            inner.GetFieldValue<'T> i
    override _.GetSchemaTable() = inner.GetSchemaTable()
    override _.NextResult() = step (fun () -> inner.NextResult())
    override _.Read() = step (fun () -> inner.Read())

    override this.ReadAsync(readCt: CancellationToken) =
        readCt.ThrowIfCancellationRequested()
        ct.ThrowIfCancellationRequested()
        Task.FromResult(step (fun () -> inner.Read()))

    override _.NextResultAsync(readCt: CancellationToken) =
        readCt.ThrowIfCancellationRequested()
        Task.FromResult(step (fun () -> inner.NextResult()))

    override _.Close() =
        finish ()
        inner.Close()

    override _.Dispose(disposing: bool) =
        if disposing then
            finish ()
            inner.Dispose()

        base.Dispose disposing

/// SQLite's transaction, its COMMIT tried while the database is busy (readers
/// of a rollback-journal file), hearing the token between tries.
/// A deferred one (`snapshot`) reads in one snapshot and says so: its
/// IsolationLevel is Snapshot, as what it begins with (`BEGIN`) differs from a
/// transaction's (`BEGIN IMMEDIATE`, transaction-events.feature).
and internal HearingTransaction(inner: SqliteTransaction, connection: DbConnection, busy: TimeSpan, snapshot: bool) =
    inherit DbTransaction()

    member _.Inner = inner
    override _.DbConnection = connection
    override _.IsolationLevel = if snapshot then IsolationLevel.Snapshot else inner.IsolationLevel
    override _.Commit() = inner.Commit()
    override _.Rollback() = inner.Rollback()
    override _.SupportsSavepoints = inner.SupportsSavepoints
    override _.Save(name: string) = inner.Save name
    override _.Rollback(name: string) = inner.Rollback name
    override _.Release(name: string) = inner.Release name

    override _.CommitAsync(ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        let sqlite = inner.Connection
        let timeout = sqlite.DefaultTimeout

        try
            Interrupted.tries ct busy (fun t -> sqlite.DefaultTimeout <- t) (fun () -> sqlite.DefaultTimeout <- timeout) (fun () -> inner.Commit())
            Task.CompletedTask
        with e ->
            match Interrupted.translate ct e with
            | :? OperationCanceledException as c when not (obj.ReferenceEquals(c, e)) -> raise c
            | _ -> reraise ()

    override _.Dispose(disposing: bool) =
        if disposing then inner.Dispose()
        base.Dispose disposing

/// A command whose statement hears its token for as long as it is the caller's.
and internal HearingCommand(inner: SqliteCommand, connection: DbConnection, interrupts: Interrupts, busy: TimeSpan) =
    inherit DbCommand()
    let mutable transaction: DbTransaction = null

    /// `f`, tried while the database is busy, hearing `ct` between tries.
    let tried (ct: CancellationToken) (f: unit -> 'T) =
        let timeout = inner.CommandTimeout
        Interrupted.tries ct busy (fun t -> inner.CommandTimeout <- t) (fun () -> inner.CommandTimeout <- timeout) f

    /// Each time sent as PostgreSQL keeps it, to the microsecond (Times), so
    /// what is stored is what a query by the same value finds.
    let kept () =
        for p in inner.Parameters do
            match p.Value with
            | :? DateTime
            | :? DateTimeOffset -> p.Value <- Times.kept p.Value
            | _ -> ()

    /// `f`, a foreign key failure raised with the key it broke (ForeignKeys):
    /// found outside the token's hearing, so no interrupt meets its cleanup,
    /// and not looked for once `ct` is cancelled.
    let keyed (ct: CancellationToken) (f: unit -> 'T) : 'T =
        try
            f ()
        with :? SqliteException as e when e.SqliteExtendedErrorCode = ForeignKeys.Code ->
            match ForeignKeys.diagnose inner (fun () -> ct.IsCancellationRequested) e with
            | :? ForeignKeys.Broken as b -> raise b
            | _ -> reraise ()

    member _.Inner = inner
    override _.CommandText with get () = inner.CommandText and set v = inner.CommandText <- v
    override _.CommandTimeout with get () = inner.CommandTimeout and set v = inner.CommandTimeout <- v
    override _.CommandType with get () = inner.CommandType and set v = inner.CommandType <- v
    override _.DesignTimeVisible with get () = inner.DesignTimeVisible and set v = inner.DesignTimeVisible <- v
    override _.UpdatedRowSource with get () = inner.UpdatedRowSource and set v = inner.UpdatedRowSource <- v
    override _.DbConnection with get () = connection and set _ = invalidOp "a Plasm.Sqlite command stays on its connection"
    override _.DbParameterCollection = inner.Parameters :> DbParameterCollection

    override _.DbTransaction
        with get () = if isNull transaction then inner.Transaction :> DbTransaction else transaction
        and set v =
            transaction <- v

            inner.Transaction <-
                match v with
                | null -> null
                | :? SqliteTransaction as t -> t
                | :? HearingTransaction as t -> t.Inner
                | other -> invalidArg "Transaction" $"a {other.GetType().Name} is not this connection's transaction"

    override _.Cancel() = inner.Cancel()
    override _.CreateDbParameter() = inner.CreateParameter() :> DbParameter
    override _.Prepare() = inner.Prepare()
    override _.ExecuteNonQuery() = kept (); keyed CancellationToken.None (fun () -> inner.ExecuteNonQuery())
    override _.ExecuteScalar() = kept (); keyed CancellationToken.None (fun () -> inner.ExecuteScalar())
    // Read as the asynchronous reader reads (its times in Plasm's form).
    override _.ExecuteDbDataReader(behavior) =
        kept ()
        new HearingReader(keyed CancellationToken.None (fun () -> inner.ExecuteReader(behavior)), CancellationToken.None, { new IDisposable with member _.Dispose() = () }) :> DbDataReader

    override _.ExecuteNonQueryAsync(ct: CancellationToken) =
        kept ()
        Task.FromResult(keyed ct (fun () -> Interrupted.run ct interrupts.Hear (fun () -> tried ct (fun () -> inner.ExecuteNonQuery()))))

    override _.ExecuteScalarAsync(ct: CancellationToken) =
        kept ()
        Task.FromResult(keyed ct (fun () -> Interrupted.run ct interrupts.Hear (fun () -> tried ct (fun () -> inner.ExecuteScalar()))))

    override _.ExecuteDbDataReaderAsync(behavior, ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        kept ()
        let heard = interrupts.Hear ct

        try
            let reader =
                try
                    tried ct (fun () -> inner.ExecuteReader(behavior))
                with :? SqliteException as se when se.SqliteExtendedErrorCode = ForeignKeys.Code ->
                    // Its key found once the token is no longer heard (keyed).
                    heard.Dispose()

                    match ForeignKeys.diagnose inner (fun () -> ct.IsCancellationRequested) se with
                    | :? ForeignKeys.Broken as b -> raise b
                    | _ -> reraise ()

            Task.FromResult(new HearingReader(reader, ct, heard) :> DbDataReader)
        with e ->
            heard.Dispose()

            match Interrupted.translate ct e with
            | :? OperationCanceledException as c when not (obj.ReferenceEquals(c, e)) -> raise c
            | _ -> reraise ()

    override _.Dispose(disposing: bool) =
        if disposing then inner.Dispose()
        base.Dispose disposing

/// Microsoft.Data.Sqlite's connection, as Plasm uses it: its commands hear
/// their tokens, and its state changes are its own (StateChange), as the
/// connection it wraps raises them. `Sqlite.connectionOf` gives the
/// SqliteConnection itself.
type internal HearingConnection(inner: SqliteConnection, busy: TimeSpan) as this =
    inherit DbConnection()
    let interrupts = Interrupts(inner)
    do inner.StateChange.Add(fun e -> this.Changed e)

    member private _.Changed(e: StateChangeEventArgs) = base.OnStateChange e

    member _.Inner = inner
    override _.ConnectionString with get () = inner.ConnectionString and set v = inner.ConnectionString <- v
    override _.Database = inner.Database
    override _.DataSource = inner.DataSource
    override _.ServerVersion = inner.ServerVersion
    override _.State = inner.State
    override _.ChangeDatabase name = inner.ChangeDatabase name
    override _.Open() = inner.Open()
    override this.BeginDbTransaction(isolation) = new HearingTransaction(inner.BeginTransaction(isolation), this, busy, false) :> DbTransaction
    override this.CreateDbCommand() = new HearingCommand(inner.CreateCommand(), this, interrupts, busy) :> DbCommand

    /// SQLite's transaction as this connection's (its COMMIT heard as a command is).
    member this.Hearing(tx: SqliteTransaction, snapshot: bool) = new HearingTransaction(tx, this, busy, snapshot) :> DbTransaction

    override _.Close() =
        interrupts.EndAll()
        inner.Close()

    override _.Dispose(disposing: bool) =
        if disposing then
            interrupts.EndAll()
            inner.Dispose()

        base.Dispose disposing

    override _.DisposeAsync() =
        interrupts.EndAll()
        inner.DisposeAsync()
