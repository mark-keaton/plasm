namespace Plasm

open System
open System.Reflection
open Microsoft.FSharp.Quotations

/// Implemented by prepared-query arguments; the translator recognises them.
type internal IArg =
    abstract Index: int

/// An argument of a prepared query (see `Query.prepare`). Use `.Value` inside
/// query operations; it is bound to a parameter each time the query runs.
[<Sealed>]
type Arg<'T> internal (index: int) =
    /// The argument's value. Only meaningful inside query operations, where it
    /// becomes a command parameter; reading it while the query is being built
    /// fails, because the value is not known until the query is run.
    member _.Value: 'T =
        invalidOp
            $"argument {index + 1} of a prepared query was read while the query was being built; use .Value only inside query operations (where, having, take, skip), where it becomes a parameter"

    interface IArg with
        member _.Index = index

    override _.ToString() = $"Arg({index + 1})"

/// Aggregate functions for `select`, `having` and `orderBy` in a query. They are
/// translated to SQL and computed by the database; calling one directly fails.
[<RequireQualifiedAccess>]
module Agg =
    let internal outside name : 'a =
        invalidOp $"Agg.{name} is only meaningful inside a query (select, having or orderBy); it is computed by the database"

    /// Number of rows (in the group).
    let count () : int64 = outside "count"

    /// Number of distinct non-null values.
    let countDistinct (_value: 'T) : int64 = outside "countDistinct"

    /// Sum of the values; zero when there are none (as `List.sum []`).
    let inline sum (value: ^N) : ^N =
        ignore (value + LanguagePrimitives.GenericZero< ^N>)
        invalidOp "Agg.sum is only meaningful inside a query (select, having or orderBy); it is computed by the database"

    /// Smallest value, or None when there are no rows.
    let min (_value: 'T) : 'T option when 'T: comparison = outside "min"

    /// Largest value, or None when there are no rows.
    let max (_value: 'T) : 'T option when 'T: comparison = outside "max"

    /// Average, or None when there are no rows. Like `List.average`, it is only
    /// defined for types that support division (decimal, float).
    let inline avg (value: ^N) : ^N option =
        ignore (LanguagePrimitives.DivideByInt< ^N> (value + LanguagePrimitives.GenericZero< ^N>) 1)
        invalidOp "Agg.avg is only meaningful inside a query (select, having or orderBy); it is computed by the database"

/// Literal SQL inside a typed query, for what the query language cannot say.
[<RequireQualifiedAccess>]
module Raw =
    /// `Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, pattern)`. The SQL text is
    /// typed as a format string, so the compiler only accepts a literal: values
    /// can never be spliced into SQL text. `{n}` placeholders take the
    /// arguments (a single value or a tuple), translated as columns or sent as
    /// parameters. Write `%%` for a literal `%`.
    let fragment<'T> (_sql: Printf.StringFormat<string>) (_args: obj) : 'T =
        invalidOp "Raw.fragment is only meaningful inside a query, where it becomes SQL"

type SortDirection =
    | Ascending
    | Descending

type internal JoinKind =
    | InnerJoin
    | LeftJoin

/// One `join` / `leftJoin`: keys are lambdas over the variable space so far
/// (outer) and over the joined record (inner).
type internal JoinDef =
    { Kind: JoinKind
      Target: Type
      OuterKey: Expr
      InnerKey: Expr }

type internal LoadSpec =
    { Assoc: PropertyInfo
      Child: QueryDef option
      /// Set by `replacePreload`: discard earlier loads of this relationship.
      Replace: bool }

and internal QueryDef =
    { Root: Type
      Wheres: Expr list
      Orders: (Expr * SortDirection) list
      Limit: int option
      Offset: int option
      PerParentLimit: int option
      Loads: LoadSpec list
      /// Joined sources, in order. The variable space is the root followed by
      /// each joined record (a flat tuple once anything is joined).
      Joins: JoinDef list
      /// Grouping keys (lambdas over the variable space) and group filters.
      GroupBy: Expr list
      Havings: Expr list
      /// `take` / `skip` bound from a prepared-query argument (its index).
      LimitArg: int option
      OffsetArg: int option
      /// A prepared query: the translated template and this call's arguments.
      Template: QueryDef option
      Args: obj[]
      /// Requests that cannot be honoured, reported when the query is planned.
      Problems: string list }

    member d.Paged =
        d.Limit.IsSome || d.Offset.IsSome || d.LimitArg.IsSome || d.OffsetArg.IsSome

    static member Empty(t: Type) =
        { Root = t
          Wheres = []
          Orders = []
          Limit = None
          Offset = None
          PerParentLimit = None
          Loads = []
          Joins = []
          GroupBy = []
          Havings = []
          LimitArg = None
          OffsetArg = None
          Template = None
          Args = [||]
          Problems = [] }

/// An immutable description of a query over `'T` with its relationship loads.
/// Building or extending a query never touches the database; execute it with a
/// `Repo`.
[<Sealed>]
type Query<'T> internal (def: QueryDef) =
    member internal _.Def = def

    /// Number of filters, for diagnostics and tests.
    member _.FilterCount = def.Wheres.Length

    /// Relationship names loaded directly from this query, in request order.
    member _.LoadPaths = def.Loads |> List.map (fun l -> l.Assoc.Name)

    override _.ToString() =
        let loads = def.Loads |> List.map (fun l -> l.Assoc.Name) |> String.concat ", "

        $"Query<{typeof<'T>.Name}>(filters={def.Wheres.Length}, orderings={def.Orders.Length}, limit=%A{def.Limit}, offset=%A{def.Offset}, loads=[{loads}])"

/// A query whose result rows are projected to `'R`. Only the columns used by the
/// projection are retrieved and no entity is materialised.
[<Sealed>]
type Projection<'R> internal (def: QueryDef, select: Expr) =
    member internal _.Def = def
    member internal _.Select = select

module internal QueryOps =
    let inline map (q: Query<'T>) f : Query<'T> = Query<'T>(f q.Def)

    let where (q: Query<'T>) (p: Expr) =
        map q (fun d -> { d with Wheres = d.Wheres @ [ p ] })

    let order (q: Query<'T>) (k: Expr) dir =
        map q (fun d -> { d with Orders = d.Orders @ [ k, dir ] })

    let checkCount what (n: int) =
        if n < 0 then invalidArg what $"{what} must be non-negative"

    /// A row count: a value, or a prepared-query argument (`take size.Value`).
    let count what (e: Expr<int>) : Choice<int, int> =
        match e with
        | Patterns.Value(v, _) -> Choice1Of2(unbox<int> v)
        | Patterns.PropertyGet(Some(Patterns.Value(:? IArg as a, _) | Patterns.ValueWithName(:? IArg as a, _, _)), p, []) when
            p.Name = "Value"
            ->
            Choice2Of2 a.Index
        | _ ->
            let rec mentions (x: Expr) =
                match x with
                | Patterns.Value(v, _)
                | Patterns.ValueWithName(v, _, _) -> v :? IArg
                | ExprShape.ShapeVar _ -> false
                | ExprShape.ShapeLambda(_, b) -> mentions b
                | ExprShape.ShapeCombination(_, xs) -> List.exists mentions xs

            if mentions e then
                invalidOp $"{what} in a prepared query takes an argument directly ({what} size.Value), not an expression of it"

            let n =
                match e with
                | Patterns.ValueWithName(v, _, _) -> unbox<int> v
                | _ -> unbox<int> (Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.EvaluateQuotation e)

            Choice1Of2 n

    let take what (q: Query<'T>) (e: Expr<int>) =
        match count what e with
        | Choice1Of2 n ->
            checkCount what n
            map q (fun d -> if what = "take" then { d with Limit = Some n; LimitArg = None } else { d with Offset = Some n; OffsetArg = None })
        | Choice2Of2 i ->
            map q (fun d -> if what = "take" then { d with LimitArg = Some i; Limit = None } else { d with OffsetArg = Some i; Offset = None })

    let preload (q: Query<'T>) (path: Expr) (child: QueryDef option) replace =
      if not q.Def.Joins.IsEmpty then
        map q (fun d ->
            { d with
                Problems =
                    d.Problems
                    @ [ "relationship loads are not supported on joined queries; select the records you need and load their relationships with repo.Preload" ] })
      else
        let prop = Paths.property "preload" path

        map q (fun d ->
            { d with
                Loads =
                    d.Loads
                    @ [ { Assoc = prop
                          Child = child
                          Replace = replace } ] })

    let join (outer: QueryDef) kind (target: Type) (outerKey: Expr) (innerKey: Expr) : QueryDef =
        { outer with
            Joins =
                outer.Joins
                @ [ { Kind = kind
                      Target = target
                      OuterKey = outerKey
                      InnerKey = innerKey } ] }

/// Computation expression for queries:
///
/// ```fsharp
/// plasm {
///     for o in Db.orders do
///     where (o.Status = Published)
///     orderBy o.PlacedAt
///     take 20
///     preload o.Payments
/// }
/// ```
///
/// Each operation receives its expression as an F# quotation so it is
/// type-checked by the compiler and translated, never run client-side.
type QueryBuilder() =

    member _.For(_source: Schema<'T>, _body: 'T -> Query<'T>) : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)
    member _.For(source: Query<'T>, _body: 'T -> Query<'T>) : Query<'T> = source
    member _.Yield(_: 'T) : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)
    member _.Zero() : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)

    /// Adds a filter. Multiple filters combine with AND.
    [<CustomOperation("where", MaintainsVariableSpace = true)>]
    member _.Where(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] predicate: Expr<'T -> bool>) =
        QueryOps.where q predicate

    /// Appends an ascending ordering key.
    [<CustomOperation("orderBy", MaintainsVariableSpace = true)>]
    member _.OrderBy(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key Ascending

    /// Appends a descending ordering key.
    [<CustomOperation("orderByDescending", MaintainsVariableSpace = true)>]
    member _.OrderByDescending(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key Descending

    /// Appends an ascending ordering key (alias of orderBy, for readability).
    [<CustomOperation("thenBy", MaintainsVariableSpace = true)>]
    member _.ThenBy(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key Ascending

    /// Appends a descending ordering key (alias of orderByDescending).
    [<CustomOperation("thenByDescending", MaintainsVariableSpace = true)>]
    member _.ThenByDescending(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key Descending

    /// Limits the number of rows of this query. Not allowed in a relationship
    /// load query, where the meaning would be ambiguous: use limitPerParent.
    /// In a prepared query, `take size.Value` binds the count per call.
    [<CustomOperation("take", MaintainsVariableSpace = true)>]
    member _.Take(q: Query<'T>, [<ReflectedDefinition>] count: Expr<int>) = QueryOps.take "take" q count

    /// Skips rows of this query (requires an ordering). In a prepared query,
    /// `skip offset.Value` binds the count per call.
    [<CustomOperation("skip", MaintainsVariableSpace = true)>]
    member _.Skip(q: Query<'T>, [<ReflectedDefinition>] count: Expr<int>) = QueryOps.take "skip" q count

    /// Groups rows by a key (a tuple for several keys). Finish a grouped query
    /// with `select`, using grouped keys and `Agg` functions.
    [<CustomOperation("groupBy", MaintainsVariableSpace = true)>]
    member _.GroupBy(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.map q (fun d -> { d with GroupBy = d.GroupBy @ [ key ] })

    /// Keeps only groups satisfying a condition over grouped keys and `Agg`
    /// functions (SQL HAVING).
    [<CustomOperation("having", MaintainsVariableSpace = true)>]
    member _.Having(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] predicate: Expr<'T -> bool>) =
        QueryOps.map q (fun d -> { d with Havings = d.Havings @ [ predicate ] })

    /// In a relationship load query: at most `count` related rows for each
    /// parent, chosen by the query's ordering.
    [<CustomOperation("limitPerParent", MaintainsVariableSpace = true)>]
    member _.LimitPerParent(q: Query<'T>, count: int) =
        QueryOps.checkCount "limitPerParent" count
        QueryOps.map q (fun d -> { d with PerParentLimit = Some count })

    /// Loads a relationship with separate batched queries.
    [<CustomOperation("preload", MaintainsVariableSpace = true)>]
    member _.Preload(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'R>>) =
        QueryOps.preload q assoc None false

    /// Loads a has-many relationship using a reusable query for the related
    /// records (filters, ordering, per-parent limits and nested loads).
    [<CustomOperation("preload", MaintainsVariableSpace = true)>]
    member _.Preload
        (q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, related: Query<'C>)
        =
        QueryOps.preload q assoc (Some related.Def) false

    /// Loads an optional relationship using a query for the related record.
    [<CustomOperation("preload", MaintainsVariableSpace = true)>]
    member _.Preload
        (
            q: Query<'T>,
            [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C option>>,
            related: Query<'C>
        ) =
        QueryOps.preload q assoc (Some related.Def) false

    /// Loads a required relationship using a query for the related record
    /// (typically to add nested loads).
    [<CustomOperation("preload", MaintainsVariableSpace = true)>]
    member _.Preload(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C>>, related: Query<'C>) =
        QueryOps.preload q assoc (Some related.Def) false

    /// Replaces every earlier load of a relationship instead of merging with it.
    [<CustomOperation("replacePreload", MaintainsVariableSpace = true)>]
    member _.ReplacePreload(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'R>>) =
        QueryOps.preload q assoc None true

    [<CustomOperation("replacePreload", MaintainsVariableSpace = true)>]
    member _.ReplacePreload
        (q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, related: Query<'C>)
        =
        QueryOps.preload q assoc (Some related.Def) true

    /// Inner join: one row per matching pair. Keys are compared with SQL `=`,
    /// so NULL keys never match. Composite keys: `on ((o.A, o.B) = (x.A, x.B))`.
    [<CustomOperation("join", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.Join
        (
            outer: Schema<'T>,
            _inner: Schema<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U -> 'R
        ) : Query<'R> =
        ignore outer
        Query<'R>(QueryOps.join (QueryDef.Empty typeof<'T>) InnerJoin typeof<'U> outerKey innerKey)

    [<CustomOperation("join", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.Join
        (
            outer: Query<'T>,
            _inner: Schema<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U -> 'R
        ) : Query<'R> =
        Query<'R>(QueryOps.join outer.Def InnerJoin typeof<'U> outerKey innerKey)

    /// Left join: every row of the outer side once per match, or once with the
    /// joined record as `None` when nothing matches.
    [<CustomOperation("leftJoin", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.LeftJoin
        (
            outer: Schema<'T>,
            _inner: Schema<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U option -> 'R
        ) : Query<'R> =
        ignore outer
        Query<'R>(QueryOps.join (QueryDef.Empty typeof<'T>) LeftJoin typeof<'U> outerKey innerKey)

    [<CustomOperation("leftJoin", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.LeftJoin
        (
            outer: Query<'T>,
            _inner: Schema<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U option -> 'R
        ) : Query<'R> =
        Query<'R>(QueryOps.join outer.Def LeftJoin typeof<'U> outerKey innerKey)

    /// Projects each row. Only the columns the projection uses are retrieved.
    /// Must be the last operation.
    [<CustomOperation("select")>]
    member _.Select(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] projection: Expr<'T -> 'R>) : Projection<'R> =
        if not q.Def.Loads.IsEmpty then
            invalidOp "A projected query cannot load relationships; remove preload or select whole records."

        Projection<'R>(q.Def, projection)

module internal Prepared =
    open Microsoft.FSharp.Quotations.Patterns

    let rec mentionsArg (e: Expr) =
        match e with
        | Value(v, _) -> v :? IArg
        | ValueWithName(v, _, _) -> v :? IArg
        | ExprShape.ShapeVar _ -> false
        | ExprShape.ShapeLambda(_, b) -> mentionsArg b
        | ExprShape.ShapeCombination(_, args) -> List.exists mentionsArg args

    let rec private check (d: QueryDef) =
        for o, _ in d.Orders do
            if mentionsArg o then
                invalidOp
                    "a prepared query cannot order by an argument: the ordering is part of the query's shape. Prepare one query per ordering"

        for l in d.Loads do
            l.Child |> Option.iter check

    /// Builds the template once with argument placeholders.
    let template (build: unit -> QueryDef) =
        let d = build ()
        check d
        d

    let bind (template: QueryDef) (args: obj[]) =
        let nonNegative (i: int option) what =
            match i with
            | Some i when unbox<int> args.[i] < 0 -> invalidArg what $"{what} must be non-negative"
            | _ -> ()

        nonNegative template.LimitArg "take"
        nonNegative template.OffsetArg "skip"
        { template with Template = Some template; Args = args }

/// Pipeline-style query functions, equivalent to the computation expression.
type Query =
    /// Prepares a query with one argument. The description is built and
    /// translated once; each call only binds the argument as a parameter:
    ///
    /// ```fsharp
    /// let ordersOf = Query.prepare (fun (c: Arg<CustomerId>) ->
    ///     plasm { for o in Db.orders do where (o.CustomerId = Some c.Value) })
    /// repo.All(ordersOf customerId)
    /// ```
    static member prepare(build: Arg<'A> -> Query<'T>) : 'A -> Query<'T> =
        let t = Prepared.template (fun () -> (build (Arg<'A>(0))).Def)
        fun a -> Query<'T>(Prepared.bind t [| box a |])

    /// Prepares a query with two arguments.
    static member prepare2(build: Arg<'A> -> Arg<'B> -> Query<'T>) : 'A -> 'B -> Query<'T> =
        let t = Prepared.template (fun () -> (build (Arg<'A>(0)) (Arg<'B>(1))).Def)
        fun a b -> Query<'T>(Prepared.bind t [| box a; box b |])

    /// Prepares a query with three arguments.
    static member prepare3(build: Arg<'A> -> Arg<'B> -> Arg<'C> -> Query<'T>) : 'A -> 'B -> 'C -> Query<'T> =
        let t = Prepared.template (fun () -> (build (Arg<'A>(0)) (Arg<'B>(1)) (Arg<'C>(2))).Def)
        fun a b c -> Query<'T>(Prepared.bind t [| box a; box b; box c |])

    static member from(_schema: Schema<'T>) : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)

    static member where([<ReflectedDefinition>] predicate: Expr<'T -> bool>) =
        fun (q: Query<'T>) -> QueryOps.where q predicate

    static member orderBy([<ReflectedDefinition>] key: Expr<'T -> 'K>) =
        fun (q: Query<'T>) -> QueryOps.order q key Ascending

    static member orderByDescending([<ReflectedDefinition>] key: Expr<'T -> 'K>) =
        fun (q: Query<'T>) -> QueryOps.order q key Descending

    static member take(count: int) =
        QueryOps.checkCount "take" count
        fun (q: Query<'T>) -> QueryOps.map q (fun d -> { d with Limit = Some count })

    static member skip(count: int) =
        QueryOps.checkCount "skip" count
        fun (q: Query<'T>) -> QueryOps.map q (fun d -> { d with Offset = Some count })

    static member preload([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'R>>) =
        fun (q: Query<'T>) -> QueryOps.preload q assoc None false

[<AutoOpen>]
module QueryBuilderInstance =
    /// The query computation expression.
    let plasm = QueryBuilder()
