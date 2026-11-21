namespace Plasm.Sqlite

open System
open System.Data
open System.Data.Common
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Plasm

/// What the provider does with a file database's journal mode.
[<RequireQualifiedAccess>]
type JournalMode =
    /// WAL, set when the provider's first open creates the file; a database
    /// that already existed keeps its mode.
    | WalWhenCreated
    /// WAL, set on any file database, whatever mode it held.
    | Wal
    /// The mode the database has, never changed.
    | Keep

type SqliteOptions =
    { /// Keys sent in one command of a load: SQLite allows 32766 parameters.
      MaxKeysPerCommand: int
      /// How long a statement waits for another connection's write lock before
      /// it fails as DbFailureKind.Timeout (the connection's Default Timeout).
      BusyTimeout: TimeSpan
      JournalMode: JournalMode }

    static member Default =
        { MaxKeysPerCommand = 30000
          BusyTimeout = TimeSpan.FromSeconds 30.0
          JournalMode = JournalMode.WalWhenCreated }

/// The oldest SQLite Plasm.Sqlite runs on, and the renderings that set it: it
/// moves when one of them does.
module internal Floor =
    /// RETURNING (3.35), IS NOT DISTINCT FROM (3.39), and string_agg with ORDER
    /// BY inside the call (3.44, the highest).
    let minimum = Version(3, 44, 0)

    let renderings = "RETURNING (3.35), IS NOT DISTINCT FROM (3.39), string_agg(x, sep ORDER BY k) (3.44)"

module internal SqliteDdl =
    /// SQLite's keywords: a table or column named by one is printed quoted.
    let private reserved =
        set
            [ "abort"; "action"; "add"; "after"; "all"; "alter"; "always"; "analyze"; "and"; "as"; "asc"; "attach"
              "autoincrement"; "before"; "begin"; "between"; "by"; "cascade"; "case"; "cast"; "check"; "collate"
              "column"; "commit"; "conflict"; "constraint"; "create"; "cross"; "current"; "current_date"
              "current_time"; "current_timestamp"; "database"; "default"; "deferrable"; "deferred"; "delete"; "desc"
              "detach"; "distinct"; "do"; "drop"; "each"; "else"; "end"; "escape"; "except"; "exclude"; "exclusive"
              "exists"; "explain"; "fail"; "filter"; "first"; "following"; "for"; "foreign"; "from"; "full"
              "generated"; "glob"; "group"; "groups"; "having"; "if"; "ignore"; "immediate"; "in"; "index"; "indexed"
              "initially"; "inner"; "insert"; "instead"; "intersect"; "into"; "is"; "isnull"; "join"; "key"; "last"
              "left"; "like"; "limit"; "match"; "materialized"; "natural"; "no"; "not"; "nothing"; "notnull"; "null"
              "nulls"; "of"; "offset"; "on"; "or"; "order"; "others"; "outer"; "over"; "partition"; "plan"; "pragma"
              "preceding"; "primary"; "query"; "raise"; "range"; "recursive"; "references"; "regexp"; "reindex"
              "release"; "rename"; "replace"; "restrict"; "returning"; "right"; "rollback"; "row"; "rows"
              "savepoint"; "select"; "set"; "table"; "temp"; "temporary"; "then"; "ties"; "to"; "transaction"
              "trigger"; "unbounded"; "union"; "unique"; "update"; "using"; "vacuum"; "values"; "view"; "virtual"
              "when"; "where"; "window"; "with"; "without" ]

    let ddl =
        { new IDdlDialect with
            member _.Reserved = reserved
            // An INTEGER PRIMARY KEY is the row's id, which SQLite generates.
            member _.GeneratedKey = " primary key"
            member _.StoreColumn _ = "TEXT"
            member _.EnumType(_, _) = None

            member _.ColumnCheck(column, holds) =
                match holds with
                | ColumnHolds.Enum labels ->
                    let listed = labels |> List.map (fun l -> "'" + l.Replace("'", "''") + "'") |> String.concat ", "
                    Some $"check ({column} in ({listed}))"
                | ColumnHolds.List _ -> Some $"check (json_valid({column}) and json_type({column}) = 'array')"
                | ColumnHolds.Scalar t when t = typeof<DateTime> -> Some(Times.check column false)
                | ColumnHolds.Scalar t when t = typeof<DateTimeOffset> -> Some(Times.check column true)
                // A document: TEXT holding JSON (SqliteJson).
                | ColumnHolds.Document -> Some $"check (json_valid({column}))"
                | ColumnHolds.Scalar _ -> None
            member _.ForeignKeysInTables = true }

