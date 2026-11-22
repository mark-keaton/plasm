namespace Plasm.Postgres

open System
open System.Data
open System.Data.Common
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Npgsql
open Plasm

type PostgresOptions =
    { /// Keys sent per relationship command (as one array parameter).
      MaxKeysPerCommand: int }

    static member Default = { MaxKeysPerCommand = 5000 }

/// jsonb documents (json-columns.feature).
type internal PostgresJson() =
    static member val Instance: IJsonDialect = PostgresJson()

    interface IJsonDialect with
        member _.Member(w, doc, key) =
            w "("
            doc ()
            let quoted = key.Replace("'", "''")
            w $" -> '{quoted}')"

        member _.StoreType = [ "jsonb" ]

        member _.Text(w, doc) =
            w "("
            doc ()
            w " #>> '{}')"

        member _.AnyElement(w, doc, alias, condition) =
            w "EXISTS (SELECT 1 FROM jsonb_array_elements("
            doc ()
            // Quoted like every name: the alias is the query's variable.
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            w (") AS " + q alias + "(\"element\") WHERE ")
            condition ()
            w ")"

        member _.Element(w, alias) =
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            w (q alias + ".\"element\"")

        member _.Contains(w, doc, value) =
            w "("
            doc ()
            w " @> CAST("
            value ()
            w " AS jsonb))"

/// PostgreSQL's DDL (ddl.feature).
module internal PostgresDdl =
    /// The words PostgreSQL reserves, in both its reserved categories (its
    /// documentation's Appendix C): a table or column named by one, such as
    /// `from` or `order`, is printed quoted, as every query quotes it.
    let private reserved =
        set
                [ "all"; "analyse"; "analyze"; "and"; "any"; "array"; "as"; "asc"; "asymmetric"; "authorization"
                  "binary"; "both"; "case"; "cast"; "check"; "collate"; "collation"; "column"; "concurrently"
                  "constraint"; "create"; "cross"; "current_catalog"; "current_date"; "current_role"
                  "current_schema"; "current_time"; "current_timestamp"; "current_user"; "default"; "deferrable"
                  "desc"; "distinct"; "do"; "else"; "end"; "except"; "false"; "fetch"; "for"; "foreign"; "freeze"
                  "from"; "full"; "grant"; "group"; "having"; "ilike"; "in"; "initially"; "inner"; "intersect"
                  "into"; "is"; "isnull"; "join"; "lateral"; "leading"; "left"; "like"; "limit"; "localtime"
                  "localtimestamp"; "natural"; "not"; "notnull"; "null"; "offset"; "on"; "only"; "or"; "order"
                  "outer"; "overlaps"; "placing"; "primary"; "references"; "returning"; "right"; "select"
                  "session_user"; "similar"; "some"; "symmetric"; "system_user"; "table"; "tablesample"; "then"
                  "to"; "trailing"; "true"; "union"; "unique"; "user"; "using"; "variadic"; "verbose"; "when"
                  "where"; "window"; "with" ]

    let ddl =
        { new IDdlDialect with
            member _.Reserved = reserved
            member _.GeneratedKey = " generated always as identity primary key"
            member _.StoreColumn typeName = typeName
            member _.EnumType(typeName, labels) =
                let listed = labels |> List.map (fun l -> "'" + l + "'") |> String.concat ", "
                Some $"create type {typeName} as enum ({listed});"
            member _.ColumnCheck(_, _) = None
            member _.ForeignKeysInTables = false }

