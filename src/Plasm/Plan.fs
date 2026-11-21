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

    /// The rows whose `matchField` (a reference over several columns) is one of
    /// `keys`, values of the owner's key of several parts: one array per part,
    /// read back in rows by unnest, so the command's text does not grow with
    /// the keys (composite-references.feature).
    let compositeKeys (alias: string) (keyField: FieldMap) (matched: (string * ColumnPart) list) (keys: Array) : Sql.Expr =
        let values = [ for k in keys -> keyField.ToColumns k ]
        let parts = matched |> List.map snd

        let lists =
            parts
            |> List.mapi (fun i p ->
                match Model.arrayOf p.ProviderType with
                | Some(_, make) -> make [ for v in values -> v.[i] ] :?> Array
                | None -> raiseT (TranslationError.InvalidQuery $"a key part of {p.ProviderType.Name} cannot be sent as an array"))

        Sql.RowsInLists(matched |> List.map (fun (name, _) -> Sql.Column(alias, name)), lists, parts |> List.map Sql.partStoreType)

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

              // By the key, else by its parts, for a key of several parts has no
              // order of its own (composite-links.feature).
              let orders =
                  match assoc.Order with
                  | Some o -> [ o ]
                  | None ->
                      let v = Quotations.Var("x", target.Type)
                      let key = Reflection.Quoted.propertyGet (Quotations.Expr.Var v) target.Shape.Fields.[target.Key.Index]

                      if target.Key.Composite then
                          [ for part in (Shape.record target.Key.FieldType).Fields -> Quotations.Expr.Lambda(v, Reflection.Quoted.propertyGet key part) ]
                      else
                          [ Quotations.Expr.Lambda(v, key) ]

              let def = { QueryDef.Empty target.Type with Orders = [ for o in orders -> o, SortDirection.Ascending ] }
              let strategy = LoadStrategy.BatchedByKeys caps.MaxKeysPerCommand

              let keyField, matchField =
                  match assoc.Kind with
                  | AssocKind.HasMany
                  | AssocKind.HasOne -> parent.Key, target.Field assoc.ForeignKey
                  // Matched on the join table's owner column (see Through).
                  | AssocKind.ManyToMany -> parent.Key, target.Key

              // Translate once; each batch only supplies its key array.
              // Read by no variable: each table under its own name.
              let ctx = Translate.newCtxWith model vol
              let alias = ctx.Name(None, target.Table)
              let wheres, orders = Translate.translateBody ctx def alias target
              let ranked = Translate.stableOrder alias target orders

              // A linked list's rows come through the join table, which supplies
              // the owner key as trailing columns, one per part
              // (composite-links.feature).
              let matchAlias, matched, joins, ownerColumns =
                  match assoc.Through with
                  | Some j ->
                      let link = ctx.Name(None, j.Table)
                      let owners = Joins.owners j parent.Key
                      let targets = Joins.targets j target.Key

                      let on =
                          List.zip targets target.Key.Parts
                          |> List.map (fun ((column, _), part) -> Sql.Binary("=", Sql.Column(link, column), Sql.Column(alias, part.Name)))
                          |> Sql.conj

                      link,
                      owners,
                      [ ({ Left = false
                           Table = j.Table
                           Alias = link
                           On = on
                           Sub = None }: Sql.Join) ],
                      owners |> List.mapi (fun i (column, _) -> Sql.Column(link, column), (if i = 0 then "__owner" else $"__owner{i}"))
                  | None -> alias, matchField.Parts |> List.map (fun p -> p.Name, p), [], []

              let columns = Translate.entityColumns alias target @ ownerColumns

              let build (keys: Array) : Sql.Select =
                  let keyFilter =
                      if keyField.Composite then
                          compositeKeys matchAlias keyField matched keys
                      else
                          let column, _ = matched.Head
                          Sql.AnyOf(Sql.Column(matchAlias, column), keys, Sql.storeType matchField)

                  { Sql.select (Sql.Table(target.Table, alias)) with
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

    /// One load bound by `let!` or `and!` (loads.feature): its query as one
    /// more command in batches of its outer level's values.
    type IncludingLevel =
        { Parts: Translate.IncludingParts
          /// What the inner records own or link, loaded with them.
          Children: LoadNode list
          /// The inner query's own loads, in the order written.
          Nested: IncludingLevel list }

    /// A query that binds loads: the outer plan and each load's level.
    type Including =
        { Outer: Plan
          Levels: IncludingLevel list
          Strategy: LoadStrategy
          /// The row `return` builds.
          Return: Translate.ReturnShape }

    let including (dialect: ISqlDialect) (model: Model) (def: QueryDef) : Result<Including, TranslationError> =
        match def.Includings with
        | [] -> Error(TranslationError.InvalidQuery "the query binds no loads")
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
                          Strategy = LoadStrategy.BatchedByKeys dialect.Capabilities.MaxKeysPerCommand
                          // A query binds loads only with let! and return.
                          Return = (Translate.returnShape def).Value })

    let rec private describeNode (dialect: ISqlDialect) (n: LoadNode) : LoadStep =
        let sample =
            if n.KeyField.Composite then n.Build(Array.Empty<obj>())
            else n.Build(Array.CreateInstance(n.KeyField.ProviderType, 1))

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

            { Path = l.Parts.Name
              Relationship = if l.Parts.One then "binds one" else "binds"
              Target = l.Parts.Target.Name
              Strategy = p.Strategy
              Sql = (Sql.renderSelect dialect sample).Sql
              Children = (l.Children |> List.map (describeNode dialect)) @ (l.Nested |> List.map step) }

        { outer with Loads = outer.Loads @ (p.Levels |> List.map step) }
