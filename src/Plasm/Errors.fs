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
    | LoadConflict of path: string * detail: string
    /// The provider cannot implement the requested operation faithfully.
    | UnsupportedByProvider of operation: string * provider: string

    override this.ToString() =
        match this with
        | UnsupportedExpression(e, r) -> $"Unsupported expression: {r}\n  in: {e}"
        | UnmappedType t -> $"Type {t} has no schema in this model"
        | InvalidQuery m -> $"Invalid query: {m}"
        | LoadConflict(p, d) -> $"Conflicting loads for '{p}': {d}"
        | UnsupportedByProvider(op, p) -> $"Provider {p} does not support {op}"

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

/// Failure of a read (query or relationship load).
type DbError =
    | Translation of TranslationError
    | Database of DbFailure
    | Materialization of MaterializationError

    override this.ToString() =
        match this with
        | Translation t -> string t
        | Database d -> string d
        | Materialization m -> $"Cannot materialize {m.Model}.{m.Property} from {m.Value}: {m.Message}"

/// What kind of rule a field error represents.
[<RequireQualifiedAccess>]
type FieldErrorKind =
    | Required
    | Length
    | Range
    | Format
    | Inclusion
    | CastFailed
    | UniqueConstraint of constraintName: string
    | ForeignKeyConstraint of constraintName: string
    | CheckConstraint of constraintName: string
    | DuplicateIdentity
    | ReplacementRejected
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
        Translation(UnsupportedExpression(string e, reason))

    let invalid msg = Translation(InvalidQuery msg)

    let config msg = raise (PlasmConfigurationException msg)

    /// Rethrows preserving the original stack trace (usable inside task handlers).
    let rethrow (ex: exn) : 'a =
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
        Unchecked.defaultof<'a>

    let describeExn (ex: exn) =
        match ex with
        | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException.Message
        | e -> e.Message