/// PostgreSQL's catalogue queries.
module internal PostgresCatalogue =
    /// PostgreSQL's catalogue: information_schema and pg_catalog, in the current
    /// schema (schema-check.feature, scaffolding).
    let catalogue =
        { new ICatalogueDialect with
            member _.Columns tables =
                "SELECT table_name, column_name, CASE WHEN data_type = 'ARRAY' THEN (SELECT format_type(t.typelem, NULL) || '[]' FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE t.typname = udt_name AND n.nspname = udt_schema) ELSE data_type END, " + "is_nullable, column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER', udt_schema, udt_name " + "FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ANY(" + tables + ")"
            member _.CurrentSchema = "SELECT current_schema()"
            member _.EnumLabels names =
                "SELECT n.nspname, t.typname, e.enumlabel FROM pg_type t " + "JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_enum e ON e.enumtypid = t.oid " + "WHERE t.typname = ANY(" + names + ") ORDER BY n.nspname, t.typname, e.enumsortorder"
            member _.UniqueIndexes tables =
                "SELECT t.relname::text, (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord) FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum WHERE k.ord <= i.indnkeyatts), ic.relname::text, " + "EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.conrelid = t.oid AND c.contype IN ('u', 'p')) FROM pg_index i " + "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "JOIN pg_class ic ON ic.oid = i.indexrelid " + "WHERE i.indisunique AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            member _.References tables =
                "SELECT t.relname::text, a.attname::text, r.relname::text, c.conname::text FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid " + "JOIN pg_namespace n ON n.oid = t.relnamespace " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = c.conkey[1] " + "WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1 AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            member _.PrimaryKeys tables =
                "SELECT t.relname::text, string_agg(a.attname::text, ',' ORDER BY k.ord) FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " + "WHERE c.contype = 'p' AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ") GROUP BY t.relname"
            member _.ForeignKeys tables =
                "SELECT t.relname::text, r.relname::text, (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord) FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum) FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "WHERE c.contype = 'f' AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            // PostgreSQL's Ddl writes no column check (IDdlDialect.ColumnCheck),
            // so the schema check reads none: its types hold what a check would.
            member _.Checks _ = "SELECT NULL::text, NULL::text WHERE false"
            member _.TableColumns table =
                "SELECT c.column_name, c.data_type, c.is_nullable = 'YES', " + "c.is_identity = 'YES' OR coalesce(c.column_default LIKE 'nextval(%', false), " + "EXISTS (SELECT 1 FROM information_schema.table_constraints tc " + "JOIN information_schema.key_column_usage k ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema AND k.table_name = tc.table_name " + "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND k.column_name = c.column_name), " + "c.column_default IS NOT NULL OR c.is_identity = 'YES' OR c.is_generated <> 'NEVER' " + "FROM information_schema.columns c WHERE c.table_schema = current_schema() AND c.table_name = " + table + " ORDER BY c.ordinal_position" }

