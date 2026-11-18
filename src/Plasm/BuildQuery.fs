namespace Plasm

open System
open Microsoft.FSharp.Quotations

// For programs that build a query at run time, from a caller's choices (a
// GraphQL layer, a report builder): an application writes its queries with
// plasm { }, not with these (build-query.feature).

/// An operator a declared field takes.
[<RequireQualifiedAccess>]
type QueryOperator =
    | Equal
    | NotEqual
    | GreaterThan
    | GreaterThanOrEqual
    | LessThan
    | LessThanOrEqual

module internal Built =
    /// The record's variable, named as a person would name it (c for Card).
    let variable (t: Type) = Var(string (Char.ToLowerInvariant t.Name.[0]), t)

    /// `read`'s body over `x`.
    let over (read: Expr<'T -> 'V>) (x: Var) : Expr<'V> =
        match read with
        | Patterns.Lambda(v, body) -> body.Substitute(fun w -> if w = v then Some(Expr.Var x) else None) |> Expr.Cast
        | _ -> invalidArg "read" "a field is read by a lambda over its record, fun (c: Card) -> c.Title"

    let refused<'T> (field: string) (why: string) =
        ArgumentException($"{typeof<'T>.Name}.{field}: {why}")

/// A condition over a record, built from declared fields, operators and
/// values, and joined with others by And, Or and Not.
[<Sealed>]
type QueryCondition<'T> internal (body: Var -> Expr<bool>) =
    member internal _.Body = body

    static member And(a: QueryCondition<'T>, b: QueryCondition<'T>) =
        QueryCondition<'T>(fun x ->
            let a, b = a.Body x, b.Body x
            <@ %a && %b @>)

    static member Or(a: QueryCondition<'T>, b: QueryCondition<'T>) =
        QueryCondition<'T>(fun x ->
            let a, b = a.Body x, b.Body x
            <@ %a || %b @>)

    static member Not(a: QueryCondition<'T>) =
        QueryCondition<'T>(fun x ->
            let a = a.Body x
            <@ not %a @>)

    /// The condition as the lambda BuildQuery.where takes.
    member _.ToExpr() : Expr<'T -> bool> =
        let x = Built.variable typeof<'T>
        Expr.Lambda(x, body x) |> Expr.Cast

