namespace Plasm.HotChocolate

open System
open System.Collections.Generic
open HotChocolate
open HotChocolate.Data.Filters
open HotChocolate.Data.Sorting
open HotChocolate.Resolvers
open HotChocolate.Types
open HotChocolate.Types.Pagination
open System.Threading.Tasks
open Plasm

/// What a GraphQL request asks of a query: its where and order, read from
/// Hot Chocolate's filter and sorting contexts, built with Plasm's BuildQuery
/// from the fields declared, so both are done by the database. Both contexts
/// are marked handled, so Hot Chocolate does neither again in memory.
[<AbstractClass; Sealed>]
type PlasmRequest =

    static member private Operator(name: string) =
        match name with
        | "eq" -> QueryOperator.Equal
        | "neq" -> QueryOperator.NotEqual
        | "gt" -> QueryOperator.GreaterThan
        | "gte" -> QueryOperator.GreaterThanOrEqual
        | "lt" -> QueryOperator.LessThan
        | "lte" -> QueryOperator.LessThanOrEqual
        | "contains" -> QueryOperator.Contains
        | "startsWith" -> QueryOperator.StartsWith
        | "endsWith" -> QueryOperator.EndsWith
        | other -> raise (GraphQLException $"{other} is no operator Plasm declared")

    static member private Condition(fields: IReadOnlyDictionary<string, QueryField<'T>>, input: IDictionary<string, obj>) : QueryCondition<'T> option =
        let parts =
            [ for KeyValue(key, value) in input do
                  match key, value with
                  | "and", (:? System.Collections.IEnumerable as parts) ->
                      let each = [ for p in parts do match PlasmRequest.Condition(fields, p :?> IDictionary<string, obj>) with Some c -> yield c | None -> () ]
                      if not each.IsEmpty then yield each |> List.reduce (fun a b -> QueryCondition.And(a, b))
                  | "or", (:? System.Collections.IEnumerable as parts) ->
                      let each = [ for p in parts do match PlasmRequest.Condition(fields, p :?> IDictionary<string, obj>) with Some c -> yield c | None -> () ]
                      if not each.IsEmpty then yield each |> List.reduce (fun a b -> QueryCondition.Or(a, b))
                  | name, (:? IDictionary<string, obj> as operations) ->
                      match fields.TryGetValue name with
                      | true, field ->
                          for KeyValue(op, v) in operations do
                              if not (isNull v) then
                                  yield
                                      try
                                          field.Condition(PlasmRequest.Operator op, v)
                                      with :? ArgumentException as e ->
                                          raise (GraphQLException e.Message)
                      | _ -> raise (GraphQLException $"{typeof<'T>.Name} declares no field {name}")
                  | _ -> () ]

        match parts with
        | [] -> None
        | first :: rest -> Some(rest |> List.fold (fun a b -> QueryCondition.And(a, b)) first)

    /// `query` with the request's where and order, from the fields declared:
    /// the field's `where` and `order` arguments, as Hot Chocolate coerced them.
    static member Apply(context: IResolverContext, fields: QueryField<'T> seq, query: Query<'T>) : Query<'T> =
        PlasmRequest.Apply(context, fields, query, Unchecked.defaultof<QueryField<'T>>)

    /// As Apply, ordered by `orderWhenNone`, ascending, when the request gives
    /// no order: Plasm refuses a page without one, since its rows would not be
    /// the same from one read to the next.
    static member Apply(context: IResolverContext, fields: QueryField<'T> seq, query: Query<'T>, orderWhenNone: QueryField<'T>) : Query<'T> =
        PlasmRequest.Refinement(context, fields, orderWhenNone).Invoke query

    /// The request's where and order as a function of the query, read from the
    /// context now: for a DataLoader's batch, which runs after the resolver
    /// and must not keep its context. `orderWhenNone` may be null.
    static member Refinement(context: IResolverContext, fields: QueryField<'T> seq, orderWhenNone: QueryField<'T>) : Func<Query<'T>, Query<'T>> =
        let byName = Dictionary<string, QueryField<'T>>()
        for f in fields do byName.[f.Name] <- f

        // Plasm does both; Hot Chocolate is told so, where it made a context.
        match context.GetFilterContext() with
        | null -> ()
        | filter -> filter.Handled true

        match context.GetSortingContext() with
        | null -> ()
        | sorting -> sorting.Handled true

        let argument (name: string) =
            if context.Selection.Field.Arguments.ContainsField name then context.ArgumentValue<obj> name else null

        let condition = PlasmRequest.WhereCondition(context, byName)

        let ordering =
            match argument "order" with
            | :? System.Collections.IEnumerable as orders ->
                [ for o in orders do
                      match o with
                      | :? IDictionary<string, obj> as entries ->
                          for KeyValue(name, direction) in entries do
                              if not (isNull direction) then
                                  match byName.TryGetValue name with
                                  | true, f -> yield f, (string direction).ToUpperInvariant() = "DESC"
                                  | _ -> raise (GraphQLException $"{typeof<'T>.Name} declares no field {name} to order by")
                      | _ -> () ]
            | _ -> []

        let ordering =
            match ordering with
            | [] when not (isNull (box orderWhenNone)) -> [ orderWhenNone, false ]
            | given -> given

        Func<Query<'T>, Query<'T>>(fun query ->
            let filtered =
                match condition with
                | Some c -> query |> BuildQuery.where (c.ToExpr())
                | None -> query

            ordering |> List.fold (fun q (f, descending) -> f.OrderBy(q, descending)) filtered)

    /// The field's where argument as a condition, if it has one.
    static member private WhereCondition(context: IResolverContext, byName: IReadOnlyDictionary<string, QueryField<'T>>) : QueryCondition<'T> option =
        if context.Selection.Field.Arguments.ContainsField "where" then
            match context.ArgumentValue<obj> "where" with
            | :? IDictionary<string, obj> as input -> PlasmRequest.Condition(byName, input)
            | _ -> None
        else
            None

    /// The request's where alone, as a function of a query, read from the
    /// context at once: what a count of a field's rows needs, which has no
    /// order (a count per parent groups by the parent, and orders by nothing).
    static member Where(context: IResolverContext, fields: QueryField<'T> seq) : Func<Query<'T>, Query<'T>> =
        let byName = Dictionary<string, QueryField<'T>>()
        for f in fields do byName.[f.Name] <- f

        let condition = PlasmRequest.WhereCondition(context, byName)

        Func<Query<'T>, Query<'T>>(fun query ->
            match condition with
            | Some c -> query |> BuildQuery.where (c.ToExpr())
            | None -> query)

    /// The field's offset paging (skip, take) per value of `key`, as a
    /// function of a query, read from the context at once: a nested field's
    /// page for every parent in one command (BuildQuery.pageEach), one row
    /// more than the page for each, so that Segment knows whether another
    /// page follows; `defaultTake` when the request gives no take.
    static member PageEach(context: IResolverContext, key: Quotations.Expr<'T -> 'K>, defaultTake: int) : Func<Query<'T>, Query<'T>> =
        let skip = defaultArg (PlasmRequest.Count(context, "skip")) 0
        let take = defaultArg (PlasmRequest.Count(context, "take")) defaultTake
        Func<Query<'T>, Query<'T>>(fun query -> query |> BuildQuery.pageEach key skip (take + 1))

    /// A name for the DataLoader of this field with these arguments: fields
    /// asked for with the same where, order, skip and take share one batch.
    static member BatchKey(context: IResolverContext) : string =
        let literal (name: string) =
            if context.Selection.Field.Arguments.ContainsField name then string (context.ArgumentLiteral<HotChocolate.Language.IValueNode> name) else ""

        context.Selection.Field.Coordinate.ToString() + "|" + literal "where" + "|" + literal "order" + "|" + literal "skip" + "|" + literal "take"

    /// The declared fields a request's selection of this field reads, on the
    /// object type of 'T's read fields (IPlasmReadType), through any types
    /// between (a page's items); __typename and the like are not fields.
    static member Chosen(context: IResolverContext, fields: ReadField<'T> seq) : ReadField<'T> list =
        let byName = Dictionary<string, ReadField<'T>>()

        for f in fields do
            byName.[f.Name] <- f

        let chosen = ResizeArray<ReadField<'T>>()

        let rec visit (selection: HotChocolate.Execution.Processing.Selection) =
            match selection.Type.NamedType() with
            | :? HotChocolate.Types.ObjectType as t when selection.HasSelections ->
                for child in context.Operation.GetSelectionSet(selection, t).Selections.ToArray() do
                    match box t with
                    | :? IPlasmReadType<'T> ->
                        match byName.TryGetValue child.Field.Name with
                        | true, f when not (chosen.Contains f) -> chosen.Add f
                        | _ -> ()
                    | _ -> visit child
            | _ -> ()

        visit context.Selection
        List.ofSeq chosen

    /// As BatchKey, with the fields chosen: requests share a batch only when
    /// they read the same fields too.
    static member BatchKey(context: IResolverContext, chosen: ReadField<'T> seq) : string =
        PlasmRequest.BatchKey context + "|" + String.Join(",", chosen |> Seq.map (fun f -> f.Name) |> Seq.sort)

    static member private Count(context: IResolverContext, name: string) : int option =
        if context.Selection.Field.Arguments.ContainsField name then
            match context.ArgumentValue<obj> name with
            | :? int as n -> Some n
            | _ -> None
        else
            None

    /// `query` paged as the field's skip and take arguments say (offset
    /// paging), `defaultTake` when the request gives no take: one row more
    /// than the page, so that Segment knows whether another page follows.
    static member Page(context: IResolverContext, query: Query<'T>, defaultTake: int) : Query<'T> =
        let skip = defaultArg (PlasmRequest.Count(context, "skip")) 0
        let take = defaultArg (PlasmRequest.Count(context, "take")) defaultTake
        let skipped = if skip > 0 then query |> BuildQuery.skip skip else query
        skipped |> BuildQuery.take (take + 1)

    /// The page of `rows` (read from a query Page made) as offset paging
    /// shows it; `total` is run only when the request selects totalCount.
    static member Segment(context: IResolverContext, rows: IReadOnlyList<'R>, defaultTake: int, total: Func<Task<int>>) : Task<CollectionSegment<'R>> =
        task {
            let skip = defaultArg (PlasmRequest.Count(context, "skip")) 0
            let take = defaultArg (PlasmRequest.Count(context, "take")) defaultTake
            let items = rows |> Seq.truncate take |> Array.ofSeq :> IReadOnlyList<'R>

            let counted = context.IsSelected "totalCount"

            let! n = if counted then total.Invoke() else Task.FromResult 0
            return CollectionSegment<'R>(items, CollectionSegmentInfo(rows.Count > take, skip > 0), n)
        }

    /// A parent's page and its total, each from a DataLoader: the page load,
    /// and the total load only when the request selects totalCount, are both
    /// started before anything is awaited. Every parent's keys then join the
    /// same two batches; a total loaded after the page is awaited joins its
    /// batch only if every parent's page comes back in time, and otherwise
    /// starts another. `page` is a query PageEach made, read one row past the
    /// page.
    static member LoadedSegment
        (context: IResolverContext, defaultTake: int, page: Func<Task<IReadOnlyList<'R>>>, total: Func<Task<int>>)
        : Task<CollectionSegment<'R>> =
        let rows = page.Invoke()
        let counted = if context.IsSelected "totalCount" then total.Invoke() else Task.FromResult 0

        task {
            let! read = rows
            return! PlasmRequest.Segment(context, read, defaultTake, (fun () -> counted))
        }

    /// As LoadedSegment, for a page load whose rows are an array.
    static member LoadedSegment
        (context: IResolverContext, defaultTake: int, page: Func<Task<'R[]>>, total: Func<Task<int>>)
        : Task<CollectionSegment<'R>> =
        PlasmRequest.LoadedSegment(
            context,
            defaultTake,
            Func<Task<IReadOnlyList<'R>>>(fun () -> task { let! rows = page.Invoke() in return rows :> IReadOnlyList<'R> }),
            total
        )