type internal PostgresDialect(options: PostgresOptions) =
    interface ISqlDialect with
        member _.Name = "PostgreSQL"
        member _.QuoteIdentifier s = "\"" + s.Replace("\"", "\"\"") + "\""
        member _.ParameterName i = $"@p{i}"

        // A column is declared, and a JSON scalar cast, as the first name the
        // catalogue gives for the type.
        member this.ColumnType t = List.tryHead ((this :> ISqlDialect).CatalogueTypes t)
        member this.CastType t = List.tryHead ((this :> ISqlDialect).CatalogueTypes t)

        member this.CatalogueTypes t =
            match t with
            // A list of scalars: one array column of its element's type.
            | t when t.IsArray && t <> typeof<byte[]> -> (this :> ISqlDialect).CatalogueTypes(t.GetElementType()) |> List.map (fun n -> n + "[]")
            | t when t = typeof<int16> -> [ "smallint" ]
            | t when t = typeof<int> -> [ "integer" ]
            | t when t = typeof<int64> -> [ "bigint" ]
            | t when t = typeof<string> -> [ "text"; "character varying"; "character"; "citext" ]
            | t when t = typeof<bool> -> [ "boolean" ]
            | t when t = typeof<decimal> -> [ "numeric"; "money" ]
            | t when t = typeof<float> -> [ "double precision" ]
            | t when t = typeof<float32> -> [ "real" ]
            | t when t = typeof<Guid> -> [ "uuid" ]
            | t when t = typeof<DateTime> -> [ "timestamp with time zone"; "timestamp without time zone" ]
            | t when t = typeof<DateTimeOffset> -> [ "timestamp with time zone" ]
            | t when t = typeof<DateOnly> -> [ "date" ]
            | t when t = typeof<TimeOnly> -> [ "time without time zone" ]
            | t when t = typeof<TimeSpan> -> [ "interval" ]
            | t when t = typeof<byte[]> -> [ "bytea" ]
            | _ -> []

        member _.Capabilities =
            { SnapshotReads = true
              ArrayParameters = true
              MaxKeysPerCommand = options.MaxKeysPerCommand
              Returning = true
              Upserts = true
              RowLocking = true
              WritersTakeTurns = false
              Batching = true
              DefersBegin = true
              Describe = true
              EnumTypes = true
              ArrayColumns = true }

        // What Npgsql sends, as PostgreSQL logs it: its wording differs by level.
        member _.Begin isolation =
            match isolation with
            | IsolationLevel.RepeatableRead
            | IsolationLevel.Snapshot -> "BEGIN ISOLATION LEVEL REPEATABLE READ"
            | IsolationLevel.Serializable -> "BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE"
            | IsolationLevel.ReadUncommitted -> "BEGIN TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
            | _ -> "BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED"

        member this.StoreCast(typeName, array) =
            let d = this :> ISqlDialect
            Some((typeName |> List.map d.QuoteIdentifier |> String.concat ".") + (if array then "[]" else ""))

        member _.TextCast = Some "text"

        member _.EmptyArray = "'{}'"
        member _.ListHas(w, element, column) =
            element ()
            w " = ANY("
            column ()
            w ")"

        member _.ListIsEmpty(w, column) =
            w "cardinality("
            column ()
            w ") = 0"

        member _.ListValue values = box values
        member _.Refusal _ = None

        member _.NotInList(w, operand, list, cast) =
            operand ()

            match cast with
            | Some c -> w $" <> ALL(CAST({list} AS {c}))"
            | None -> w $" <> ALL({list})"

        member this.ListRows(w, lists, alias) =
            let q = (this :> ISqlDialect).QuoteIdentifier
            w "unnest("

            lists
            |> List.iteri (fun i (list, cast) ->
                if i > 0 then w ", "

                match cast with
                | Some c -> w $"CAST({list} AS {c})"
                | None -> w list)

            w ")"

            match alias with
            | Some(name, columns) ->
                let named = columns |> List.map q |> String.concat ", "
                w $" AS {q name}({named})"
            | None -> ()

        member _.RowsIn(w, columns, lists) =
            w "("

            columns
            |> List.iteri (fun i column ->
                if i > 0 then w ", "
                column ())

            w ") IN (SELECT * FROM unnest("

            lists
            |> List.iteri (fun i (list, cast) ->
                if i > 0 then w ", "

                match cast with
                | Some c -> w $"CAST({list} AS {c})"
                | None -> w list)

            w "))"

        member _.InList(w, operand, list, cast) =
            operand ()

            match cast with
            | Some c -> w $" = ANY(CAST({list} AS {c}))"
            | None -> w $" = ANY({list})"

        member _.Catalogue = PostgresCatalogue.catalogue

        member _.Ddl = PostgresDdl.ddl

        member _.UnboundedLimit = None
        member _.Cycle = CycleCheck.Clause(fun key flag path -> "CYCLE " + String.concat ", " key + " SET " + flag + " USING " + path)

        member _.NullSafeComparison negated =
            if negated then "IS DISTINCT FROM" else "IS NOT DISTINCT FROM"

        // A backslash is PostgreSQL's own escape character in LIKE.
        // LIKE, whose escape is a backslash by default: the text's own \\, %
        // and _ escaped, then % where the match is open.
        member _.TextMatch(w, kind, operand, pattern) =
            operand ()
            w " LIKE "

            pattern (fun t ->
                let e = t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")

                match kind with
                | TextMatchKind.StartsWith -> e + "%"
                | TextMatchKind.EndsWith -> "%" + e
                | TextMatchKind.Contains -> "%" + e + "%")
        member _.FunctionName name = name
        member _.Json = Some PostgresJson.Instance
        member _.Exact = None

