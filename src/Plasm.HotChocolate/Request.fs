namespace Plasm.HotChocolate

open System
open System.Collections.Generic
open HotChocolate
open HotChocolate.Data.Filters
open HotChocolate.Data.Sorting
open HotChocolate.Resolvers
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

        let filtered =
            match argument "where" with
            | :? IDictionary<string, obj> as input ->
                match PlasmRequest.Condition(byName, input) with
                | Some c -> query |> BuildQuery.where (c.ToExpr())
                | None -> query
            | _ -> query

        match argument "order" with
        | :? System.Collections.IEnumerable as orders ->
            [ for o in orders do
                  match o with
                  | :? IDictionary<string, obj> as entries ->
                      for KeyValue(name, direction) in entries do
                          if not (isNull direction) then yield name, direction
                  | _ -> () ]
            |> List.fold
                (fun q (name, direction) ->
                    match byName.TryGetValue name with
                    | true, f -> f.OrderBy(q, (string direction).ToUpperInvariant() = "DESC")
                    | _ -> raise (GraphQLException $"{typeof<'T>.Name} declares no field {name} to order by"))
                filtered
        | _ -> filtered