/// A field of 'T that a program lets its callers filter, and perhaps order,
/// by: its name, the operators it takes, the type of the value a caller gives,
/// and how a condition and an ordering are built from it. Made by the
/// functions of the QueryField module, once, when the program starts.
[<Sealed>]
type QueryField<'T> internal (name: string, operators: QueryOperator list, valueType: Type, sample: obj, condition: QueryOperator -> obj -> Var -> Expr<bool>, order: (Query<'T> -> bool -> Query<'T>) option) =
    member _.Name = name
    member _.Operators: QueryOperator list = operators
    /// The type of the value a caller gives a condition.
    member _.ValueType = valueType
    member _.Orderable = order.IsSome
    member internal _.Sample = sample

    /// `field operator value`. Raises ArgumentException, naming the field, for
    /// an operator it does not take or a value it cannot make its own.
    member _.Condition(operator: QueryOperator, value: obj) : QueryCondition<'T> =
        if not (List.contains operator operators) then
            raise (Built.refused<'T> name $"takes no {operator}")

        // Built now, so a value the field refuses is refused here.
        let x0 = Built.variable typeof<'T>
        let built = condition operator value x0
        QueryCondition<'T>(fun x -> if x = x0 then built else built.Substitute(fun w -> if w = x0 then Some(Expr.Var x) else None) |> Expr.Cast)

    /// The query ordered by this field, after the orderings it has.
    member _.OrderBy(query: Query<'T>, descending: bool) : Query<'T> =
        match order with
        | Some o -> o query descending
        | None -> raise (Built.refused<'T> name "cannot be ordered by")

/// Declares a field a program lets its callers filter or order by.
[<RequireQualifiedAccess>]
module QueryField =

    let private given<'T, 'V> (name: string) (raw: obj) : 'V =
        match raw with
        | :? 'V as v -> v
        | null -> raise (Built.refused<'T> name "a value is required")
        | other ->
            try
                Convert.ChangeType(other, typeof<'V>) :?> 'V
            with _ ->
                raise (Built.refused<'T> name $"{other} is not a {typeof<'V>.Name}")

    let private via<'T, 'I, 'V> (name: string) (convert: 'I -> Result<'V, string>) (raw: obj) : 'V =
        match convert (given<'T, 'I> name raw) with
        | Ok v -> v
        | Error why -> raise (Built.refused<'T> name why)

    let private value (v: 'V) : Expr<'V> = Expr.Value(v, typeof<'V>) |> Expr.Cast

    let private equality (read: Expr<'T -> 'V>) (make: obj -> 'V) (op: QueryOperator) (raw: obj) (x: Var) : Expr<bool> when 'V: equality =
        let field, v = Built.over read x, value (make raw)

        match op with
        | QueryOperator.Equal -> <@ %field = %v @>
        | _ -> <@ %field <> %v @>

    let private comparison (read: Expr<'T -> 'V>) (make: obj -> 'V) (op: QueryOperator) (raw: obj) (x: Var) : Expr<bool> when 'V: comparison =
        let field, v = Built.over read x, value (make raw)

        match op with
        | QueryOperator.Equal -> <@ %field = %v @>
        | QueryOperator.NotEqual -> <@ %field <> %v @>
        | QueryOperator.GreaterThan -> <@ %field > %v @>
        | QueryOperator.GreaterThanOrEqual -> <@ %field >= %v @>
        | QueryOperator.LessThan -> <@ %field < %v @>
        | QueryOperator.LessThanOrEqual -> <@ %field <= %v @>

    let private ordering (read: Expr<'T -> 'V>) =
        Some(fun (q: Query<'T>) descending -> QueryOps.order q read.Raw (if descending then SortDirection.Descending else SortDirection.Ascending))

    let private compared = [ QueryOperator.Equal; QueryOperator.NotEqual ]

    let private all =
        [ QueryOperator.Equal
          QueryOperator.NotEqual
          QueryOperator.GreaterThan
          QueryOperator.GreaterThanOrEqual
          QueryOperator.LessThan
          QueryOperator.LessThanOrEqual ]

    /// The value the check builds a plain field's conditions with.
    let private sampleOf<'V> () : obj = if typeof<'V> = typeof<string> then box "" else box Unchecked.defaultof<'V>

    /// A field compared for equality, given as its own type.
    let equal (name: string) (read: Expr<'T -> 'V>) : QueryField<'T> when 'V: equality =
        QueryField<'T>(name, compared, typeof<'V>, sampleOf<'V> (), equality read (given<'T, 'V> name), None)

    /// A field compared and ordered by, given as its own type.
    let ordered (name: string) (read: Expr<'T -> 'V>) : QueryField<'T> when 'V: comparison =
        QueryField<'T>(name, all, typeof<'V>, sampleOf<'V> (), comparison read (given<'T, 'V> name), ordering read)

    /// A field compared for equality, given as 'I and made a 'V by `convert`
    /// (Error says why it cannot be); `sample` is an 'I the check uses.
    let equalVia (name: string) (read: Expr<'T -> 'V>) (convert: 'I -> Result<'V, string>) (sample: 'I) : QueryField<'T> when 'V: equality =
        QueryField<'T>(name, compared, typeof<'I>, box sample, equality read (via<'T, 'I, 'V> name convert), None)

    /// A field compared and ordered by, given as 'I and made a 'V by `convert`.
    let orderedVia (name: string) (read: Expr<'T -> 'V>) (convert: 'I -> Result<'V, string>) (sample: 'I) : QueryField<'T> when 'V: comparison =
        QueryField<'T>(name, all, typeof<'I>, box sample, comparison read (via<'T, 'I, 'V> name convert), ordering read)

/// Adds what a program built to a query: what the query builder adds for the
/// same clause written, so the SQL, the rows and the refusals are the written
/// query's.
[<RequireQualifiedAccess>]
module BuildQuery =

    let where (condition: Expr<'T -> bool>) (query: Query<'T>) : Query<'T> = QueryOps.where query condition.Raw
    let orderBy (key: Expr<'T -> 'K>) (query: Query<'T>) : Query<'T> = QueryOps.order query key.Raw SortDirection.Ascending
    let orderByDescending (key: Expr<'T -> 'K>) (query: Query<'T>) : Query<'T> = QueryOps.order query key.Raw SortDirection.Descending
    let take (count: int) (query: Query<'T>) : Query<'T> = QueryOps.take "take" query (Expr.Value count |> Expr.Cast)
    let skip (count: int) (query: Query<'T>) : Query<'T> = QueryOps.take "skip" query (Expr.Value count |> Expr.Cast)