/// SQLite's catalogue: the table-valued pragmas, joined to the list of tables
/// sent as JSON (schema-check.feature). Constraint names SQLite keeps only in
/// each table's own text, so a unique constraint or a foreign key is reported
/// by the name Plasm gives it (`t_c_key`, `t_c_fkey`): a misnamed one cannot
/// be found.
module internal SqliteCatalogue =
    let catalogue =
        { new ICatalogueDialect with
            member _.Columns tables =
                "SELECT t.value, c.name, c.type, CASE WHEN c.\"notnull\" = 1 OR c.pk > 0 THEN 'NO' ELSE 'YES' END, "
                + "c.dflt_value IS NOT NULL OR (c.pk = 1 AND upper(c.type) = 'INTEGER'), 'main', c.type "
                + "FROM json_each("
                + tables
                + ") AS t JOIN pragma_table_xinfo(t.value) AS c"

            member _.CurrentSchema = "SELECT 'main'"
            member _.EnumLabels _ = "SELECT NULL, NULL, NULL WHERE 0"

            member _.UniqueIndexes tables =
                "SELECT t.value, (SELECT string_agg(ii.name, ',' ORDER BY ii.seqno) FROM pragma_index_info(il.name) AS ii), "
                + "CASE WHEN il.origin = 'u' THEN t.value || '_' || (SELECT string_agg(ii.name, '_' ORDER BY ii.seqno) FROM pragma_index_info(il.name) AS ii) || '_key' ELSE il.name END, "
                + "il.origin IN ('u', 'pk') FROM json_each("
                + tables
                + ") AS t JOIN pragma_index_list(t.value) AS il WHERE il.\"unique\" = 1"

            member _.References tables =
                "SELECT t.value, f.\"from\", f.\"table\", t.value || '_' || f.\"from\" || '_fkey' FROM json_each("
                + tables
                + ") AS t JOIN pragma_foreign_key_list(t.value) AS f "
                + "WHERE (SELECT count(*) FROM pragma_foreign_key_list(t.value) AS g WHERE g.id = f.id) = 1"

            member _.PrimaryKeys tables =
                "SELECT t.value, string_agg(c.name, ',' ORDER BY c.pk) FROM json_each("
                + tables
                + ") AS t JOIN pragma_table_info(t.value) AS c WHERE c.pk > 0 GROUP BY t.value"

            member _.ForeignKeys tables =
                "SELECT t.value, f.\"table\", string_agg(f.\"from\", ',' ORDER BY f.seq) FROM json_each("
                + tables
                + ") AS t JOIN pragma_foreign_key_list(t.value) AS f GROUP BY t.value, f.id"

            member _.TableColumns table =
                "SELECT c.name, c.type, c.\"notnull\" = 0 AND c.pk = 0, c.pk = 1 AND upper(c.type) = 'INTEGER', c.pk > 0, "
                + "c.dflt_value IS NOT NULL OR (c.pk = 1 AND upper(c.type) = 'INTEGER') FROM pragma_table_xinfo("
                + table
                + ") AS c ORDER BY c.cid" }

/// Decimals stored as text made exact by the functions and the collation
/// Decimals.register gives each connection: compared, ordered, added and
/// aggregated by value, and sent as text keeping their scale.
module internal SqliteExact =
    let exact =
        { new IExactDialect with
            member _.Compare(w, op, a, b) =
                w "plasm_decimal_compare("
                a ()
                w ", "
                b ()
                w $") {op} 0"

            member _.NullSafe(w, negated, a, b) =
                w "plasm_decimal_same("
                a ()
                w ", "
                b ()
                w (if negated then ") = 0" else ") = 1")

            member _.Arithmetic(w, op, a, b) =
                let name =
                    match op with
                    | "+" -> "add"
                    | "-" -> "subtract"
                    | "*" -> "multiply"
                    | "/" -> "divide"
                    | "%" -> "remainder"
                    | other -> invalidOp $"no exact decimal operator {other}"

                w $"plasm_decimal_{name}("
                a ()
                w ", "
                b ()
                w ")"

            member _.Aggregate(w, name, a) =
                match name with
                | "SUM"
                | "AVG" ->
                    w $"plasm_decimal_{name.ToLowerInvariant()}("
                    a ()
                    w ")"
                | _ ->
                    w $"{name}("
                    a ()
                    w $" COLLATE {Decimals.collation})"

            // The same functions, as the window functions they are (Windows).
            member _.WindowAggregate(w, name, a, over) =
                w $"plasm_decimal_{name.ToLowerInvariant()}("
                a ()
                w ")"
                over ()

            member _.Ordered(w, a) =
                a ()
                w $" COLLATE {Decimals.collation}"

            member _.Value m = box (Decimals.text m) }

