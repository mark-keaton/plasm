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

[<RequireQualifiedAccess>]
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

/// One `including` or `includingOne` (including.feature): the inner query, a
/// lambda over the root record.
type internal IncludingDef = { Inner: Expr; One: bool }

type internal QueryDef =
    { Root: Type
      Wheres: Expr list
      Orders: (Expr * SortDirection) list
      Limit: int option
      Offset: int option
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
      Problems: string list
      /// Each `including` and `includingOne`, in the order written
      /// (including.feature).
      Includings: IncludingDef list
      /// `selectDistinct`: each selected value once (distinct.feature).
      Distinct: bool }

    member d.Paged =
        d.Limit.IsSome || d.Offset.IsSome || d.LimitArg.IsSome || d.OffsetArg.IsSome

    static member Empty(t: Type) =
        { Root = t
          Wheres = []
          Orders = []
          Limit = None
          Offset = None
          Joins = []
          GroupBy = []
          Havings = []
          LimitArg = None
          OffsetArg = None
          Template = None
          Args = [||]
          Problems = []
          Includings = []
          Distinct = false }

/// Something that can describe a query for display: a `Repo`.
type internal IQueryDescriber =
    /// Its model maps the type, or (F# Interactive) a redefinition of it.
    abstract Knows: Type -> bool
    abstract Describe: QueryDef -> string

/// The live repositories, most recent last, so that a query value can show the
/// SQL it would run (F# Interactive displays). Held weakly: a repository that
/// is no longer referenced drops out.
module internal Describers =
    let private live = ResizeArray<WeakReference<IQueryDescriber>>()

    let register (d: IQueryDescriber) =
        lock live (fun () ->
            live.RemoveAll(fun r -> not (fst (r.TryGetTarget()))) |> ignore
            live.Add(WeakReference<IQueryDescriber>(d)))

    /// The most recent repository that maps `t`, or a redefinition of it (so a
    /// query built before a type was redefined in F# Interactive explains that
    /// instead of showing SQL from a shadowed repository); else the most
    /// recent one.
    let find (t: Type) =
        lock live (fun () ->
            let alive =
                live
                |> Seq.choose (fun r ->
                    match r.TryGetTarget() with
                    | true, d -> Some d
                    | _ -> None)
                |> Seq.rev
                |> List.ofSeq

            match alive |> List.tryFind (fun d -> d.Knows t) with
            | Some d -> Some d
            | None -> List.tryHead alive)

/// A query value Plasm can translate without running it (Repo.CheckQueries):
/// a query, a projection, or a prepared query's template.
type internal ICheckable =
    abstract Def: QueryDef
    abstract Select: Expr option

/// An immutable description of a query over `'T` with its relationship loads.
/// Building or extending a query never touches the database; execute it with a
/// `Repo`.
[<Sealed; StructuredFormatDisplay("{Description}")>]
type Query<'T> internal (def: QueryDef) =
    member internal _.Def = def

    interface ICheckable with
        member _.Def = def
        member _.Select = None

    /// The SQL this query runs, with its parameter values and one command per
    /// relationship load, as translated by the most recently created repository
    /// that maps `'T`; or why it cannot be translated. Computing it performs no
    /// database access. F# Interactive displays queries this way.
    member _.Description =
        match Describers.find typeof<'T> with
        | Some d -> d.Describe def
        | None -> $"(create a Repo whose model maps {typeof<'T>.Name} to see the SQL this query runs)"

    /// Number of filters, for diagnostics and tests.
    member _.FilterCount = def.Wheres.Length

    override _.ToString() =
        $"Query<{typeof<'T>.Name}>(filters={def.Wheres.Length}, orderings={def.Orders.Length}, limit=%A{def.Limit}, offset=%A{def.Offset})"

/// A query whose result rows are projected to `'R`. Only the columns used by the
/// projection are retrieved and no entity is materialised.
[<Sealed>]
type Projection<'R> internal (def: QueryDef, select: Expr) =
    member internal _.Def = def
    member internal _.Select = select

    interface ICheckable with
        member _.Def = def
        member _.Select = Some select

