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
      Succeeded: bool
      /// The round trip the command travelled in: commands sent together
      /// share it, and Duration is that round trip's (round-trips.feature).
      RoundTrip: int64 }

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

/// What a schema check found.
[<RequireQualifiedAccess>]
type SchemaIssueKind =
    /// A mapped table (or join table) does not exist.
    | MissingTable
    /// A mapped column does not exist.
    | MissingColumn
    /// The column's type cannot hold the field's values.
    | IncompatibleType
    /// A non-optional field reads a nullable column: a NULL would fail to load.
    | NullableColumn
    /// A NOT NULL column without a default that the model never writes:
    /// inserts would fail.
    | UnwritableColumn
    /// An enum type's labels differ from the union's cases (missing, extra or
    /// in a different order).
    | EnumLabels
    /// A field the schema states is unique has no unique constraint or index
    /// on its column alone.
    | MissingUnique
    /// A field that references another record (a typed id) has no foreign key
    /// to that record's table.
    | MissingReference
    /// A unique field or a reference is held by a constraint (or unique index)
    /// whose name is not the one a violation is mapped to the field by
    /// (<table>_<column>_key, <table>_<column>_fkey): a violation would be a
    /// database failure instead of the field's error.
    | MisnamedConstraint

/// A difference between the model and the database that would make a mapped
/// read or write fail.
type SchemaIssue =
    { Kind: SchemaIssueKind
      Model: string
      /// The field or relationship, when the issue concerns one.
      Field: string option
      Table: string
      Column: string option
      Message: string }

    override i.ToString() =
        let field = i.Field |> Option.map (fun f -> "." + f) |> Option.defaultValue ""
        let column = i.Column |> Option.map (fun c -> "." + c) |> Option.defaultValue ""
        $"{i.Kind} {i.Model}{field} ({i.Table}{column}): {i.Message}"

/// A query an application defines that Plasm cannot translate: its name, as
/// Module.value, and why.
type QueryProblem =
    { Query: string
      Error: TranslationError }

    override p.ToString() = $"{p.Query}: {p.Error}"

/// What `Repo.Start` does with what its check finds (query-check.feature).
[<RequireQualifiedAccess>]
type StartupCheck =
    /// Fail start-up with every problem in one PlasmStartupException.
    | Crash
    /// Report each problem through RepoOptions.OnProblem, and start.
    | Warn
    /// Do not check.
    | Ignore

/// What `Repo.Start`'s check found.
[<RequireQualifiedAccess>]
type StartupProblem =
    /// A query value Plasm cannot translate.
    | Query of QueryProblem
    /// A difference between the model and the database.
    | Schema of SchemaIssue
    /// The assembly defines no query values, so the check checked nothing.
    | NoQueries of assembly: string
    /// The database could not be read, so the schema was not checked.
    | Unchecked of DbError
    /// Functions returning a query: not values, so not checked. A note,
    /// reported under Warn; it never fails start-up.
    | UncheckedFunctions of names: string list

    override p.ToString() =
        match p with
        | Query q -> string q
        | Schema s -> string s
        | NoQueries a -> $"found no query values in {a}: pass the assembly that defines the application's queries"
        | Unchecked e -> $"the schema could not be checked: {e}"
        | UncheckedFunctions names ->
            let what = if names.Length = 1 then "function returning a query is" else "functions returning a query are"
            let listed = String.concat ", " names
            $"{names.Length} {what} not checked: {listed}"

type RepoOptions =
    { OnCommand: (CommandEvent -> unit) option
      /// Include parameter values in diagnostics. Off by default because values
      /// may be sensitive.
      IncludeParameterValues: bool
      /// What `Repo.Start` does with a query it cannot translate (default Crash).
      Queries: StartupCheck
      /// What `Repo.Start` does with a difference between the model and the
      /// database (default Crash). Separate, since a database migrated after
      /// start-up is a reason to relax this one alone.
      Schema: StartupCheck
      /// Where Warn reports each problem; standard error when None.
      OnProblem: (StartupProblem -> unit) option }

    static member Default =
        { OnCommand = None
          IncludeParameterValues = false
          Queries = StartupCheck.Crash
          Schema = StartupCheck.Crash
          OnProblem = None }

    /// Default, with both checks written as Ignore: for a repository that has
    /// nothing to check, as in a script or a benchmark.
    static member Unchecked =
        { RepoOptions.Default with
            Queries = StartupCheck.Ignore
            Schema = StartupCheck.Ignore }

/// `Repo.Start` found problems under Crash: every one, queries by name, then
/// schema differences, so two runs of a failing start-up read the same.
type PlasmStartupException(problems: StartupProblem list, message: string) =
    inherit Exception(message)
    member _.Problems = problems

/// Consistency of a read that issues several commands (relationship loads).
[<RequireQualifiedAccess>]
type ReadConsistency =
    /// Each command sees the latest committed data (default).
    | Independent
    /// All commands see one snapshot. Rejected if the provider cannot do this.
    | Snapshot