/// JSON documents ([<Json>] fields, json-columns.feature) as SQLite's JSON
/// text: `->` for a member (JSON), `->>` for a scalar's value, json_each for
/// an array's elements. A document is TEXT, written by Plasm's serializer.
type internal SqliteJson() =
    static member val Instance: IJsonDialect = SqliteJson()

    interface IJsonDialect with
        member _.Member(w, doc, key) =
            w "("
            doc ()
            let quoted = key.Replace("'", "''")
            w $" -> '{quoted}')"

        member _.StoreType = [ "TEXT" ]

        member _.Text(w, doc) =
            w "("
            doc ()
            w " ->> '$')"

        member _.AnyElement(w, doc, alias, condition) =
            w "EXISTS (SELECT 1 FROM json_each("
            doc ()
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            w (") AS " + q alias + " WHERE ")
            condition ()
            w ")"

        // json_each gives a scalar element as its SQL value: written back as
        // JSON by its type, so a member or the text of it reads as PostgreSQL's
        // element does.
        member _.Element(w, alias) =
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            let a = q alias

            w (
                $"(CASE {a}.type WHEN 'object' THEN json({a}.value) WHEN 'array' THEN json({a}.value) "
                + $"WHEN 'true' THEN json('true') WHEN 'false' THEN json('false') WHEN 'null' THEN json('null') "
                + $"ELSE json_quote({a}.value) END)"
            )

        // Every element of `value` is an element of the document: PostgreSQL's
        // @> for arrays of scalars and of documents written alike.
        member _.Contains(w, doc, value) =
            w "NOT EXISTS (SELECT 1 FROM json_each("
            value ()
            w ") AS \"plasm_wanted\" WHERE NOT EXISTS (SELECT 1 FROM json_each("
            doc ()
            w ") AS \"plasm_held\" WHERE \"plasm_held\".type = \"plasm_wanted\".type AND \"plasm_held\".value = \"plasm_wanted\".value))"

