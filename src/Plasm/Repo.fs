namespace Plasm

open System
open System.Data
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// Why a write did not happen (or was rolled back). Cases are qualified
/// (`WriteError.Invalid`) so they never collide with domain unions.
[<RequireQualifiedAccess>]
type WriteError<'T> =
    /// Validation or declared-constraint errors; inspect `Changeset.Errors`.
    | Invalid of Changeset<'T>
    /// The stored version differs from the expected one (or the row is gone).
    | Stale of Changeset<'T>
    /// A database failure not described by the changeset's constraints.
    | Failed of DbFailure
    /// The row was written but could not be read back into the domain model
    /// (for example a trigger stored a value the model rejects). A write that
    /// ran in a transaction (one with children or links) is rolled back; a
    /// single-row write is not.
    | Materialization of MaterializationError
    /// The write could not be translated, or the provider cannot perform it
    /// (`TranslationError.UnsupportedByProvider`, naming the missing
    /// capability). Nothing was sent to the database.
    | Translation of TranslationError

/// Text for query displays (F# Interactive): SQL broken before its top-level
/// clauses, with parameter values.
module internal QueryDisplay =
    open System.Text

    let private clauses =
        [ " FROM "; " INNER JOIN "; " LEFT JOIN "; " WHERE "; " GROUP BY "; " HAVING "; " ORDER BY "; " LIMIT "; " OFFSET " ]

    /// Breaks the SQL before clauses that are outside parentheses and literals.
    let pretty (sql: string) =
        let sb = StringBuilder()
        let mutable depth = 0
        let mutable quoted = false
        let mutable i = 0

        while i < sql.Length do
            let c = sql.[i]

            if c = '\'' then quoted <- not quoted

            if not quoted then
                if c = '(' then depth <- depth + 1
                elif c = ')' then depth <- depth - 1

            let clause =
                if depth = 0 && not quoted then
                    clauses |> List.tryFind (fun k -> String.CompareOrdinal(sql, i, k, 0, k.Length) = 0)
                else
                    None

            match clause with
            | Some k ->
                sb.Append('\n').Append(k.TrimStart()) |> ignore
                i <- i + k.Length
            | None ->
                sb.Append c |> ignore
                i <- i + 1

        sb.ToString()

    let rec value (args: obj[]) (v: obj) =
        match v with
        | null -> "NULL"
        | v when v = box DBNull.Value -> "NULL"
        | :? Sql.Bound as b ->
            // A prepared-query argument: its bound value, and which argument it
            // is (Arg(1).Value -> argument 1).
            let label =
                Text.RegularExpressions.Regex.Replace(b.Label, @"Arg\((\d+)\)\.Value|\barg(\d+)\b", "argument $1$2")

            if args.Length > 0 then
                $"{value [||] (b.Resolve args)}  ({label})"
            else
                $"<{label}>"
        | v -> sprintf "%A" v

    let indent (prefix: string) (text: string) =
        text.Split('\n') |> Array.map (fun l -> prefix + l) |> String.concat "\n"

    let command (dialect: ISqlDialect) (showValues: bool) (args: obj[]) (cmd: Sql.Command) =
        let parameters =
            cmd.Parameters
            |> List.mapi (fun i p ->
                if showValues then
                    $"    {dialect.ParameterName i} = {value args p}"
                else
                    $"    {dialect.ParameterName i} = (value hidden; set IncludeParameterValues to show it)")

        String.concat "\n" (indent "  " (pretty cmd.Sql) :: parameters)

    let rec loads (steps: LoadStep list) =
        steps
        |> List.collect (fun l ->
            let header = $"  then {l.Path} ({l.Relationship}, {l.Target}), one command per batch of keys:"
            [ header; indent "  " (pretty l.Sql) ] @ loads l.Children)

