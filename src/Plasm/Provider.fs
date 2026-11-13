namespace Plasm

open System
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks

/// One executed database command, reported to `RepoOptions.OnCommand`.
type CommandEvent =
    { Sql: string
      /// Parameter names with values; values are `None` unless
      /// `RepoOptions.IncludeParameterValues` is enabled.
      Parameters: (string * string option) list
      Duration: TimeSpan
      /// Rows read (queries) or affected (writes).
      Rows: int
      Succeeded: bool }

/// Raised (and classified as `DbFailureKind.RepositoryClosed`) when a
/// sandbox's or playground's repository is used after it ended.
exception internal RepositoryClosedException of string with
    override e.Message = e.Data0

module internal Closed =
    let failure (ex: exn) =
        match ex with
        | RepositoryClosedException m ->
            Some
                { Kind = DbFailureKind.RepositoryClosed
                  SqlState = None
                  Constraint = None
                  Message = m
                  Exception = ex }
        | _ -> None

/// A database provider: dialect, connections and error classification.
/// Provider-specific behaviour lives behind this interface and `ISqlDialect`
/// (see docs/status.md for what is still PostgreSQL-shaped in the core).
type IProvider =
    abstract Dialect: ISqlDialect
    abstract OpenConnection: CancellationToken -> Task<DbConnection>
    /// Maps a provider exception to a classified failure; None if the exception
    /// is not a database failure.
    abstract Classify: exn -> DbFailure option
    /// Begins a read-only transaction giving one snapshot for several commands,
    /// or None if the provider cannot.
    abstract BeginSnapshot: DbConnection * CancellationToken -> Task<DbTransaction option>
    abstract BeginTransaction: DbConnection * IsolationLevel * CancellationToken -> Task<DbTransaction>

type RepoOptions =
    { OnCommand: (CommandEvent -> unit) option
      /// Include parameter values in diagnostics. Off by default because values
      /// may be sensitive.
      IncludeParameterValues: bool }

    static member Default =
        { OnCommand = None
          IncludeParameterValues = false }

/// Consistency of a read that issues several commands (relationship loads).
[<RequireQualifiedAccess>]
type ReadConsistency =
    /// Each command sees the latest committed data (default).
    | Independent
    /// All commands see one snapshot. Rejected if the provider cannot do this.
    | Snapshot