type internal SqliteDialect(options: SqliteOptions) =
    let q (s: string) = "\"" + s.Replace("\"", "\"\"") + "\""

    interface ISqlDialect with
        member _.Name = "SQLite"
        member _.QuoteIdentifier s = q s
        member _.ParameterName i = $"@p{i}"

        // SQLite's own type names, so each column has the affinity its values
        // need.
        member _.ColumnType t =
            match t with
            | t when t = typeof<int16> || t = typeof<int> || t = typeof<int64> || t = typeof<bool> -> Some "INTEGER"
            | t when t = typeof<float> || t = typeof<float32> -> Some "REAL"
            // Exact text; Plasm's queries compare and order it by value (the
            // registered collation and functions), and any tool reads the text.
            | t when t = typeof<decimal> -> Some "TEXT"
            | t when t = typeof<byte[]> -> Some "BLOB"
            // A list of scalars: one JSON array (Lists.json), held to that form
            // by its check (ColumnCheck).
            | t when t.IsArray -> Some "TEXT"
            | t when
                t = typeof<string> || t = typeof<Guid> || t = typeof<DateTime> || t = typeof<DateTimeOffset>
                || t = typeof<DateOnly> || t = typeof<TimeOnly> || t = typeof<TimeSpan>
                ->
                Some "TEXT"
            | _ -> None

        member this.CatalogueTypes t =
            match (this :> ISqlDialect).ColumnType t with
            | Some declared -> [ declared.Split(' ').[0] ]
            | None -> []

        member _.CastType t =
            match t with
            | t when t = typeof<int16> || t = typeof<int> || t = typeof<int64> || t = typeof<bool> -> Some "INTEGER"
            | t when t = typeof<float> || t = typeof<float32> -> Some "REAL"
            | _ -> Some "TEXT"

        member _.StoreCast(_, _) = None
        member _.TextCast = None
        member _.EmptyArray = "'[]'"
        // A list column holds JSON text (Lists.json): its elements are read by
        // json_each, its length by json_array_length.
        member _.ListHas(w, element, column) =
            element ()
            w " IN (SELECT value FROM json_each("
            column ()
            w "))"

        member _.ListIsEmpty(w, column) =
            w "json_array_length("
            column ()
            w ") = 0"
        member _.ListValue values = Lists.json values
        member _.Refusal v = Lists.refusal v

        member _.InList(w, operand, list, _) =
            operand ()
            w $" IN (SELECT value FROM json_each({list}))"

        member _.NotInList(w, operand, list, _) =
            operand ()
            w $" NOT IN (SELECT value FROM json_each({list}))"

        member _.RowsIn(w, columns, lists) =
            w "("

            columns
            |> List.iteri (fun i column ->
                if i > 0 then w ", "
                column ())

            w ") IN "
            w (Lists.rows lists None q)

        member _.ListRows(w, lists, alias) = w (Lists.rows lists alias q)
        member _.Catalogue = SqliteCatalogue.catalogue
        member _.Ddl = SqliteDdl.ddl
        member _.UnboundedLimit = Some "-1"
        member _.NullSafeComparison negated = if negated then "IS NOT" else "IS"
        // GLOB, not LIKE, which ignores ASCII case: GLOB keeps every
        // character's case, and its own *, ? and [ are written [*], [?] and
        // [[] (a ] outside brackets is itself), then * where the match is open.
        member _.TextMatch(w, kind, operand, pattern) =
            operand ()
            w " GLOB "

            pattern (fun t ->
                let e =
                    t
                    |> String.collect (fun c ->
                        match c with
                        | '*' -> "[*]"
                        | '?' -> "[?]"
                        | '[' -> "[[]"
                        | c -> string c)

                match kind with
                | TextMatchKind.StartsWith -> e + "*"
                | TextMatchKind.EndsWith -> "*" + e
                | TextMatchKind.Contains -> "*" + e + "*")

        member _.FunctionName name =
            match name with
            | "char_length" -> "length"
            | "btrim" -> "trim"
            // Over 0 and 1, the greatest is whether any is true, the least whether all are.
            | "BOOL_OR" -> "MAX"
            | "BOOL_AND" -> "MIN"
            | "ARRAY_AGG" -> "json_group_array"
            | n -> n

        member _.Json = Some SqliteJson.Instance
        member _.Exact = Some SqliteExact.exact

        member _.Capabilities =
            { SnapshotReads = true
              ArrayParameters = false
              MaxKeysPerCommand = options.MaxKeysPerCommand
              Returning = true
              Upserts = true
              RowLocking = false
              WritersTakeTurns = true
              Batching = false
              DefersBegin = false
              Describe = true
              EnumTypes = false
              ArrayColumns = true }

        // Every transaction takes the write lock from its first statement.
        member _.Begin _ = "BEGIN IMMEDIATE"

