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

/// A database provider: dialect, connections and error classification.
/// Everything provider-specific lives behind this interface.
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
