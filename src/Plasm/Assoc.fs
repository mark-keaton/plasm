namespace Plasm

/// The load state of a relationship field on an immutable record.
///
/// `NotLoaded` means the relationship was not requested; it says nothing about
/// whether related rows exist. `Loaded []`, `Loaded None` and `Loaded value`
/// mean the relationship was loaded and describe what was found.
///
/// The payload shape encodes cardinality:
/// - `Assoc<'T list>`   has-many
/// - `Assoc<'T option>` optional belongs-to / has-one
/// - `Assoc<'T>`        required belongs-to
///
/// Reading an `Assoc` never touches the database.
type Assoc<'T> =
    | NotLoaded
    | Loaded of 'T

[<RequireQualifiedAccess>]
module Assoc =

    let isLoaded (assoc: Assoc<'T>) =
        match assoc with
        | Loaded _ -> true
        | NotLoaded -> false

    let tryGet (assoc: Assoc<'T>) =
        match assoc with
        | Loaded v -> Some v
        | NotLoaded -> None

    /// Returns the loaded value. Fails with `InvalidOperationException` when the
    /// relationship was not loaded; it never loads it.
    let get (assoc: Assoc<'T>) =
        match assoc with
        | Loaded v -> v
        | NotLoaded ->
            invalidOp $"Relationship of type {typeof<'T>.Name} was not loaded. Request it explicitly with preload."

    let map (f: 'T -> 'U) (assoc: Assoc<'T>) =
        match assoc with
        | Loaded v -> Loaded(f v)
        | NotLoaded -> NotLoaded

    let defaultValue (fallback: 'T) (assoc: Assoc<'T>) =
        match assoc with
        | Loaded v -> v
        | NotLoaded -> fallback

/// Relationship predicates usable inside query expressions.
///
/// Inside a query these translate to correlated `EXISTS` subqueries, so a parent
/// row is returned at most once regardless of how many related rows match.
/// Outside a query they evaluate over loaded data and fail on `NotLoaded`.
[<RequireQualifiedAccess>]
module Rel =

    /// True when at least one related record satisfies the predicate.
    let any (predicate: 'T -> bool) (assoc: Assoc<'T list>) : bool =
        List.exists predicate (Assoc.get assoc)

    /// For an optional relationship (has-one, or an optional belongs-to): true
    /// when the related record exists and satisfies the predicate.
    let exists (predicate: 'T -> bool) (assoc: Assoc<'T option>) : bool =
        Option.exists predicate (Assoc.get assoc)

    /// True when no related record exists that fails the predicate
    /// (vacuously true for an empty relationship).
    let all (predicate: 'T -> bool) (assoc: Assoc<'T list>) : bool =
        List.forall predicate (Assoc.get assoc)