module internal Classify =
    let private kindOf (extended: int) =
        match extended with
        | 2067 // SQLITE_CONSTRAINT_UNIQUE
        | 1555 -> DbFailureKind.UniqueViolation // SQLITE_CONSTRAINT_PRIMARYKEY
        | 787 -> DbFailureKind.ForeignKeyViolation // SQLITE_CONSTRAINT_FOREIGNKEY
        | 275 -> DbFailureKind.CheckViolation // SQLITE_CONSTRAINT_CHECK
        | 1299 -> DbFailureKind.NotNullViolation // SQLITE_CONSTRAINT_NOTNULL
        | 517 -> DbFailureKind.SerializationFailure // SQLITE_BUSY_SNAPSHOT: a stale view, run again
        | e when e &&& 0xff = 5 || e &&& 0xff = 6 -> DbFailureKind.Timeout // SQLITE_BUSY, SQLITE_LOCKED, after the busy timeout
        | _ -> DbFailureKind.OtherDatabaseError

    /// The constraint a message names: a CHECK's name; a UNIQUE's columns, as
    /// the name Plasm gives such a constraint (`t_c_key`).
    let private constraintOf (extended: int) (message: string) =
        let detail =
            match message.IndexOf "constraint failed: " with
            | -1 -> None
            | i -> Some(message.Substring(i + "constraint failed: ".Length).TrimEnd('\'', '.'))

        match extended, detail with
        | 275, Some name -> Some name
        | (2067 | 1555), Some columns ->
            // "customers.email" or "t.a, t.b"
            let parts = columns.Split(", ") |> Array.map (fun c -> c.Split('.'))

            if parts.Length > 0 && parts |> Array.forall (fun p -> p.Length = 2) then
                Some(parts.[0].[0] + "_" + (parts |> Array.map (fun p -> p.[1]) |> String.concat "_") + "_key")
            else
                None
        | _ -> None

    let rec classify (ex: exn) : DbFailure option =
        match ex with
        | :? SqliteException as se ->
            Some
                { Kind = kindOf se.SqliteExtendedErrorCode
                  SqlState = None
                  Constraint = constraintOf se.SqliteExtendedErrorCode se.Message
                  Message = se.Message
                  Exception = ex }
        | :? AggregateException as ae when ae.InnerExceptions.Count = 1 -> classify ae.InnerException
        | _ -> None

/// SQLite provider over Microsoft.Data.Sqlite. Each connection it opens has
/// foreign keys on (read back), the decimal functions and collation
/// registered, and a busy timeout; the first open of a file database sets its
/// journal mode as the options say.
/// Microsoft.Data.Sqlite's own connection under one Plasm.Sqlite gives.
module internal Connections =
    /// The SqliteConnection under a connection Plasm.Sqlite opened: in a
    /// Tx.run step, `Sqlite.connectionOf cmd.Connection`. A command made on it
    /// joins the transaction and has Plasm's decimal functions and collation,
    /// but its token does not interrupt it, Plasm does not refuse a COMMIT or
    /// BEGIN sent through it, and OnCommand does not report it.
    let connectionOf (connection: DbConnection) : SqliteConnection =
        match connection with
        | :? HearingConnection as h -> h.Inner
        | :? SqliteConnection as s -> s
        | null -> nullArg "connection"
        | other -> invalidArg "connection" $"a {other.GetType().Name} is not a connection Plasm.Sqlite opened"

    /// SQLite's transaction as the connection Plasm.Sqlite opened gives it.
    let hearing (connection: DbConnection) (tx: SqliteTransaction) : DbTransaction =
        match connection with
        | :? HearingConnection as h -> h.Hearing tx
        | _ -> tx :> DbTransaction

