namespace Plasm

open System

/// How a relationship load is executed.
[<RequireQualifiedAccess>]
type LoadStrategy =
    /// One command per batch of distinct parent keys (`key = ANY(@keys)`);
    /// the command count depends on the number of keys divided by the batch
    /// size, never on the number of parents.
    | BatchedByKeys of maxKeysPerCommand: int

/// An inspectable description of one relationship load.
type LoadStep =
    { Path: string
      Relationship: string
      Target: string
      Strategy: LoadStrategy
      /// SQL for one batch; the key list is a single parameter.
      Sql: string
      Children: LoadStep list }

/// An inspectable execution plan. Producing it performs no database access.
type QueryPlan =
    { Sql: string
      Loads: LoadStep list }

    /// Every load path, depth first (e.g. "Lines", "Lines.Notes", "Tags").
    member p.LoadPaths =
        let rec walk (steps: LoadStep list) =
            steps |> List.collect (fun s -> s.Path :: walk s.Children)

        walk p.Loads

module internal Planner =

    type LoadNode =
        { Path: string
          Assoc: AssocMap
          Parent: SchemaInfo
          Target: SchemaInfo
          /// Parent field whose values are the keys sent to the database.
          KeyField: FieldMap
          /// Related field matched against those keys.
          MatchField: FieldMap
          /// Builds the command for one batch of keys. Filters and ordering are
          /// translated once, when the plan is made.
          Build: Array -> Sql.Select
          /// A linked list: each row carries the owner key in a trailing column.
          Through: JoinTable option
          Strategy: LoadStrategy
          /// Arguments of a bound prepared query used as the load query; its
          /// parameters bind to these rather than to the root query's.
          Args: obj[]
          Children: LoadNode list }

    type Plan =
        { Root: Sql.Select
          RootSchema: SchemaInfo
          Loads: LoadNode list
          /// False when a closed subexpression may evaluate differently on the
          /// next execution (e.g. DateTime.UtcNow); such plans are not reused.
          Reusable: bool }

    let private raiseT e = raise (Translate.TranslationFailure e)

    /// The loads of what `parent` owns or links (owned-collections.feature):
    /// every owned or linked field, at every level, in its declared order (else
    /// by key), one batched command per field.
    let rec private planLevel (dialect: ISqlDialect) (model: Model) (vol: bool ref) (prefix: string) (parent: SchemaInfo) : LoadNode list =
        let caps = dialect.Capabilities

        [ for assoc in parent.Assocs do
              let path = if prefix = "" then assoc.Name else prefix + "." + assoc.Name

              let target =
                  match model.Schema assoc.Target with
                  | Ok s -> s
                  | Error e -> raiseT e

              let order =
                  match assoc.Order with
                  | Some o -> o
                  | None ->
                      let v = Quotations.Var("x", target.Type)
                      Quotations.Expr.Lambda(v, Quotations.Expr.PropertyGet(Quotations.Expr.Var v, target.Shape.Fields.[target.Key.Index]))

              let def = { QueryDef.Empty target.Type with Orders = [ order, SortDirection.Ascending ] }
              let strategy = LoadStrategy.BatchedByKeys caps.MaxKeysPerCommand

              let keyField, matchField =
                  match assoc.Kind with
                  | AssocKind.HasMany
                  | AssocKind.HasOne -> parent.Key, target.Field assoc.ForeignKey
                  // Matched on the join table's owner column (see Through).
                  | AssocKind.ManyToMany -> parent.Key, target.Key

              // Translate once; each batch only supplies its key array.
              let wheres, orders = Translate.translateBody (Translate.newCtxWith model vol) def "t0" target
              let ranked = Translate.stableOrder "t0" target orders

              // A linked list's rows come through the join table, which supplies
              // the owner key as a trailing column.
              let matchExpr, joins, ownerColumn =
                  match assoc.Through with
                  | Some j ->
                      Sql.Column("j", j.OwnerColumn),
                      [ ({ Left = false
                           Table = j.Table
                           Alias = "j"
                           On = Sql.Binary("=", Sql.Column("j", j.TargetColumn), Sql.Column("t0", target.Key.Column)) }: Sql.Join) ],
                      [ Sql.Column("j", j.OwnerColumn), "__owner" ]
                  | None -> Sql.Column("t0", matchField.Column), [], []

              let columns = Translate.entityColumns "t0" target @ ownerColumn

              let build (keys: Array) : Sql.Select =
                  let keyFilter = Sql.AnyOf(matchExpr, keys, Sql.storeType matchField)

                  { Sql.select (Sql.Table(target.Table, "t0")) with
                      Columns = columns
                      Joins = joins
                      Where = keyFilter :: wheres
                      OrderBy = ranked }

              { Path = path
                Assoc = assoc
                Parent = parent
                Target = target
                KeyField = keyField
                MatchField = matchField
                Build = build
                Through = assoc.Through
                Strategy = strategy
                Args = [||]
                Children = planLevel dialect model vol path target } ]

    let planLoads dialect model (schema: SchemaInfo) =
        Translate.wrap (fun () -> planLevel dialect model (ref false) "" schema)

    let plan (dialect: ISqlDialect) (model: Model) (def: QueryDef) : Result<Plan, TranslationError> =
        let vol = ref false

        match Translate.rootSelect model vol def with
        | Error e -> Error e
        | Ok(root, schema) ->
            Translate.wrap (fun () -> planLevel dialect model vol "" schema)
            |> Result.map (fun loads ->
                { Root = root
                  RootSchema = schema
                  Loads = loads
                  Reusable = not vol.Value })

    /// One `including` (including.feature): the inner query as one more load
    /// in batches of its outer level's values.
    type IncludingLevel =
        { Parts: Translate.IncludingParts
          /// What the inner records own or link, loaded with them.
          Children: LoadNode list
          /// The inner query's own `including`s, in the order written.
          Nested: IncludingLevel list }

    /// A query ending in `including`s: the outer plan and each list's level.
    type Including =
        { Outer: Plan
          Levels: IncludingLevel list
          Strategy: LoadStrategy }

    let including (dialect: ISqlDialect) (model: Model) (def: QueryDef) : Result<Including, TranslationError> =
        match def.Includings with
        | [] -> Error(TranslationError.InvalidQuery "the query does not end in including")
        | lambdas ->
            let vol = ref false

            let parts =
                lambdas
                |> List.fold
                    (fun acc l ->
                        match acc, Translate.includingParts model vol def l with
                        | Ok ps, Ok p -> Ok(ps @ [ p ])
                        | (Error _ as e), _ -> e
                        | _, Error e -> Error e)
                    (Ok [])

            // Two loads of one type at one level could be swapped and still
            // compile: refused, naming them (including.feature).
            let rec sameTyped (inner: bool) (ps: Translate.IncludingParts list) =
                let ordinal n =
                    match n % 100, n % 10 with
                    | (11 | 12 | 13), _ -> $"{n}th"
                    | _, 1 -> $"{n}st"
                    | _, 2 -> $"{n}nd"
                    | _, 3 -> $"{n}rd"
                    | _ -> $"{n}th"

                let indexed = ps |> List.mapi (fun i p -> i + 1, p)

                let clash =
                    indexed
                    |> List.tryPick (fun (i, p) ->
                        indexed |> List.tryFind (fun (j, q) -> j > i && q.HeldType = p.HeldType) |> Option.map (fun (j, _) -> i, j, p))

                match clash with
                | Some(i, j, p) ->
                    let where = if inner then " of the inner query" else ""
                    let typeName = Reflection.fsharpTypeName p.HeldType

                    Some(
                        TranslationError.InvalidQuery
                            $"the {ordinal i} and {ordinal j} includings{where} both give a {typeName}, so the result's type cannot tell them apart and the two written in the other order would still compile. Give each a type of its own: select a one-case type in each inner query, as in select (NewestTitle c.Title)"
                    )
                | None -> ps |> List.tryPick (fun p -> sameTyped true p.Nested)

            let parts =
                match parts with
                | Ok ps ->
                    match sameTyped false ps with
                    | Some e -> Error e
                    | None -> Ok ps
                | e -> e

            match parts with
            | Error e -> Error e
            | Ok parts ->
                match plan dialect model { def with Includings = [] } with
                | Error e -> Error e
                | Ok outer ->
                    Translate.wrap (fun () ->
                        let rec level (p: Translate.IncludingParts) =
                            { Parts = p
                              Children = if p.Projected.IsSome then [] else planLevel dialect model vol "" p.Target
                              Nested = p.Nested |> List.map level }

                        let levels = parts |> List.map level

                        { Outer = { outer with Reusable = outer.Reusable && not vol.Value }
                          Levels = levels
                          Strategy = LoadStrategy.BatchedByKeys dialect.Capabilities.MaxKeysPerCommand })

    let rec private describeNode (dialect: ISqlDialect) (n: LoadNode) : LoadStep =
        let sample = n.Build(Array.CreateInstance(n.KeyField.ProviderType, 1))

        { Path = n.Path
          Relationship =
            if n.Assoc.Linked then "links"
            elif n.Assoc.Cardinality = Many then "owns"
            else "owns one"
          Target = n.Target.Name
          Strategy = n.Strategy
          Sql = (Sql.renderSelect dialect sample).Sql
          Children = n.Children |> List.map (describeNode dialect) }

    let describe (dialect: ISqlDialect) (p: Plan) : QueryPlan =
        { Sql = (Sql.renderSelect dialect p.Root).Sql
          Loads = p.Loads |> List.map (describeNode dialect) }

    let describeIncluding (dialect: ISqlDialect) (p: Including) : QueryPlan =
        let outer = describe dialect p.Outer

        let rec step (l: IncludingLevel) : LoadStep =
            let sample = l.Parts.Build(Array.CreateInstance(l.Parts.MatchField.ProviderType, 1))

            { Path = "including"
              Relationship = if l.Parts.One then "includes one" else "includes"
              Target = l.Parts.Target.Name
              Strategy = p.Strategy
              Sql = (Sql.renderSelect dialect sample).Sql
              Children = (l.Children |> List.map (describeNode dialect)) @ (l.Nested |> List.map step) }

        { outer with Loads = outer.Loads @ (p.Levels |> List.map step) }
