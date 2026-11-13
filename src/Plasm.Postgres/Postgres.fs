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

type internal PostgresDialect(options: PostgresOptions) =
    interface ISqlDialect with
        member _.Name = "PostgreSQL"
        member _.QuoteIdentifier s = "\"" + s.Replace("\"", "\"\"") + "\""
        member _.ParameterName i = $"@p{i}"

        member _.ColumnTypes t =
            match t with
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
            { PerParentLimit = true
              SnapshotReads = true
              ArrayParameters = true
              MaxKeysPerCommand = options.MaxKeysPerCommand
              Returning = true
              Upserts = true
              RowLocking = true }

        member _.NullSafeComparison negated =
            if negated then "IS DISTINCT FROM" else "IS NOT DISTINCT FROM"

        member _.FunctionName name = name

module internal Classify =

    let private kindOf (state: string) =
        match state with
        | "23505" -> DbFailureKind.UniqueViolation
        | "23503" -> DbFailureKind.ForeignKeyViolation
        | "23514" -> DbFailureKind.CheckViolation
        | "23502" -> DbFailureKind.NotNullViolation
        | "40001" -> DbFailureKind.SerializationFailure
        | "40P01" -> DbFailureKind.Deadlock
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
                  Message = ne.Message
                  Exception = ex }
        | :? SocketException ->
            Some
                { Kind = DbFailureKind.ConnectionFailure
                  SqlState = None
                  Constraint = None
                  Message = ex.Message
                  Exception = ex }
        | :? TimeoutException ->
            Some
                { Kind = DbFailureKind.Timeout
                  SqlState = None
                  Constraint = None
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

        member _.BeginSnapshot(conn, ct) =
            task {
                let! tx = conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- "SET TRANSACTION READ ONLY"
                let! _ = cmd.ExecuteNonQueryAsync(ct)
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
