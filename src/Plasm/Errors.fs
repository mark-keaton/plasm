namespace Plasm

open System

/// A problem found while validating persistence configuration.
type ConfigError =
    { Model: string
      Property: string option
      Message: string }

    override this.ToString() =
        match this.Property with
        | Some p -> $"{this.Model}.{p}: {this.Message}"
        | None -> $"{this.Model}: {this.Message}"

/// A query, load or write description that cannot be executed as stated.
/// These are detected before any database command is sent.
[<RequireQualifiedAccess>]
type TranslationError =
    /// An expression that cannot be translated to SQL. The library never falls
    /// back to evaluating it in application memory.
    | UnsupportedExpression of expression: string * reason: string
    /// A type used as a query source or relationship target has no schema.
    | UnmappedType of typeName: string
    /// The query is well-typed but its meaning is ambiguous or unsound,
    /// e.g. pagination without an ordering.
    | InvalidQuery of message: string
    /// Two load specifications customise the same relationship differently.
    /// The provider cannot implement the requested operation faithfully.
    | UnsupportedByProvider of operation: string * provider: string
    /// F# Interactive: the query and the model use different definitions of a
    /// type with the same name, because one was evaluated before the type was
    /// redefined in a later submission.
    | RedefinedType of typeName: string * detail: string

    override this.ToString() =
        match this with
        | UnsupportedExpression(e, r) -> $"Unsupported expression: {r}\n  in: {e}"
        | UnmappedType t -> $"Type {t} has no schema in this model"
        | InvalidQuery m -> $"Invalid query: {m}"
        | UnsupportedByProvider(op, p) -> $"Provider {p} does not support {op}"
        | RedefinedType(_, d) -> d

/// Classification of a database failure that application code can match on
/// without parsing messages.
[<RequireQualifiedAccess>]
type DbFailureKind =
    | ConnectionFailure
    | Timeout
    | UniqueViolation
    | ForeignKeyViolation
    | CheckViolation
    | NotNullViolation
    | SerializationFailure
    | Deadlock
    | OtherDatabaseError
    /// The repository can no longer be used: its sandbox ended or its
    /// playground was closed. Nothing was sent to the database.
    | RepositoryClosed

type DbFailure =
    { Kind: DbFailureKind
      SqlState: string option
      Constraint: string option
      Message: string
      Exception: exn }

    override this.ToString() =
        let state = defaultArg this.SqlState "-"
        $"{this.Kind} ({state}): {this.Message}"

/// A stored value that could not be reconstructed into the domain model.
type MaterializationError =
    { Model: string
      Property: string
      Value: string
      Message: string }

/// Failure of a read (query or relationship load). Cases are qualified
/// (`DbError.Database`) so they never collide with domain unions.
[<RequireQualifiedAccess>]
type DbError =
    | Translation of TranslationError
    | Database of DbFailure
    | Materialization of MaterializationError
    /// `repo.One` found more than one row for a query expected to match at most one.
    | MoreThanOneRow

    override this.ToString() =
        match this with
        | Translation t -> string t
        | Database d -> string d
        | Materialization m -> $"Cannot materialize {m.Model}.{m.Property} from {m.Value}: {m.Message}"
        | MoreThanOneRow -> "Expected at most one row, but more than one matched"

/// What kind of rule a field error represents.
[<RequireQualifiedAccess>]
type FieldErrorKind =
    | UniqueConstraint of constraintName: string
    | ForeignKeyConstraint of constraintName: string
    | CheckConstraint of constraintName: string
    | DuplicateIdentity
    | NotOwned
    | KeyChange
    | Custom of string

/// A structured validation or constraint error attached to a field.
type FieldError =
    { Field: string
      Message: string
      Kind: FieldErrorKind }

/// Raised for programming errors in persistence configuration that cannot be
/// expressed as values (e.g. a path lambda that is not a property access).
exception PlasmConfigurationException of string

module internal Err =
    let unsupported (e: obj) reason =
        DbError.Translation(TranslationError.UnsupportedExpression(string e, reason))

    let invalid msg = DbError.Translation(TranslationError.InvalidQuery msg)

    let config msg = raise (PlasmConfigurationException msg)

    /// Rethrows preserving the original stack trace (usable inside task handlers).
    let rethrow (ex: exn) : 'a =
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
        Unchecked.defaultof<'a>

    let describeExn (ex: exn) =
        match ex with
        | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException.Message
        | e -> e.Message
