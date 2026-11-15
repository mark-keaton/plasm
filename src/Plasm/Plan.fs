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
          /// Many-to-many: each row carries the owner key in a trailing column.
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