type SqliteProvider(connectionString: string, ?options: SqliteOptions) =
    let options = defaultArg options SqliteOptions.Default
    let dialect = SqliteDialect(options) :> ISqlDialect

    let builder = SqliteConnectionStringBuilder(connectionString)

    do builder.DefaultTimeout <- int options.BusyTimeout.TotalSeconds

    let file =
        match builder.DataSource with
        | null
        | ""
        | ":memory:" -> None
        | _ when builder.Mode = SqliteOpenMode.Memory -> None
        | path -> Some path

    // Whether the file existed before this provider first opened it.
    let existed = file |> Option.map File.Exists
    let mutable journalSet = 0
    let mutable journal = ""

    // The open's own statements wait for a busy database as a command does: in
    // tries of a second that hear the token (a rollback-journal file a writer
    // holds keeps even the journal mode from being read).
    let scalarHearing (ct: CancellationToken) (c: SqliteConnection) (sql: string) =
        use cmd = c.CreateCommand()
        cmd.CommandText <- sql

        try
            Interrupted.tries ct options.BusyTimeout (fun t -> cmd.CommandTimeout <- t) ignore (fun () -> cmd.ExecuteScalar())
        with e ->
            match Interrupted.translate ct e with
            | :? OperationCanceledException as c when not (obj.ReferenceEquals(c, e)) -> raise c
            | _ -> reraise ()

    let prepare (ct: CancellationToken) (c: SqliteConnection) =
        let scalar = scalarHearing ct

        let version = Version(c.ServerVersion)

        if version < Floor.minimum then
            invalidOp $"SQLite {c.ServerVersion} is older than {Floor.minimum}, the oldest Plasm.Sqlite runs on: {Floor.renderings}"

        scalar c "PRAGMA foreign_keys = ON" |> ignore

        if Convert.ToInt64(scalar c "PRAGMA foreign_keys") <> 1L then
            invalidOp "this SQLite does not enforce foreign keys (PRAGMA foreign_keys stays off: built with SQLITE_OMIT_FOREIGN_KEY?); Plasm.Sqlite needs them"

        Decimals.register c
        // plasm_decimal_sum and plasm_decimal_avg, window functions and so
        // aggregates too.
        Windows.register (c.Handle.DangerousGetHandle())

        match file with
        | Some _ when Volatile.Read(&journalSet) = 0 ->
            let wal =
                match options.JournalMode with
                | JournalMode.Wal -> true
                | JournalMode.WalWhenCreated -> existed = Some false
                | JournalMode.Keep -> false

            if wal then scalar c "PRAGMA journal_mode = WAL" |> ignore
            journal <- string (scalar c "PRAGMA journal_mode")
            // Set once read: an open cancelled before it leaves it to the next.
            Volatile.Write(&journalSet, 1)
        | _ -> ()

    /// The journal mode the file database had, or was given, when the provider
    /// first opened it; "" for an in-memory database or before the first open.
    member _.JournalMode = journal

    interface IProvider with
        member _.Dialect = dialect

        member _.OpenConnection ct =
            let c = new SqliteConnection(builder.ConnectionString)

            try
                c.Open()
                prepare ct c
                Task.FromResult(new HearingConnection(c, options.BusyTimeout) :> DbConnection)
            with _ ->
                c.Dispose()
                reraise ()

        member _.Classify ex = Classify.classify ex

        // Prepare compiles the statement without running it: unknown tables and
        // columns are refused; parameters and result types are not checked.
        member _.Describe(command, _) =
            task {
                try
                    command.Prepare()
                    return DescribeOutcome.Described
                with :? SqliteException as ex ->
                    match Classify.classify ex with
                    | Some f -> return DescribeOutcome.Refused f
                    | None -> return raise ex
            }

        // A deferred BEGIN: one snapshot for the reads that follow.
        member _.BeginSnapshot(conn, _) =
            task {
                let tx = (Connections.connectionOf conn).BeginTransaction(IsolationLevel.Serializable, true)
                return Some(Connections.hearing conn tx)
            }

        // Serializable whatever is asked, which is never weaker; begun IMMEDIATE,
        // so a writer waits here for the one before it, and only here.
        // Microsoft.Data.Sqlite waits for a busy database on the calling thread
        // and hears no cancellation while it does: so the BEGIN is tried on the
        // thread pool for a second at a time, the token heard between tries,
        // until the busy timeout has passed.
        member _.BeginTransaction(conn, isolation, ct) =
            task {
                match isolation with
                | IsolationLevel.Snapshot
                | IsolationLevel.Chaos -> return invalidOp $"SQLite has no {isolation} isolation; every SQLite transaction is serializable"
                | _ ->
                    let c = Connections.connectionOf conn
                    let waited = Diagnostics.Stopwatch.StartNew()
                    let mutable began: SqliteTransaction option = None
                    c.DefaultTimeout <- 1

                    try
                        while began.IsNone do
                            ct.ThrowIfCancellationRequested()

                            try
                                let! tx = Task.Run((fun () -> c.BeginTransaction(IsolationLevel.Serializable, false)), ct)
                                began <- Some tx
                            with :? SqliteException as e when e.SqliteErrorCode = 5 && waited.Elapsed < options.BusyTimeout ->
                                ()

                        return Connections.hearing conn began.Value
                    finally
                        c.DefaultTimeout <- int options.BusyTimeout.TotalSeconds
            }

[<RequireQualifiedAccess>]
module Sqlite =
    /// The SqliteConnection under a connection Plasm.Sqlite opened (see above).
    let connectionOf (connection: DbConnection) = Connections.connectionOf connection

    /// A provider for a connection string (`Data Source=app.db`).
    let connect (connectionString: string) = SqliteProvider(connectionString)

    let connectWith (options: SqliteOptions) (connectionString: string) = SqliteProvider(connectionString, options)