module internal Classify =

    let private kindOf (state: string) =
        match state with
        | "23505" -> DbFailureKind.UniqueViolation
        | "23503" -> DbFailureKind.ForeignKeyViolation
        | "23514" -> DbFailureKind.CheckViolation
        | "23502" -> DbFailureKind.NotNullViolation
        | "40001" -> DbFailureKind.SerializationFailure
        | "40P01" -> DbFailureKind.Deadlock
        | "55P03" -> DbFailureKind.LockNotAvailable
        | "57014" -> DbFailureKind.Timeout
        | s when s.StartsWith "08" -> DbFailureKind.ConnectionFailure
        | "57P01"
        | "57P02"
        | "57P03" -> DbFailureKind.ConnectionFailure
        | _ -> DbFailureKind.OtherDatabaseError

    let rec classify (ex: exn) : DbFailure option =
        match ex with
        | :? PostgresException as pe ->
            Some
                { Kind = kindOf pe.SqlState
                  SqlState = Some pe.SqlState
                  Constraint = Option.ofObj pe.ConstraintName
                  Table = Option.ofObj pe.TableName
                  Columns = []
                  Message = pe.MessageText
                  Exception = ex }
        | :? NpgsqlException as ne ->
            let kind =
                match ne.InnerException with
                | :? TimeoutException -> DbFailureKind.Timeout
                | _ -> DbFailureKind.ConnectionFailure

            Some
                { Kind = kind
                  SqlState = None
                  Constraint = None
                  Table = None
                  Columns = []
                  Message = ne.Message
                  Exception = ex }
        | :? SocketException ->
            Some
                { Kind = DbFailureKind.ConnectionFailure
                  SqlState = None
                  Constraint = None
                  Table = None
                  Columns = []
                  Message = ex.Message
                  Exception = ex }
        | :? TimeoutException ->
            Some
                { Kind = DbFailureKind.Timeout
                  SqlState = None
                  Constraint = None
                  Table = None
                  Columns = []
                  Message = ex.Message
                  Exception = ex }
        | :? AggregateException as ae when ae.InnerExceptions.Count = 1 -> classify ae.InnerException
        | _ -> None

/// PostgreSQL provider over an Npgsql data source (which owns pooling).
type PostgresProvider(dataSource: NpgsqlDataSource, ?options: PostgresOptions) =
    let dialect = PostgresDialect(defaultArg options PostgresOptions.Default) :> ISqlDialect

    member _.DataSource = dataSource

    interface IProvider with
        member _.Dialect = dialect

        member _.OpenConnection ct =
            task {
                let! c = dataSource.OpenConnectionAsync(ct)
                return c :> DbConnection
            }

        member _.Classify ex = Classify.classify ex

        // Parse and Describe: a reader asked for the schema only is not run.
        // 42P18 is a parameter sent untyped that nothing in the statement types.
        member _.Describe(command, ct) =
            task {
                try
                    use! _ = command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct)
                    return DescribeOutcome.Described
                with ex ->
                    match (match ex with :? OperationCanceledException -> None | _ -> Classify.classify ex) with
                    | Some f when f.SqlState = Some "42P18" -> return DescribeOutcome.CannotType
                    | Some f -> return DescribeOutcome.Refused f
                    | None ->
                        // Cancelled, or not a database failure: raised as it was.
                        Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
                        return DescribeOutcome.Described
            }

        // REPEATABLE READ alone gives the snapshot; Plasm sends only SELECTs
        // in it. No SET, which a pooler in transaction mode may pin on.
        member _.BeginSnapshot(conn, ct) =
            task {
                let! tx = conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                return Some tx
            }

        member _.BeginTransaction(conn, isolation, ct) =
            task {
                let! tx = conn.BeginTransactionAsync(isolation, ct)
                return tx
            }

[<RequireQualifiedAccess>]
module Postgres =
    /// A provider for a connection string (creates an Npgsql data source).
    let connect (connectionString: string) =
        PostgresProvider(NpgsqlDataSource.Create(connectionString))

    let connectWith (options: PostgresOptions) (connectionString: string) =
        PostgresProvider(NpgsqlDataSource.Create(connectionString), options)
