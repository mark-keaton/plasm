namespace Plasm

open System
open System.Data
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// Text for query displays (F# Interactive): the SQL as it is sent, a clause
/// a line, with parameter values.
module internal QueryDisplay =
    open System.Text

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
        | v -> Reflection.Shown.value v

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

        String.concat "\n" (indent "  " cmd.Sql :: parameters)

    let rec loads (steps: LoadStep list) =
        steps
        |> List.collect (fun l ->
            let header = $"  then {l.Path} ({l.Relationship}, {l.Target}), one command per batch of keys:"
            [ header; indent "  " l.Sql ] @ loads l.Children)

/// A repository: where Plasm meets the database. Made from a model (the
/// record types it can store) and a provider (the database connection), and
/// checked as it starts: `Repo.Start(model, Postgres.connect connectionString,
/// queriesIn = typeof<Order>.Assembly)`. It runs queries (All, One,
/// Count) and transactions (Transact), each returning a Result. It is
/// the explicit boundary where descriptions are executed: every member performs
/// database work; nothing else in the library does.
type Repo internal (model: Model, provider: IProvider, ?options: RepoOptions) =
    let options = defaultArg options RepoOptions.Default

    // A provider without JSON documents cannot store a [<Json>] field: refused
    // now, at start-up, rather than at the first query (json-columns.feature).
    do
        if provider.Dialect.Json.IsNone then
            let documents =
                [ for s in model.Schemas.Values do
                      for f in s.Fields do
                          if f.Json.IsSome then
                              yield $"{s.Name}.{f.Name}" ]

            if not documents.IsEmpty then
                Err.config
                    $"""{provider.Dialect.Name} has no JSON documents, so it cannot store {String.concat ", " documents} ([<Json>]); use a provider that has them, or store these fields another way"""

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

    // A query that binds loads (loads.feature), planned once per value like any query.
    let includingPlans = ConditionalWeakTable<QueryDef, Planner.Including>()

    let includingFor (def: QueryDef) =
        let def = defaultArg def.Template def

        match includingPlans.TryGetValue def with
        | true, p -> Ok p
        | _ ->
            Threading.Interlocked.Increment(&planned) |> ignore

            match Planner.including provider.Dialect model def with
            | Ok p when p.Outer.Reusable ->
                includingPlans.AddOrUpdate(def, p)
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
      if not projection.Def.Includings.IsEmpty then
        Planner.including provider.Dialect model projection.Def |> Result.map (Planner.describeIncluding provider.Dialect)
      else
        Translate.projection model (ref false) projection.Def (Some projection.Select)
        |> Result.map (fun (select, _) ->
            { Sql = (Sql.renderSelect provider.Dialect select).Sql
              Loads = [] })

    /// Reads a query's records, each with what it owns or links, in batched
    /// commands.
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
                    let! rows = withArgs query.Def.Args ct false (fun s -> Execution.readProjection s model select shape query.Def.Root.Name)
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
              if not projection.Def.Includings.IsEmpty then
                match includingFor projection.Def with
                | Error e -> return Error(DbError.Translation e)
                | Ok plan ->
                    let! rows = withArgs projection.Def.Args ct false (fun s -> Execution.readIncluding s model plan)
                    return Ok(rows |> List.map unbox<'R>)
              else
                match Translate.projection model (ref false) projection.Def (Some projection.Select) with
                | Error e -> return Error(DbError.Translation e)
                | Ok(select, shape) ->
                    let! rows =
                        withArgs projection.Def.Args ct false (fun s -> Execution.readProjection s model select shape projection.Def.Root.Name)

                    return Ok(rows |> List.map unbox<'R>)
            })

    /// Counts the rows a query matches (what they own is not read) with one
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
                let! rows = withSession ct false (fun s -> Execution.rawRecords s model typeof<'R> cmd)
                return Ok(rows |> Seq.map unbox<'R> |> List.ofSeq)
            })

    /// Every query value in `assembly`'s modules (`inNamespace` limits it, for
    /// Plasm's own tests), translated without running any; with how many were
    /// found and the functions returning a query, which are not values.
    member internal _.Scan(assembly: Reflection.Assembly, ?inNamespace: string) =
        let within (t: Type) =
            match inNamespace with
            | None -> true
            | Some n -> t.FullName = n || t.FullName.StartsWith(n + ".") || t.FullName.StartsWith(n + "+")

        let isModule (t: Type) =
            t.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
            |> Seq.exists (fun a -> (a :?> CompilationMappingAttribute).SourceConstructFlags = SourceConstructFlags.Module)

        let isQueryType (t: Type) =
            t.IsGenericType
            && (let d = t.GetGenericTypeDefinition()
                d = typedefof<Query<_>> || d = typedefof<Projection<_>> || d = typedefof<One<_>> || d = typedefof<OneProjection<_>>)

        let rec isQuery (t: Type) =
            if isQueryType t then true
            else
                match Reflection.functionResult t with
                | Some r -> isQuery r
                | None -> false

        let translates (q: ICheckable) =
            match q.Select with
            | Some _ when not q.Def.Includings.IsEmpty -> Planner.including provider.Dialect model q.Def |> Result.map ignore
            | Some select -> Translate.projection model (ref false) q.Def (Some select) |> Result.map ignore
            | None when not q.Def.Joins.IsEmpty -> Translate.projection model (ref false) q.Def None |> Result.map ignore
            | None -> planFor q.Def |> Result.map ignore

        let flags = Reflection.BindingFlags.Static ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.DeclaredOnly
        let mutable found = 0
        let problems = ResizeArray<QueryProblem>()
        let functions = ResizeArray<string>()

        for t in assembly.GetTypes() do
            if isModule t && within t then
                let name (m: string) = $"{t.FullName.Replace('+', '.')}.{m}"

                for p in t.GetProperties flags do
                    if p.GetIndexParameters().Length = 0 && isQuery p.PropertyType then
                        // Reading the value runs its module's initialisation; where that
                        // fails (in a native binary, a quotation naming a member it does
                        // not keep, docs/aot.md), the query is a problem, not a crash.
                        let value =
                            try
                                Ok(p.GetValue null)
                            with ex ->
                                let rec innermost (e: exn) = if isNull e.InnerException then e else innermost e.InnerException
                                Error(innermost ex)

                        match value with
                        | Error e ->
                            found <- found + 1

                            let native =
                                if Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported then ""
                                else "; in a native binary this is a quotation naming a member the binary does not keep (docs/aot.md)"

                            problems.Add
                                { Query = name p.Name
                                  Error = TranslationError.UnsupportedExpression(name p.Name, $"it could not be read: {e.GetType().Name}: {e.Message}{native}") }
                        | Ok(:? ICheckable as q) ->
                            found <- found + 1

                            match translates q with
                            | Error e -> problems.Add { Query = name p.Name; Error = e }
                            | Ok() -> ()
                        // A function that builds a query each call is not a value Plasm can check.
                        | Ok _ -> functions.Add(name p.Name)

                for m in t.GetMethods flags do
                    if not m.IsSpecialName && m.GetParameters().Length > 0 && isQuery m.ReturnType then
                        functions.Add(name m.Name)

        {| Found = found
           Problems = problems |> Seq.sortBy (fun p -> p.Query) |> List.ofSeq
           Functions = functions |> Seq.distinct |> Seq.sort |> List.ofSeq |}

    /// The query values of `assembly` that Plasm cannot translate (Plasm's own
    /// tests; an application starts its repository with Repo.Start).
    member internal this.CheckQueries(assembly: Reflection.Assembly, ?inNamespace: string) : QueryProblem list =
        (match inNamespace with
         | Some n -> this.Scan(assembly, n)
         | None -> this.Scan assembly)
            .Problems

    /// The one way to get a repository (query-check.feature): made from a
    /// model and a provider, then checked. Every query value `queriesIn`
    /// defines is translated, without running any, and the model is compared
    /// with the database; RepoOptions.Queries and RepoOptions.Schema say what
    /// to do with what is found: Crash (the default) raises one
    /// PlasmStartupException naming every problem, Warn reports each through
    /// OnProblem and starts, Ignore does not check.
    static member Start(model: Model, provider: IProvider, queriesIn: Reflection.Assembly, ?options: RepoOptions, ?cancellationToken: CancellationToken) : Task<Repo> =
        let options = defaultArg options RepoOptions.Default
        let ct = defaultArg cancellationToken CancellationToken.None

        task {
            let repo = Repo(model, provider, options)
            let assembly = queriesIn.GetName().Name

            let scan =
                if options.Queries = StartupCheck.Ignore then None else Some(repo.Scan queriesIn)

            let queries =
                match scan with
                | None -> []
                | Some s ->
                    [ if s.Found = 0 then yield StartupProblem.NoQueries assembly
                      for p in s.Problems -> StartupProblem.Query p ]

            let! schema =
                if options.Schema = StartupCheck.Ignore then
                    Task.FromResult []
                else
                    task {
                        match! repo.CheckSchema ct with
                        | Ok issues ->
                            return
                                issues
                                |> List.sortBy (fun i -> i.Table, defaultArg i.Column "", i.Model, defaultArg i.Field "", string i.Kind)
                                |> List.map StartupProblem.Schema
                        | Error e -> return [ StartupProblem.Unchecked e ]
                    }

            let functions =
                match scan with
                | Some s when not s.Functions.IsEmpty -> [ StartupProblem.UncheckedFunctions s.Functions ]
                | _ -> []

            let report =
                match options.OnProblem with
                | Some f -> f
                | None -> fun p -> Console.Error.WriteLine("Plasm: " + string p)

            if options.Queries = StartupCheck.Warn then
                for p in queries @ functions do
                    report p

            if options.Schema = StartupCheck.Warn then
                for p in schema do
                    report p

            let crash =
                [ if options.Queries = StartupCheck.Crash then yield! queries
                  if options.Schema = StartupCheck.Crash then yield! schema ]

            if not crash.IsEmpty then
                let plural n one many = if n = 1 then $"{n} {one}" else $"{n} {many}"

                let summary =
                    [ match scan with
                      | Some s when options.Queries = StartupCheck.Crash && not s.Problems.IsEmpty ->
                          $"{s.Problems.Length} of {s.Found} queries in {assembly} cannot be translated"
                      | _ -> ()
                      let differences = schema |> List.filter (function StartupProblem.Schema _ -> true | _ -> false)

                      if options.Schema = StartupCheck.Crash && not differences.IsEmpty then
                          plural differences.Length "schema difference" "schema differences"

                      match functions with
                      | [ StartupProblem.UncheckedFunctions names ] ->
                          plural names.Length "function returning a query, which is not checked" "functions returning a query, which are not checked"
                      | _ -> () ]

                let first =
                    match summary with
                    | [] -> "Plasm did not start"
                    | parts -> "Plasm did not start: " + String.concat "; " parts

                let lines = first :: [ for p in crash -> $"  - {p}" ]
                raise (PlasmStartupException(crash, String.concat Environment.NewLine lines))

            return repo
        }

    /// The DDL this repository's model expects (ddl.feature): every table,
    /// its columns, key and constraints, with the names Plasm maps violations
    /// by. It is printed, never run: write the migration that reaches it.
    member _.Ddl() : string = Ddl.print model provider.Dialect

    /// Compares the model with the live database catalogue and reports every
    /// difference that would make a mapped read or write fail: missing tables
    /// and columns (join tables included), incompatible column types,
    /// non-optional fields over nullable columns, and NOT NULL columns the
    /// model cannot write. An empty list means none was found. Reads only the
    /// catalogue; it does not create or migrate anything.
    /// Translates every declared field with each operator it takes, and its
    /// ordering, once (build-query.feature): what a program building queries
    /// from them can combine. Raises PlasmStartupException naming each one
    /// Plasm cannot translate, as Start does for a query.
    /// Checks that Plasm translates every field a program lets its callers
    /// read; raises PlasmStartupException naming each one it cannot.
    member this.CheckFields(fields: ReadField<'T> seq) : unit =
        let all = Query<'T>(QueryDef.Empty typeof<'T>)

        let problems =
            [ for f in fields do
                  let problem error =
                      Some(StartupProblem.Query { Query = $"{typeof<'T>.Name}.{f.Name} read"; Error = error })

                  let found =
                      try
                          match this.Explain(BuildQuery.select [ f ] all) with
                          | Ok _ -> None
                          | Error e -> problem e
                      with :? ArgumentException as e ->
                          problem (TranslationError.InvalidQuery e.Message)

                  match found with
                  | Some p -> yield p
                  | None -> () ]

        if not problems.IsEmpty then
            let what = if problems.Length = 1 then "declared field Plasm cannot translate" else "declared fields Plasm cannot translate"
            let lines = $"Plasm did not start: {problems.Length} {what}" :: [ for p in problems -> $"  - {p}" ]
            raise (PlasmStartupException(problems, String.concat Environment.NewLine lines))

    member this.CheckFields(fields: QueryField<'T> seq) : unit =
        let record = typeof<'T>.Name
        let all = Query<'T>(QueryDef.Empty typeof<'T>)

        let translated (what: string) (build: unit -> Query<'T>) =
            let problem error =
                Some(StartupProblem.Query { Query = $"{record}.{what}"; Error = error })

            try
                match this.Explain(build ()) with
                | Ok _ -> None
                | Error e -> problem e
            with :? ArgumentException as e ->
                problem (TranslationError.InvalidQuery e.Message)

        let problems =
            [ for f in fields do
                  for op in f.Operators do
                      match translated $"{f.Name} {op}" (fun () -> QueryOps.where all (f.Condition(op, f.Sample).ToExpr()).Raw) with
                      | Some p -> yield p
                      | None -> ()

                  if f.Orderable then
                      match translated $"{f.Name} ordering" (fun () -> f.OrderBy(all, false)) with
                      | Some p -> yield p
                      | None -> () ]

        if not problems.IsEmpty then
            let what = if problems.Length = 1 then "declared field Plasm cannot translate" else "declared fields Plasm cannot translate"
            let lines = $"Plasm did not start: {problems.Length} {what}" :: [ for p in problems -> $"  - {p}" ]
            raise (PlasmStartupException(problems, String.concat Environment.NewLine lines))

    member _.CheckSchema(?cancellationToken: CancellationToken) : Task<Result<SchemaIssue list, DbError>> =
        let ct = defaultArg cancellationToken CancellationToken.None

        Execution.guard provider (fun () ->
            task {
                let! issues = withSession ct false (fun s -> SchemaCheck.run model s)
                return Ok issues
            })

    /// F# source for a [<Table>] record that maps `table`, generated from the
    /// database catalogue: column types become F# types, nullable columns
    /// become options, the primary key becomes the field named Id, and columns
    /// with no F# type in this dialect are left out with a comment. Reads only the catalogue.
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
    /// The one record a tie can find for a record in hand
    /// (`repo.One(referredBy<Customer> order)`), if there is one.
    member this.One(one: One<'T>, ?cancellationToken: CancellationToken) =
        this.One(Query<'T>(one.Def), ?cancellationToken = cancellationToken)

    member this.One(query: Query<'T>, ?cancellationToken: CancellationToken) =
        task {
            // Remembered per query value, so repeated lookups reuse one plan.
            let limited = Query<'T>(oneDefs.GetValue(query.Def, fun d -> OneRow.limit model d))

            match! this.All(limited, cancellationToken = defaultArg cancellationToken CancellationToken.None) with
            | Ok [] -> return Ok None
            | Ok [ x ] -> return Ok(Some x)
            | Ok _ -> return Error DbError.MoreThanOneRow
            | Error e -> return Error e
        }

    /// Runs a transaction description atomically. An empty description
    /// succeeds without opening a connection.
    member _.Transact(tx: Tx<'T, 'E>, ?isolation: IsolationLevel, ?retry: RetryPolicy, ?cancellationToken: CancellationToken) =
        let ct = defaultArg cancellationToken CancellationToken.None
        let isolation = defaultArg isolation IsolationLevel.ReadCommitted
        let retry = defaultArg retry RetryPolicy.None

        task {
            match tx with
            | Pure v -> return Ok v
            | Effect _ ->
                let mutable attempt = 0
                let mutable outcome = None
                let mutable waited = TimeSpan.Zero

                while outcome.IsNone do
                    // After a failed attempt the policy retries, it waits first,
                    // so that what it lost to can commit (elegance.feature).
                    if attempt > 0 then
                        let wait = retry.Wait attempt

                        if wait > TimeSpan.Zero then
                            do! Task.Delay(wait, ct)
                            waited <- waited + wait

                    attempt <- attempt + 1
                    let completed = ResizeArray<TxStep>()
                    let mutable current = { File = ""; Line = 0; Action = "begin" }

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

                                current <- { current with Action = "steps" }
                                let! r = TxCore.run' rt tx

                                match r with
                                | Ok v ->
                                    current <- { current with Action = "commit" }
                                    do! dbTx.CommitAsync(ct)
                                    return Ok v
                                | Error(step, failure) ->
                                    do! dbTx.RollbackAsync(CancellationToken.None)
                                    return Error(step, failure)
                            with
                            | :? OperationCanceledException as ex -> return Err.rethrow ex
                            | Execution.MaterializationFailure m -> return Error(current, TxFailure.Failed(DbError.Materialization m))
                            | Translate.TranslationFailure t -> return Error(current, TxFailure.Failed(DbError.Translation t))
                            | ex ->
                                // Steps attribute their own failures; what reaches here
                                // is from opening, beginning or committing.
                                match provider.Classify ex with
                                | Some f -> return Error(current, TxFailure.Failed(DbError.Database f))
                                | None -> return Err.rethrow ex
                        }

                    match result with
                    | Ok v -> outcome <- Some(Ok v)
                    | Error(step, failure) ->
                        let retryable =
                            match failure with
                            | TxFailure.Failed(DbError.Database f) -> retry.ShouldRetry f
                            | _ -> false

                        if not (retryable && attempt < retry.MaxAttempts) then
                            outcome <-
                                Some(
                                    Error
                                        { Step = step
                                          Failure = failure
                                          RolledBack = List.ofSeq completed
                                          Attempts = attempt
                                          Waited = waited }
                                )

                return outcome.Value
        }

