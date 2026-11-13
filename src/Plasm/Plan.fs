namespace Plasm

open System

/// How a relationship load is executed.
type LoadStrategy =
    /// One command per batch of distinct parent keys (`key = ANY(@keys)`);
    /// the command count depends on the number of keys divided by the batch
    /// size, never on the number of parents.
    | BatchedByKeys of maxKeysPerCommand: int
    /// As BatchedByKeys, ranking related rows per parent with a window function
    /// and keeping at most `limit` for each parent.
    | PerParentWindow of limit: int * maxKeysPerCommand: int

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

    /// Every load path, depth first (e.g. "Lines", "Lines.Product", "Payments").
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
          /// Many-to-many: each row carries the owner key in a trailing column.
          Through: JoinTable option
          Strategy: LoadStrategy
          Children: LoadNode list }

    type Plan =
        { Root: Sql.Select
          RootSchema: SchemaInfo
          Loads: LoadNode list
          /// False when a closed subexpression may evaluate differently on the
          /// next execution (e.g. DateTime.UtcNow); such plans are not reused.
          Reusable: bool }

    let private raiseT e = raise (Translate.TranslationFailure e)

    /// A canonical rendering of a load customisation, used to decide whether two
    /// loads of the same relationship mean the same thing.
    let private signature (dialect: ISqlDialect) (model: Model) (vol: bool ref) (target: SchemaInfo) (def: QueryDef) =
        let ctx = Translate.newCtxWith model vol
        let wheres, orders = Translate.translateBody ctx def "c" target

        let rendered =
            Sql.renderSelect
                dialect
                { Sql.select (Sql.Table(target.Table, "c")) with
                    Where = wheres
                    OrderBy = orders }

        let ps =
            rendered.Parameters
            |> List.map (fun p ->
                match p with
                | :? Array as a -> a |> Seq.cast<obj> |> Seq.map string |> String.concat ","
                | p -> $"{p.GetType().Name}:{p}")

        rendered.Sql + " | " + String.concat ";" ps + $" | perParent=%A{def.PerParentLimit}"

    let rec private planLevel
        (dialect: ISqlDialect)
        (model: Model)
        (vol: bool ref)
        (prefix: string)
        (parent: SchemaInfo)
        (loads: LoadSpec list)
        : LoadNode list =
        let caps = dialect.Capabilities

        // Group by relationship, keeping first-request order.
        let groups =
            loads
            |> List.fold
                (fun (acc: (string * LoadSpec list) list) l ->
                    match acc |> List.tryFindIndex (fun (n, _) -> n = l.Assoc.Name) with
                    | Some i -> acc |> List.mapi (fun j (n, ls) -> if i = j then n, ls @ [ l ] else n, ls)
                    | None -> acc @ [ l.Assoc.Name, [ l ] ])
                []

        [ for name, specs in groups do
              let path = if prefix = "" then name else prefix + "." + name

              let assoc =
                  match parent.TryAssoc name with
                  | Some a -> a
                  | None -> raiseT (InvalidQuery $"{parent.Name}.{name} is not a declared relationship")

              let target =
                  match model.Schema assoc.Target with
                  | Ok s -> s
                  | Error e -> raiseT e

              // An explicit replacement discards earlier requests.
              let specs =
                  match specs |> List.tryFindIndexBack (fun s -> s.Replace) with
                  | Some i -> specs |> List.skip i
                  | None -> specs

              let defs =
                  specs |> List.map (fun s -> s.Child |> Option.defaultValue (QueryDef.Empty target.Type))

              for d in defs do
                  if d.Root <> target.Type then
                      raiseT (InvalidQuery $"load query for '{path}' is over {d.Root.Name}, expected {target.Name}")

                  if d.Limit.IsSome || d.Offset.IsSome then
                      raiseT (
                          InvalidQuery
                              $"take/skip in the load query for '{path}' is ambiguous (global or per parent?); use limitPerParent for a per-parent limit"
                      )

              let signatures =
                  match defs with
                  | [ _ ] -> [ () ]
                  | _ -> defs |> List.map (signature dialect model vol target) |> List.distinct |> List.map ignore

              if signatures.Length > 1 then
                  raiseT (
                      LoadConflict(
                          path,
                          "the relationship is loaded more than once with different filters, ordering or limits; use replacePreload to choose one"
                      )
                  )

              let def = defs.Head
              let nested = defs |> List.collect (fun d -> d.Loads)

              let strategy =
                  match def.PerParentLimit with
                  | None -> BatchedByKeys caps.MaxKeysPerCommand
                  | Some n ->
                      if assoc.Cardinality <> Many then
                          raiseT (InvalidQuery $"limitPerParent on '{path}' requires a has-many relationship")

                      if def.Orders.IsEmpty then
                          raiseT (
                              InvalidQuery $"limitPerParent on '{path}' requires an ordering to decide which rows are kept"
                          )

                      if not caps.PerParentLimit then
                          raiseT (UnsupportedByProvider($"per-parent limit for '{path}'", dialect.Name))

                      PerParentWindow(n, caps.MaxKeysPerCommand)

              let keyField, matchField =
                  match assoc.Kind with
                  | HasMany
                  | HasOne -> parent.Key, target.Field assoc.ForeignKey
                  | BelongsTo -> parent.Field assoc.ForeignKey, target.Key
                  // Matched on the join table's owner column (see Through).
                  | ManyToMany -> parent.Key, target.Key

              // Translate once; each batch only supplies its key array.
              let wheres, orders = Translate.translateBody (Translate.newCtxWith model vol) def "t0" target
              let ranked = Translate.stableOrder "t0" target orders

              let outerOrders =
                  match strategy with
                  | PerParentWindow _ ->
                      let _, o = Translate.translateBody (Translate.newCtxWith model vol) def "s" target
                      Translate.stableOrder "s" target o
                  | BatchedByKeys _ -> []

              // Many-to-many rows come through the join table, which supplies
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

              let outerColumns =
                  (target.Fields |> Array.toList |> List.map (fun f -> Sql.Column("s", f.Column), f.Column))
                  @ (ownerColumn |> List.map (fun (_, n) -> Sql.Column("s", n), n))

              let build (keys: Array) : Sql.Select =
                  let keyFilter = Sql.AnyOf(matchExpr, keys)

                  match strategy with
                  | BatchedByKeys _ ->
                      { Sql.select (Sql.Table(target.Table, "t0")) with
                          Columns = columns
                          Joins = joins
                          Where = keyFilter :: wheres
                          OrderBy = ranked }
                  | PerParentWindow(n, _) ->
                      let inner =
                          { Sql.select (Sql.Table(target.Table, "t0")) with
                              Columns = columns @ [ Sql.RowNumber(matchExpr, ranked), "__rank" ]
                              Joins = joins
                              Where = keyFilter :: wheres }

                      { Sql.select (Sql.Subquery(inner, "s")) with
                          Columns = outerColumns
                          Where = [ Sql.Binary("<=", Sql.Column("s", "__rank"), Sql.Param(box (int64 n))) ]
                          OrderBy = outerOrders }

              { Path = path
                Assoc = assoc
                Parent = parent
                Target = target
                KeyField = keyField
                MatchField = matchField
                Build = build
                Through = assoc.Through
                Strategy = strategy
                Children = planLevel dialect model vol path target nested } ]

    let planLoads dialect model (schema: SchemaInfo) (loads: LoadSpec list) =
        Translate.wrap (fun () -> planLevel dialect model (ref false) "" schema loads)

    let plan (dialect: ISqlDialect) (model: Model) (def: QueryDef) : Result<Plan, TranslationError> =
        let vol = ref false

        match Translate.rootSelect model vol def with
        | Error e -> Error e
        | Ok(root, schema) ->
            Translate.wrap (fun () -> planLevel dialect model vol "" schema def.Loads)
            |> Result.map (fun loads ->
                { Root = root
                  RootSchema = schema
                  Loads = loads
                  Reusable = not vol.Value })

    let rec private describeNode (dialect: ISqlDialect) (n: LoadNode) : LoadStep =
        let sample = n.Build(Array.CreateInstance(n.KeyField.ProviderType, 1))

        { Path = n.Path
          Relationship = $"{n.Assoc.Kind}"
          Target = n.Target.Name
          Strategy = n.Strategy
          Sql = (Sql.renderSelect dialect sample).Sql
          Children = n.Children |> List.map (describeNode dialect) }

    let describe (dialect: ISqlDialect) (p: Plan) : QueryPlan =
        { Sql = (Sql.renderSelect dialect p.Root).Sql
          Loads = p.Loads |> List.map (describeNode dialect) }