/// The explicit boundary where persistence descriptions are executed. Every
/// member performs database work; nothing else in the library does.
type Repo(model: Model, provider: IProvider, ?options: RepoOptions) =
    let options = defaultArg options RepoOptions.Default

    // Queries are immutable values, so a plan can be reused for the same value,
    // unless translation evaluated something volatile (Plan.Reusable = false).
    let plans = ConditionalWeakTable<QueryDef, Planner.Plan>()
    let oneDefs = ConditionalWeakTable<QueryDef, QueryDef>()

    // How many plans this repo has made (for tests: cache behaviour).
    let mutable planned = 0

    let planFor (def: QueryDef) =
        // A prepared query is planned once, through its template.
        let def = defaultArg def.Template def

        match plans.TryGetValue def with
        | true, p -> Ok p
        | _ ->
            Threading.Interlocked.Increment(&planned) |> ignore

            match Planner.plan provider.Dialect model def with
            | Ok p when p.Reusable ->
                plans.AddOrUpdate(def, p)
                Ok p
            | other -> other

    let withArgs (args: obj[]) (ct: CancellationToken) (snapshot: bool) (body: Execution.Session -> Task<'a>) : Task<'a> =
        task {
            use! conn = provider.OpenConnection ct

            let! tx =
                if snapshot then
                    provider.BeginSnapshot(conn, ct)
                else
                    Task.FromResult None

            let session: Execution.Session =
                { Provider = provider
                  Options = options
                  Connection = conn
                  Transaction = tx
                  Ct = ct
                  Args = args }

            try
                let! r = body session

                match tx with
                | Some t -> do! t.CommitAsync(ct)
                | None -> ()

                return r
            finally
                tx |> Option.iter (fun t -> t.Dispose())
        }

    let withSession ct snapshot body = withArgs [||] ct snapshot body

    // Parameter values appear in a query's display only when this repo's
    // diagnostics include them, or when the query's types were defined in F#
    // Interactive (where the person typing is the one who supplied them).
    let showValues (def: QueryDef) =
        options.IncludeParameterValues
        || (Reflection.Fsi.submission def.Root).IsSome
        || def.Joins |> List.exists (fun j -> (Reflection.Fsi.submission j.Target).IsSome)

    // Lets query values display their SQL; held by this repo, registered weakly.
    let describer =
        { new IQueryDescriber with
            member _.Knows t = (model.TrySchema t).IsSome || (model.Redefinition t).IsSome

            member _.Describe def =
                let text =
                    // Planned afresh, never cached: displaying a query must not
                    // change how it later runs.
                    if def.Joins.IsEmpty then
                        Planner.plan provider.Dialect model (defaultArg def.Template def)
                        |> Result.map (fun p ->
                            let plan = Planner.describe provider.Dialect p
                            QueryDisplay.command provider.Dialect (showValues def) def.Args (Sql.renderSelect provider.Dialect p.Root) :: QueryDisplay.loads plan.Loads)
                    else
                        Translate.projection model (ref false) def None
                        |> Result.map (fun (select, _) -> [ QueryDisplay.command provider.Dialect (showValues def) def.Args (Sql.renderSelect provider.Dialect select) ])

                // F# Interactive puts a long first line on its own line,
                // indented by two like the rest.
                match text with
                | Ok lines -> (String.concat "\n" lines).TrimStart()
                | Error e -> (QueryDisplay.indent "  " $"cannot be translated: {e}").TrimStart() }

    do Describers.register describer

    let single (select: Sql.Select) (args: obj[]) (ct: CancellationToken) (read: Data.Common.DbDataReader -> 'a) =
        withArgs args ct false (fun s ->
            task {
                let! rows = Execution.query s (Sql.renderSelect provider.Dialect select) read
                return rows
            })

    member _.Model = model
    member _.Provider = provider
    member internal _.Options = options
    member internal _.PlansMade = planned

    // Keeps the describer (registered weakly) alive as long as this repo.
    member internal _.Describer = describer

    /// Describes how a query would execute. Performs no database access.
    member _.Explain(query: Query<'T>) : Result<QueryPlan, TranslationError> =
        if query.Def.Joins.IsEmpty then
            planFor query.Def |> Result.map (Planner.describe provider.Dialect)
        else
            Translate.projection model (ref false) query.Def None
            |> Result.map (fun (select, _) ->
                { Sql = (Sql.renderSelect provider.Dialect select).Sql
                  Loads = [] })

    /// Describes how a projection would execute. Performs no database access.
    member _.Explain(projection: Projection<'R>) : Result<QueryPlan, TranslationError> =
        Translate.projection model (ref false) projection.Def (Some projection.Select)
        |> Result.map (fun (select, _) ->
            { Sql = (Sql.renderSelect provider.Dialect select).Sql
              Loads = [] })

    /// Executes a query, loading requested relationships with batched queries.
    member _.All(query: Query<'T>, ?consistency: ReadConsistency, ?cancellationToken: CancellationToken) =
        let ct = defaultArg cancellationToken CancellationToken.None
        let consistency = defaultArg consistency ReadConsistency.Independent

        Execution.guard provider (fun () ->
            task {
              if not query.Def.Joins.IsEmpty then
                // A joined query returns combined rows: read it as a projection.
                match Translate.projection model (ref false) query.Def None with
                | Error e -> return Error(DbError.Translation e)
                | Ok(select, shape) ->
                    let! rows = withArgs query.Def.Args ct false (fun s -> Execution.readProjection s select shape query.Def.Root.Name)
                    return Ok(rows |> List.map unbox<'T>)
              else
                match planFor query.Def with
                | Error e -> return Error(DbError.Translation e)
                | Ok plan ->
                    let snapshot = consistency = ReadConsistency.Snapshot && not plan.Loads.IsEmpty

                    if snapshot && not provider.Dialect.Capabilities.SnapshotReads then
                        return Error(DbError.Translation(TranslationError.UnsupportedByProvider("snapshot reads", provider.Dialect.Name)))
                    else
                        let! rows = withArgs query.Def.Args ct snapshot (fun s -> Execution.readRoot s plan)
                        return Ok(rows |> List.map unbox<'T>)
            })

    /// Executes a projection. Only the projected columns are read.
    member _.All(projection: Projection<'R>, ?cancellationToken: CancellationToken) =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                match Translate.projection model (ref false) projection.Def (Some projection.Select) with
                | Error e -> return Error(DbError.Translation e)
                | Ok(select, shape) ->
                    let! rows =
                        withArgs projection.Def.Args ct false (fun s -> Execution.readProjection s select shape projection.Def.Root.Name)

                    return Ok(rows |> List.map unbox<'R>)
            })

    /// Counts the rows a query matches (ignoring relationship loads) with one
    /// command that transfers a single row. A page counts the rows of the page;
    /// a grouped query counts its groups.
    member _.Count(query: Query<'T>, ?cancellationToken: CancellationToken) : Task<Result<int64, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                match Translate.countSelect model (ref false) query.Def with
                | Error e -> return Error(DbError.Translation e)
                | Ok select ->
                    let! rows = single select query.Def.Args ct (fun r -> Convert.ToInt64(r.GetValue 0))
                    return Ok rows.[0]
            })

    /// Whether a query matches any row. Reads at most one row.
    member _.Exists(query: Query<'T>, ?cancellationToken: CancellationToken) : Task<Result<bool, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                match Translate.existsSelect model (ref false) query.Def with
                | Error e -> return Error(DbError.Translation e)
                | Ok select ->
                    let! rows = single select query.Def.Args ct ignore
                    return Ok(rows.Count > 0)
            })

    /// Executes a projection expected to return at most one row, such as an
    /// aggregate without groupBy (which always returns exactly one).
    member this.One(projection: Projection<'R>, ?cancellationToken: CancellationToken) : Task<Result<'R option, DbError>> =
        task {
            match! this.All(projection, cancellationToken = defaultArg cancellationToken CancellationToken.None) with
            | Ok [] -> return Ok None
            | Ok [ x ] -> return Ok(Some x)
            | Ok _ -> return Error DbError.MoreThanOneRow
            | Error e -> return Error e
        }

    /// Describes the SQL a bulk write would send. Performs no database access.
    member _.Explain(write: BulkWrite<'R>) : Result<QueryPlan, TranslationError> =
        BulkExec.commands model provider.Dialect write.Def
        |> Result.map (fun cmds ->
            { Sql = BulkExec.sqlOf cmds |> String.concat ";\n"
              Loads = [] })

    /// Runs a bulk write. Several commands (very large inserts) run in one
    /// transaction; an empty insert sends nothing.
    member _.Execute(write: BulkWrite<'R>, ?cancellationToken: CancellationToken) : Task<Result<'R, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                match BulkExec.commands model provider.Dialect write.Def with
                | Error e -> return Error(DbError.Translation e)
                | Ok [] -> return Ok(write.Finish(box ([]: obj list)))
                | Ok cmds ->
                    let args =
                        match write.Def with
                        | UpdateAll(q, _)
                        | DeleteAll q -> q.Args
                        | InsertAll _ -> [||]

                    use! conn = provider.OpenConnection ct

                    let! tx =
                        if cmds.Length > 1 then
                            task {
                                let! t = provider.BeginTransaction(conn, IsolationLevel.ReadCommitted, ct)
                                return Some t
                            }
                        else
                            Task.FromResult None

                    try
                        let session: Execution.Session =
                            { Provider = provider
                              Options = options
                              Connection = conn
                              Transaction = tx
                              Ct = ct
                              Args = args }

                        let! v = BulkExec.run session write.Def cmds

                        match tx with
                        | Some t -> do! t.CommitAsync(ct)
                        | None -> ()

                        return Ok(write.Finish v)
                    finally
                        tx |> Option.iter (fun t -> t.Dispose())
            })

    /// Runs hand-written SQL, reading each row as `'R`: a mapped record
    /// (columns by name, relationships not loaded), another record (fields by
    /// name or snake_case name), a tuple (columns in order) or a scalar.
    /// Interpolated values are always command parameters:
    /// `repo.Sql<Order>($"SELECT * FROM orders WHERE total > {minimum}")`.
    member _.Sql<'R>(sql: FormattableString, ?cancellationToken: CancellationToken) : Task<Result<'R list, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                let cmd = Execution.rawCommand model provider.Dialect sql
                let read = Execution.rawReader model typeof<'R> "SQL"
                let! rows = withSession ct false (fun s -> Execution.query s cmd read)
                return Ok(rows |> Seq.map unbox<'R> |> List.ofSeq)
            })

    /// Runs a hand-written command, returning the number of affected rows.
    /// Interpolated values are always command parameters.
    member _.ExecuteSql(sql: FormattableString, ?cancellationToken: CancellationToken) : Task<Result<int, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                let cmd = Execution.rawCommand model provider.Dialect sql
                let! n = withSession ct false (fun s -> Execution.execute s cmd)
                return Ok n
            })

    /// Compares the model with the live database catalogue and reports every
    /// difference that would make a mapped read or write fail: missing tables
    /// and columns (join tables included), incompatible column types,
    /// non-optional fields over nullable columns, and NOT NULL columns the
    /// model cannot write. An empty list means none was found. Reads only the
    /// catalogue; it does not create or migrate anything.
    member _.CheckSchema(?cancellationToken: CancellationToken) : Task<Result<SchemaIssue list, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                let! issues = withSession ct false (fun s -> SchemaCheck.run model s)
                return Ok issues
            })

    /// F# source for a record and a schema that map `table`, generated from the
    /// database catalogue: column types become F# types, nullable columns
    /// become options, the primary key becomes `key` (or `identityKey` when the
    /// database generates it), and columns with no F# type in this dialect are
    /// left out with a comment. Reads only the catalogue.
    member _.Scaffold(table: string, ?cancellationToken: CancellationToken) : Task<Result<Scaffold, ScaffoldError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        task {
            let! r =
                Execution.guard provider (fun () ->
                    task {
                        let! r = withSession ct false (fun s -> Scaffolding.run table s)
                        return Ok r
                    })

            match r with
            | Ok inner -> return inner
            | Error e -> return Error(ScaffoldError.Failed e)
        }

    /// Runs `body` with a repository whose work happens inside one database
    /// transaction that is rolled back when `body` finishes: a test sandbox
    /// (Ecto's SQL sandbox). Transactions inside it become savepoints, each
    /// operation runs in its own savepoint so a database error does not end
    /// the sandbox, and operations run one at a time. Isolation levels and
    /// snapshot reads inside a sandbox see the sandbox's transaction.
    member _.Sandbox(body: Repo -> Task<'T>, ?cancellationToken: CancellationToken) : Task<'T> =
        let ct = defaultArg cancellationToken CancellationToken.None

        task {
            use! conn = provider.OpenConnection ct
            use! tx = provider.BeginTransaction(conn, IsolationLevel.ReadCommitted, ct)
            let sandboxProvider = SandboxProvider(provider, conn, tx)
            let sandbox = Repo(model, sandboxProvider, options)

            try
                return! body sandbox
            finally
                sandboxProvider.End()

                try
                    tx.Rollback()
                with _ ->
                    ()
        }

    /// Executes a query expected to return at most one row.
    member this.One(query: Query<'T>, ?cancellationToken: CancellationToken) =
        task {
            // Remembered per query value, so repeated lookups reuse one plan.
            let limited = Query<'T>(oneDefs.GetValue(query.Def, fun d -> Repo.LimitForOne(model, d)))

            match! this.All(limited, cancellationToken = defaultArg cancellationToken CancellationToken.None) with
            | Ok [] -> return Ok None
            | Ok [ x ] -> return Ok(Some x)
            | Ok _ -> return Error DbError.MoreThanOneRow
            | Error e -> return Error e
        }

    /// The query `One` runs: at most two rows (enough to detect a second
    /// match), never more than the query's own limit, ordered by key when the
    /// query has no ordering.
    static member private LimitForOne(model: Model, d: QueryDef) : QueryDef =
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

    /// Loads relationships onto values already in hand, returning new values.
    /// `loads` is a query whose preloads are applied; it must not filter, order
    /// or page. The given values are not modified. Requested relationships are
    /// always (re)loaded.
    member _.Preload(loads: Query<'T>, values: 'T list, ?cancellationToken: CancellationToken) =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                let d = loads.Def

                if not d.Wheres.IsEmpty || not d.Orders.IsEmpty || d.Limit.IsSome || d.Offset.IsSome then
                    return
                        Error(DbError.Translation(TranslationError.InvalidQuery "a load specification for existing values may only contain preloads"))
                else
                    match model.Schema typeof<'T> with
                    | Error e -> return Error(DbError.Translation e)
                    | Ok schema ->
                        match Planner.planLoads provider.Dialect model schema d.Loads with
                        | Error e -> return Error(DbError.Translation e)
                        | Ok nodes when values.IsEmpty || nodes.IsEmpty -> return Ok values
                        | Ok nodes ->
                            let rows =
                                values |> List.map (fun v -> Array.copy (schema.Shape.Deconstruct(box v))) |> Array.ofList

                            do! withSession ct false (fun s -> Execution.loadInto s rows nodes)
                            return Ok(rows |> Array.map (schema.Shape.Construct >> unbox<'T>) |> List.ofArray)
            })

    member private _.WriteOne(cs: Changeset<'T>, ct: CancellationToken) : Task<Result<'T, WriteError<'T>>> =
        task {
            let checkedState = Writes.check model cs.State

            if not (Writes.isValid checkedState) then
                return Error(WriteError.Invalid(Changeset<'T>(checkedState)))
            else
                try
                    use! conn = provider.OpenConnection ct

                    let! tx =
                        if Writes.needsTransaction checkedState then
                            task {
                                let! t = provider.BeginTransaction(conn, IsolationLevel.ReadCommitted, ct)
                                return Some t
                            }
                        else
                            Task.FromResult None

                    use _ = { new IDisposable with member _.Dispose() = tx |> Option.iter (fun t -> t.Dispose()) }

                    let session: Execution.Session =
                        { Provider = provider
                          Options = options
                          Connection = conn
                          Transaction = tx
                          Ct = ct
                          Args = [||] }

                    let! r = Writes.write model session checkedState

                    match r, tx with
                    | Ok v, Some t ->
                        do! t.CommitAsync(ct)
                        return Ok(unbox<'T> v)
                    | Ok v, None -> return Ok(unbox<'T> v)
                    | Error e, t ->
                        match t with
                        | Some t -> do! t.RollbackAsync(CancellationToken.None)
                        | None -> ()

                        match e with
                        | Writes.Invalid s -> return Error(WriteError.Invalid(Changeset<'T>(s)))
                        | Writes.Stale s -> return Error(WriteError.Stale(Changeset<'T>(s)))
                        | Writes.Failed f -> return Error(WriteError.Failed f)
                        | Writes.Unsupported op -> return Error(WriteError.Translation(TranslationError.UnsupportedByProvider(op, provider.Dialect.Name)))
                with
                | :? OperationCanceledException as ex -> return Err.rethrow ex
                | Execution.MaterializationFailure m -> return Error(WriteError.Materialization m)
                | ex ->
                    match provider.Classify ex with
                    | Some f -> return Error(WriteError.Failed f)
                    | None -> return Err.rethrow ex
        }

    /// Validates and inserts. Invalid changesets are returned without any
    /// write; generated values come back in a new record.
    member this.Insert(cs: Changeset<'T>, ?cancellationToken) =
        if cs.Action <> ChangeAction.Insert then
            invalidArg "cs" $"repo.Insert was given a changeset for {(string cs.Action).ToLowerInvariant()}; build it with Changeset.insert, or call repo.{string cs.Action}"
        this.WriteOne(cs, defaultArg cancellationToken CancellationToken.None)

    /// Validates and updates only the changed fields, enforcing the version.
    member this.Update(cs: Changeset<'T>, ?cancellationToken) =
        if cs.Action <> ChangeAction.Update then
            invalidArg "cs" $"repo.Update was given a changeset for {(string cs.Action).ToLowerInvariant()}; build it with Changeset.update or Changeset.edit, or call repo.{string cs.Action}"
        this.WriteOne(cs, defaultArg cancellationToken CancellationToken.None)

    member this.Delete(cs: Changeset<'T>, ?cancellationToken) =
        if cs.Action <> ChangeAction.Delete then
            invalidArg "cs" $"repo.Delete was given a changeset for {(string cs.Action).ToLowerInvariant()}; build it with Changeset.delete, or call repo.{string cs.Action}"
        this.WriteOne(cs, defaultArg cancellationToken CancellationToken.None)

    /// Runs a transaction description atomically. An empty description
    /// succeeds without opening a connection.
    member _.Transact(tx: Tx<'T>, ?isolation: IsolationLevel, ?retry: RetryPolicy, ?cancellationToken: CancellationToken) =
        let ct = defaultArg cancellationToken CancellationToken.None
        let isolation = defaultArg isolation IsolationLevel.ReadCommitted
        let retry = defaultArg retry RetryPolicy.None

        task {
            match tx with
            | Pure v -> return Ok v
            | Effect _ ->
                let mutable attempt = 0
                let mutable outcome = None

                while outcome.IsNone do
                    attempt <- attempt + 1
                    let completed = ResizeArray<string>()
                    let mutable current = "begin"

                    let! result =
                        task {
                            try
                                use! conn = provider.OpenConnection ct
                                use! dbTx = provider.BeginTransaction(conn, isolation, ct)

                                let rt: TxRuntime =
                                    { Session =
                                        { Provider = provider
                                          Options = options
                                          Connection = conn
                                          Transaction = Some dbTx
                                          Ct = ct
                                          Args = [||] }
                                      Model = model
                                      Completed = completed }

                                current <- "steps"
                                let! r = Tx.run' rt tx

                                match r with
                                | Ok v ->
                                    current <- "commit"
                                    do! dbTx.CommitAsync(ct)
                                    return Ok v
                                | Error(step, failure) ->
                                    do! dbTx.RollbackAsync(CancellationToken.None)
                                    return Error(step, failure)
                            with
                            | :? OperationCanceledException as ex -> return Err.rethrow ex
                            | Execution.MaterializationFailure m -> return Error(current, StepFailure.Failed(DbError.Materialization m))
                            | Translate.TranslationFailure t -> return Error(current, StepFailure.Failed(DbError.Translation t))
                            | ex ->
                                // Steps attribute their own failures; what reaches here
                                // is from opening, beginning or committing.
                                match provider.Classify ex with
                                | Some f -> return Error(current, StepFailure.Failed(DbError.Database f))
                                | None -> return Err.rethrow ex
                        }

                    match result with
                    | Ok v -> outcome <- Some(Ok v)
                    | Error(step, failure) ->
                        let retryable =
                            match failure with
                            | StepFailure.Failed(DbError.Database f) -> retry.ShouldRetry f
                            | _ -> false

                        if not (retryable && attempt < retry.MaxAttempts) then
                            outcome <-
                                Some(
                                    Error
                                        { Step = step
                                          Failure = failure
                                          RolledBack = List.ofSeq completed
                                          Attempts = attempt }
                                )

                return outcome.Value
        }

[<AutoOpen>]
module ChangesetModelExtensions =
    type Changeset with
        /// Model-aware validation without database access: primary-key and
        /// version changes, relationship write targets and duplicate child
        /// identities. Repo writes run the same checks before any command.
        static member check (model: Model) (cs: Changeset<'T>) : Changeset<'T> =
            Changeset<'T>(Writes.check model cs.State)