module internal QueryOps =
    /// One more `including`: a projection built from a query only ever comes
    /// from the operations above, so its record is the tuple of the outer
    /// record and the lists in the order written.
    let including (one: bool) (def: QueryDef) (inner: Expr) : Projection<'R> =
        Projection<'R>({ def with Includings = def.Includings @ [ { Inner = inner; One = one } ] }, inner)

    /// A derived query. It is no longer the prepared query it may have come
    /// from, so it is planned from its own description, not the template.
    let inline map (q: Query<'T>) f : Query<'T> = Query<'T>({ f q.Def with Template = None })

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
/// }
/// ```
///
/// Each operation receives its expression as an F# quotation so it is
/// type-checked by the compiler and translated, never run client-side.
type QueryBuilder() =

    member _.For(_source: Table<'T>, _body: 'T -> Query<'T>) : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)
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
        QueryOps.order q key SortDirection.Ascending

    /// Appends a descending ordering key.
    [<CustomOperation("orderByDescending", MaintainsVariableSpace = true)>]
    member _.OrderByDescending(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key SortDirection.Descending

    /// Appends an ascending ordering key (alias of orderBy, for readability).
    [<CustomOperation("thenBy", MaintainsVariableSpace = true)>]
    member _.ThenBy(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key SortDirection.Ascending

    /// Appends a descending ordering key (alias of orderByDescending).
    [<CustomOperation("thenByDescending", MaintainsVariableSpace = true)>]
    member _.ThenByDescending(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] key: Expr<'T -> 'K>) =
        QueryOps.order q key SortDirection.Descending

    /// Limits the number of rows of this query. In a prepared query, `take size.Value` binds the count per call.
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

    /// Inner join: one row per matching pair. Keys are compared with SQL `=`,
    /// so NULL keys never match. Composite keys: `on ((o.A, o.B) = (x.A, x.B))`.
    [<CustomOperation("join", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.Join
        (
            outer: Table<'T>,
            _inner: Table<'U>,
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
            _inner: Table<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U -> 'R
        ) : Query<'R> =
        Query<'R>(QueryOps.join { outer.Def with Template = None } InnerJoin typeof<'U> outerKey innerKey)

    /// Left join: every row of the outer side once per match, or once with the
    /// joined record as `None` when nothing matches.
    [<CustomOperation("leftJoin", IsLikeJoin = true, JoinConditionWord = "on")>]
    member _.LeftJoin
        (
            outer: Table<'T>,
            _inner: Table<'U>,
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
            _inner: Table<'U>,
            [<ReflectedDefinition>] outerKey: Expr<'T -> 'K>,
            [<ReflectedDefinition>] innerKey: Expr<'U -> 'K>,
            _result: 'T -> 'U option -> 'R
        ) : Query<'R> =
        Query<'R>(QueryOps.join { outer.Def with Template = None } LeftJoin typeof<'U> outerKey innerKey)

    /// Reads each record with the list an inner query finds for it, as
    /// `record * list`. The inner query refers to the record by one equality
    /// between an inner and an outer column; its conditions, order and `take`
    /// apply per record. One more command per batch of outer values. An inner
    /// query ending in `select` or `selectDistinct` gives a list of what it
    /// selects; one ending in its own `including`s, its records with theirs.
    /// `includingOne` gives the one record the tie can find, as an option: the
    /// tie must be the inner record's key or a unique field.
    ///
    /// One overload per arity, as F# has no variadic generics: the outer record
    /// and up to 16 lists or options, mixed in any order (the number is stated
    /// here and in including.feature). A seventeenth finds no overload. A projection
    /// can be followed only by these, since only they keep the query's
    /// variable in scope, so its earlier elements may be of any type.

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'U list> =
        QueryOps.including false q.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'U list> =
        QueryOps.including false q.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'U option> =
        QueryOps.including true q.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'U option> =
        QueryOps.including true q.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("including", MaintainsVariableSpace = true)>]
    member _.Including(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O * 'U list> =
        QueryOps.including false p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Query<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O * 'U option> =
        QueryOps.including true p.Def inner

    [<CustomOperation("includingOne", MaintainsVariableSpace = true)>]
    member _.IncludingOne(p: Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O>, [<ProjectionParameter; ReflectedDefinition>] inner: Expr<'T -> Projection<'U>>) : Projection<'T * 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L * 'M * 'N * 'O * 'U option> =
        QueryOps.including true p.Def inner

    /// Projects each row, keeping each selected value once (as `List.distinct`;
    /// None is one value). Order by selected values only; `take` and `skip`
    /// are refused with it. Must be the last operation.
    [<CustomOperation("selectDistinct")>]
    member _.SelectDistinct(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] projection: Expr<'T -> 'R>) : Projection<'R> =
        Projection<'R>({ q.Def with Distinct = true }, projection)

    /// Projects each row. Only the columns the projection uses are retrieved.
    /// Must be the last operation.
    [<CustomOperation("select")>]
    member _.Select(q: Query<'T>, [<ProjectionParameter; ReflectedDefinition>] projection: Expr<'T -> 'R>) : Projection<'R> =
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

/// A prepared query: called like a function, and a value Plasm can find and
/// check (Repo.CheckQueries) with its arguments as parameters.
type internal PreparedQuery<'A, 'T>(template: QueryDef) =
    inherit FSharpFunc<'A, Query<'T>>()
    override _.Invoke(a: 'A) = Query<'T>(Prepared.bind template [| box a |])

    interface ICheckable with
        member _.Def = { template with Args = Array.zeroCreate 1 }
        member _.Select = None

type internal PreparedQuery2<'A, 'B, 'T>(template: QueryDef) =
    inherit OptimizedClosures.FSharpFunc<'A, 'B, Query<'T>>()
    override _.Invoke(a: 'A, b: 'B) = Query<'T>(Prepared.bind template [| box a; box b |])
    override this.Invoke(a: 'A) = fun (b: 'B) -> this.Invoke(a, b)

    interface ICheckable with
        member _.Def = { template with Args = Array.zeroCreate 2 }
        member _.Select = None

type internal PreparedQuery3<'A, 'B, 'C, 'T>(template: QueryDef) =
    inherit OptimizedClosures.FSharpFunc<'A, 'B, 'C, Query<'T>>()
    override _.Invoke(a: 'A, b: 'B, c: 'C) = Query<'T>(Prepared.bind template [| box a; box b; box c |])
    override this.Invoke(a: 'A) = fun (b: 'B) (c: 'C) -> this.Invoke(a, b, c)

    interface ICheckable with
        member _.Def = { template with Args = Array.zeroCreate 3 }
        member _.Select = None

/// A prepared projection: like PreparedQuery, for a query that ends in `select`.
type internal PreparedProjection<'A, 'R>(template: QueryDef, select: Expr) =
    inherit FSharpFunc<'A, Projection<'R>>()
    override _.Invoke(a: 'A) = Projection<'R>(Prepared.bind template [| box a |], select)

    interface ICheckable with
        member _.Def = { template with Args = Array.zeroCreate 1 }
        member _.Select = Some select

type internal PreparedProjection2<'A, 'B, 'R>(template: QueryDef, select: Expr) =
    inherit OptimizedClosures.FSharpFunc<'A, 'B, Projection<'R>>()
    override _.Invoke(a: 'A, b: 'B) = Projection<'R>(Prepared.bind template [| box a; box b |], select)
    override this.Invoke(a: 'A) = fun (b: 'B) -> this.Invoke(a, b)

    interface ICheckable with
        member _.Def = { template with Args = Array.zeroCreate 2 }
        member _.Select = Some select

/// Queries over a whole schema, and prepared queries.
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
        PreparedQuery<'A, 'T>(t) :> obj :?> ('A -> Query<'T>)

    /// Prepares a query that ends in `select`, with one argument: called like a
    /// function, and checked by repo.CheckQueries like any prepared query.
    static member prepare(build: Arg<'A> -> Projection<'R>) : 'A -> Projection<'R> =
        let p = build (Arg<'A>(0))
        let t = Prepared.template (fun () -> p.Def)
        PreparedProjection<'A, 'R>(t, p.Select) :> obj :?> ('A -> Projection<'R>)

    /// Prepares a query that ends in `select`, with two arguments.
    static member prepare2(build: Arg<'A> -> Arg<'B> -> Projection<'R>) : 'A -> 'B -> Projection<'R> =
        let p = build (Arg<'A>(0)) (Arg<'B>(1))
        let t = Prepared.template (fun () -> p.Def)
        PreparedProjection2<'A, 'B, 'R>(t, p.Select) :> obj :?> ('A -> 'B -> Projection<'R>)

    /// Prepares a query with two arguments.
    static member prepare2(build: Arg<'A> -> Arg<'B> -> Query<'T>) : 'A -> 'B -> Query<'T> =
        let t = Prepared.template (fun () -> (build (Arg<'A>(0)) (Arg<'B>(1))).Def)
        PreparedQuery2<'A, 'B, 'T>(t) :> obj :?> ('A -> 'B -> Query<'T>)

    /// Prepares a query with three arguments.
    static member prepare3(build: Arg<'A> -> Arg<'B> -> Arg<'C> -> Query<'T>) : 'A -> 'B -> 'C -> Query<'T> =
        let t = Prepared.template (fun () -> (build (Arg<'A>(0)) (Arg<'B>(1)) (Arg<'C>(2))).Def)
        PreparedQuery3<'A, 'B, 'C, 'T>(t) :> obj :?> ('A -> 'B -> 'C -> Query<'T>)

    static member from(_schema: Table<'T>) : Query<'T> = Query<'T>(QueryDef.Empty typeof<'T>)


[<AutoOpen>]
module QueryBuilderInstance =
    /// The query computation expression.
    let plasm = QueryBuilder()

    /// Inside a query's condition: true when `query` finds a row. The inner
    /// query may read the outer row's fields and a prepared query's arguments;
    /// it becomes a correlated `EXISTS` in the same command.
    /// `where (exists (plasm { for p in payments do where (p.Order = o.Id) }))`
    let exists (_query: Query<'T>) : bool =
        invalidOp "exists is only meaningful inside a query's condition, where it becomes SQL"

/// The condition `referring` and `referredBy` stand for (referring.feature):
/// `Refers(x, o)` is "x holds o's key", `ReferredBy(x, o)` is "o holds x's
/// key". The translator replaces each with the equality the model proves
/// (Translate.expandTies), so it is never run.
type internal TieMarkers =
    static member Refers<'T, 'O>(_inner: 'T, _outer: 'O) : bool = invalidOp "a tie is translated, never run"
    static member ReferredBy<'T, 'O>(_inner: 'T, _outer: 'O) : bool = invalidOp "a tie is translated, never run"

module internal Ties =
    /// A query of the `t` records tied to `outer` (an expression of a stored
    /// record: a value, or a query's variable), as one marker condition.
    let make (t: Type) (referring: bool) (outer: Expr) : obj =
        let outer =
            match outer with
            | Patterns.Coerce(x, _) -> x
            | x -> x

        let x = Var("x", t)
        let marker =
            match (if referring then <@@ TieMarkers.Refers(0, 0) @@> else <@@ TieMarkers.ReferredBy(0, 0) @@>) with
            | Patterns.Call(None, m, _) -> m.GetGenericMethodDefinition()
            | _ -> invalidOp "unreachable"

        let mi = marker.MakeGenericMethod(t, outer.Type)
        let def = { QueryDef.Empty t with Wheres = [ Expr.Lambda(x, Expr.Call(mi, [ Expr.Var x; outer ])) ] }

        Activator.CreateInstance(
            typedefof<Query<_>>.MakeGenericType t,
            Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Public,
            null,
            [| box def |],
            null
        )

    let isMarker (mi: Reflection.MethodInfo) = mi.DeclaringType = typeof<TieMarkers>

/// Ties the typed ids prove (referring.feature).
[<AutoOpen>]
module TieQueries =
    /// The `'T` records that refer to `record`: those whose one field of its key
    /// type (plain or an option) holds its key. `referring<Payment> o` is
    /// `plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) }`. A
    /// query: use it in `including`, `exists`, as a source, or with a record in
    /// hand. Not exactly one such field is refused, naming them.
    let referring<'T> (record: obj) : Query<'T> =
        if isNull record then nullArg "record"
        Ties.make typeof<'T> true (Expr.Value(record, record.GetType())) :?> Query<'T>

    /// The `'T` record that `record` refers to: the one whose key equals
    /// `record`'s one field of `'T`'s key type (a None finds nothing). At most
    /// one, so `includingOne` takes it.
    let referredBy<'T> (record: obj) : Query<'T> =
        if isNull record then nullArg "record"
        Ties.make typeof<'T> false (Expr.Value(record, record.GetType())) :?> Query<'T>

/// The query a read of one row runs (Repo.One, Tx.one).
module internal OneRow =

    /// At most two rows (enough to detect a second match), never more than the
    /// query's own limit, ordered by key when the query has no ordering.
    let limit (model: Model) (d: QueryDef) : QueryDef =
        let limited =
            match d.Limit, d.LimitArg with
            | Some n, _ when n <= 2 -> d
            | _, Some _ -> d // a prepared take: checked on the rows returned
            | _ -> { d with Limit = Some 2; Template = None }

        if not (obj.ReferenceEquals(limited, d)) && limited.Orders.IsEmpty then
            match model.TrySchema limited.Root with
            | Some s ->
                let v = Quotations.Var("x", limited.Root)
                let key = Quotations.Expr.Lambda(v, Quotations.Expr.PropertyGet(Quotations.Expr.Var v, s.Shape.Fields.[s.Key.Index]))
                { limited with Orders = [ key, SortDirection.Ascending ] }
            | None -> limited
        else
            limited

