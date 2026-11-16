/// Command execution, materialisation and batched relationship loading.
module internal Plasm.Execution

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Data.Common
open System.Diagnostics
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

exception MaterializationFailure of MaterializationError

type Session =
    { Provider: IProvider
      Options: RepoOptions
      Connection: DbConnection
      Transaction: DbTransaction option
      Ct: CancellationToken
      /// Arguments of a prepared query, bound into its parameters.
      Args: obj[] }

type private ColumnReader<'T>() =
    static member Read(r: DbDataReader, i: int) : obj =
        if r.IsDBNull i then null else box (r.GetFieldValue<'T> i)

let private readers = ConcurrentDictionary<Type, Func<DbDataReader, int, obj>>()

/// Typed column reader for a provider type (avoids boxing surprises such as
/// int32 vs int64).
let columnReader (t: Type) =
    readers.GetOrAdd(
        t,
        fun t ->
          if t = typeof<obj> then
            // Computed columns: whatever the database returns.
            Func<DbDataReader, int, obj>(fun r i -> if r.IsDBNull i then null else r.GetValue i)
          else
            typedefof<ColumnReader<_>>
                .MakeGenericType(t)
                .GetMethod("Read", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                .CreateDelegate<Func<DbDataReader, int, obj>>()
    )

let private schemaReaders = ConditionalWeakTable<SchemaInfo, Func<DbDataReader, int, obj>[]>()

/// Column readers for a schema's fields, computed once per schema.
let readersFor (schema: SchemaInfo) =
    schemaReaders.GetValue(schema, fun s -> s.Fields |> Array.collect (fun f -> f.Parts |> List.map (fun p -> columnReader p.ProviderType) |> Array.ofList))

let private describe (v: obj) =
    if isNull v then "NULL" else $"{v} ({v.GetType().Name})"

/// Resolves parameters bound from prepared-query arguments.
let private bind (s: Session) (cmd: Sql.Command) : Sql.Command =
    if cmd.Parameters |> List.exists (fun p -> p :? Sql.Bound) then
        { cmd with
            Parameters =
                cmd.Parameters
                |> List.map (fun p ->
                    match p with
                    | :? Sql.Bound as b -> b.Resolve s.Args
                    | p -> p) }
    else
        cmd

let private prepare (s: Session) (cmd: Sql.Command) =
    let c = s.Connection.CreateCommand()
    c.CommandText <- cmd.Sql

    match s.Transaction with
    | Some t -> c.Transaction <- Sandboxes.unwrap t
    | None -> ()

    cmd.Parameters
    |> List.iteri (fun i v ->
        let p = c.CreateParameter()
        p.ParameterName <- s.Provider.Dialect.ParameterName i
        p.Value <- v
        c.Parameters.Add p |> ignore)

    c

let private report (s: Session) (cmd: Sql.Command) (elapsed: TimeSpan) rows ok =
    match s.Options.OnCommand with
    | None -> ()
    | Some f ->
        let ps =
            cmd.Parameters
            |> List.mapi (fun i v ->
                let name = s.Provider.Dialect.ParameterName i

                let value =
                    if s.Options.IncludeParameterValues then
                        match v with
                        | :? Array as a -> Some("[" + (a |> Seq.cast<obj> |> Seq.map string |> String.concat ", ") + "]")
                        | v when isNull v || v = box DBNull.Value -> Some "NULL"
                        | v -> Some(string v)
                    else
                        None

                name, value)

        f
            { Sql = cmd.Sql
              Parameters = ps
              Duration = elapsed
              Rows = rows
              Succeeded = ok }

/// Runs a query command, reading each row with `read`.
let query (s: Session) (cmd: Sql.Command) (read: DbDataReader -> 'a) : Task<ResizeArray<'a>> =
    let cmd = bind s cmd

    task {
        use c = prepare s cmd
        let sw = Stopwatch.StartNew()
        let results = ResizeArray<'a>()

        try
            use! r = c.ExecuteReaderAsync(s.Ct)

            let mutable go = true

            while go do
                let! more = r.ReadAsync(s.Ct)

                if more then results.Add(read r) else go <- false

            report s cmd sw.Elapsed results.Count true
            return results
        with ex ->
            report s cmd sw.Elapsed results.Count false
            return Err.rethrow ex
    }

/// Runs a non-query command, returning affected rows.
let execute (s: Session) (cmd: Sql.Command) : Task<int> =
    let cmd = bind s cmd

    task {
        use c = prepare s cmd
        let sw = Stopwatch.StartNew()

        try
            let! n = c.ExecuteNonQueryAsync(s.Ct)
            report s cmd sw.Elapsed n true
            return n
        with ex ->
            report s cmd sw.Elapsed 0 false
            return Err.rethrow ex
    }

/// Reads an entity row (columns in schema field order) into a field array
/// with every owned or linked field empty until its load fills it.
let readEntity (schema: SchemaInfo) : DbDataReader -> obj[] =
    let readers = readersFor schema

    fun r ->
        let values = Array.zeroCreate<obj> schema.FieldCount
        let mutable j = 0

        for f in schema.Fields do
          match f.Composite with
          | false ->
            // One column: read it and convert it, with nothing allocated per row
            // beyond the value itself.
            let at = j
            j <- j + 1

            let raw =
                try
                    readers.[at].Invoke(r, at)
                with :? InvalidCastException as ex ->
                    raise (
                        MaterializationFailure
                            { Model = schema.Name
                              Property = f.Name
                              Value = string (r.GetFieldType at)
                              Message = $"column {f.Column} does not match {f.ProviderType.Name}: {ex.Message}" }
                    )

            match f.FromDb raw with
            | Ok v -> values.[f.Index] <- v
            | Error msg ->
                raise (
                    MaterializationFailure
                        { Model = schema.Name
                          Property = f.Name
                          Value = describe raw
                          Message = msg }
                )
          | true ->
            // A union with fields, or a value, reads its several columns.
            let raws =
                f.Parts
                |> List.map (fun p ->
                    let at = j
                    j <- j + 1

                    try
                        readers.[at].Invoke(r, at)
                    with :? InvalidCastException as ex ->
                        raise (
                            MaterializationFailure
                                { Model = schema.Name
                                  Property = f.Name
                                  Value = string (r.GetFieldType at)
                                  Message = $"column {p.Name} does not match {p.ProviderType.Name}: {ex.Message}" }
                        ))

            let raw = match raws with [ one ] -> one | many -> box (String.Join(", ", many |> List.map describe))

            match f.FromColumns raws with
            | Ok v -> values.[f.Index] <- v
            | Error msg ->
                raise (
                    MaterializationFailure
                        { Model = schema.Name
                          Property = f.Name
                          Value = describe raw
                          Message = msg }
                )

        for a in schema.Assocs do
            values.[a.Index] <- a.Ctors.Empty

        values

let private keyOf (f: FieldMap) (v: obj) : obj option = f.ReadOption v

let private providerArray (f: FieldMap) (keys: obj seq) =
    let ks = Seq.toArray keys
    let arr = Array.CreateInstance(f.ProviderType, ks.Length)
    ks |> Array.iteri (fun i k -> arr.SetValue(f.ScalarToDb k, i))
    arr

/// Loads `nodes` into the field arrays `rows` (mutating the arrays, which are
/// not yet visible to callers). Children are fully assembled — including their
/// own nested loads — before being attached.
let rec loadInto (s: Session) (rows: IReadOnlyList<obj[]>) (nodes: Planner.LoadNode list) : Task<unit> =
    task {
        for node in nodes do
            let s = if node.Args.Length > 0 then { s with Args = node.Args } else s
            let keys = HashSet<obj>()

            for r in rows do
                match keyOf node.KeyField r.[node.KeyField.Index] with
                | Some k -> keys.Add k |> ignore
                | None -> ()

            // For many-to-many, (owner key, child row): one target row may
            // belong to several owners and is materialised once. A plain list
            // groups its rows by their own column instead, keeping no second
            // buffer of them: at thousands of rows each such buffer is a
            // large-object allocation, and those bring on gen2 collections
            // (docs/benchmarks.md, "The page of 1,000").
            let entries = ResizeArray<obj * obj[]>()
            let mutable childRows = ResizeArray<obj[]>()

            let batchSize =
                match node.Strategy with
                | LoadStrategy.BatchedByKeys n -> max 1 n

            match node.Through with
            | None ->
                for batch in Seq.chunkBySize batchSize keys do
                    let select = node.Build(providerArray node.KeyField batch)
                    let cmd = Sql.renderSelect s.Provider.Dialect select
                    let! got = query s cmd (readEntity node.Target)

                    if childRows.Count = 0 then
                        childRows <- got
                    else
                        childRows.AddRange got
            | Some _ ->
                let read = readEntity node.Target
                let ownerAt = node.Target.Fields |> Array.sumBy (fun f -> f.Parts.Length)
                let ownerReader = columnReader node.KeyField.ProviderType
                let canonical = Dictionary<obj, obj[]>()

                for batch in Seq.chunkBySize batchSize keys do
                    let select = node.Build(providerArray node.KeyField batch)
                    let cmd = Sql.renderSelect s.Provider.Dialect select

                    let! got =
                        query s cmd (fun r ->
                            let row = read r

                            let owner =
                                match node.KeyField.FromDb(ownerReader.Invoke(r, ownerAt)) with
                                | Ok v -> v
                                | Error m ->
                                    raise (
                                        MaterializationFailure
                                            { Model = node.Parent.Name
                                              Property = node.Assoc.Name
                                              Value = "owner key"
                                              Message = m }
                                    )

                            owner, row)

                    for owner, row in got do
                        let targetKey = row.[node.Target.Key.Index]

                        let row =
                            match canonical.TryGetValue targetKey with
                            | true, existing -> existing
                            | _ ->
                                canonical.[targetKey] <- row
                                childRows.Add row
                                row

                        entries.Add(owner, row)

            do! loadInto s childRows node.Children

            let groups = Dictionary<obj, ResizeArray<obj>>()

            let add (k: obj) (v: obj) =
                match groups.TryGetValue k with
                | true, g -> g.Add v
                | _ -> groups.[k] <- ResizeArray [ v ]

            match node.Through with
            | None ->
                for cr in childRows do
                    match keyOf node.MatchField cr.[node.MatchField.Index] with
                    | Some k -> add k (node.Target.Shape.Construct cr)
                    | None -> ()
            | Some _ ->
                let built = Dictionary<obj[], obj>(HashIdentity.Reference)

                for k, cr in entries do
                    match built.TryGetValue cr with
                    | true, v -> add k v
                    | _ ->
                        let v = node.Target.Shape.Construct cr
                        built.[cr] <- v
                        add k v

            let a = node.Assoc

            for r in rows do
                let key = keyOf node.KeyField r.[node.KeyField.Index]

                let found =
                    match key with
                    | Some k ->
                        match groups.TryGetValue k with
                        | true, g -> g :> IReadOnlyList<obj>
                        | _ -> [||]
                    | None -> [||]

                let payload =
                    match a.Cardinality with
                    | Many -> a.MakeList found
                    | OptionalOne ->
                        if found.Count > 1 then
                            raise (
                                MaterializationFailure
                                    { Model = node.Parent.Name
                                      Property = a.Name
                                      Value = $"{found.Count} rows"
                                      Message = $"a has-one relationship matched {found.Count} records for the {node.Parent.Name} with key {key.Value}" }
                            )

                        if found.Count = 0 then
                            null
                        else
                            a.WrapSome found.[0]
                    | RequiredOne ->
                        if found.Count <> 1 then
                            raise (
                                MaterializationFailure
                                    { Model = node.Parent.Name
                                      Property = a.Name
                                      Value = string key
                                      Message =
                                        $"a required relationship found {found.Count} matching {node.Target.Name} records (filtered load or missing row)" }
                            )

                        found.[0]

                r.[a.Index] <- a.Ctors.Loaded payload
    }

let classifyOrRaise (s: Session) (ex: exn) =
    match s.Provider.Classify ex with
    | Some f -> f
    | None -> raise ex

/// Runs `body`, converting provider exceptions into `DbError`. Cancellation
/// propagates as OperationCanceledException.
let guard (provider: IProvider) (body: unit -> Task<Result<'a, DbError>>) : Task<Result<'a, DbError>> =
    task {
        try
            return! body ()
        with
        | :? OperationCanceledException as ex -> return Err.rethrow ex
        | MaterializationFailure m -> return Error(DbError.Materialization m)
        | Translate.TranslationFailure t -> return Error(DbError.Translation t)
        | ex ->
            match provider.Classify ex with
            | Some f -> return Error(DbError.Database f)
            | None -> return Err.rethrow ex
    }

let readRoot (s: Session) (plan: Planner.Plan) : Task<obj list> =
    task {
        let cmd = Sql.renderSelect s.Provider.Dialect plan.Root
        let! rows = query s cmd (readEntity plan.RootSchema)
        do! loadInto s rows plan.Loads
        return rows |> Seq.map plan.RootSchema.Shape.Construct |> List.ofSeq
    }

/// The lists a record owns or links, for records read as field values (a
/// join's or hand-written SQL's rows): loaded as a query of the records loads
/// them, one batched command per list (owned-collections.feature).
let private loadOwned (s: Session) (model: Model) (schema: SchemaInfo) (rows: obj[] list) : Task<unit> =
    if rows.IsEmpty || not (schema.Assocs |> Array.exists (fun a -> a.Owned || a.Linked)) then
        Task.FromResult()
    else
        match Planner.planLoads s.Provider.Dialect model schema with
        | Ok nodes -> loadInto s (ResizeArray rows) nodes
        | Error e -> invalidOp $"the lists {schema.Name} owns could not be planned: {e}"

/// A whole record's field values from a projection row; null for a
/// left-joined source without a match.
let private entityValues (start: int) (schema: SchemaInfo) (optional: bool) (row: obj[]) : obj[] =
    // Each field's columns start where the fields before it end.
    let offsets = schema.Fields |> Array.scan (fun at f -> at + f.Parts.Length) start
    let keyAt = offsets.[schema.Fields |> Array.findIndex (fun f -> f.Name = schema.Key.Name)]

    if optional && isNull row.[keyAt] then
        null
    else
        let values = Array.zeroCreate<obj> schema.FieldCount

        schema.Fields
        |> Array.iteri (fun j f ->
            let raws = [ for i in offsets.[j] .. offsets.[j + 1] - 1 -> row.[i] ]

            match f.FromColumns raws with
            | Ok v -> values.[f.Index] <- v
            | Error m ->
                raise (
                    MaterializationFailure
                        { Model = schema.Name
                          Property = f.Name
                          Value = String.Join(", ", raws |> List.map describe)
                          Message = m }
                ))

        for a in schema.Assocs do
            values.[a.Index] <- a.Ctors.Empty

        values

/// Builds a projected value; `entity` gives the field values of the n-th
/// whole record in the shape (in the order `collect` found them).
let rec private build (shape: Translate.Shape) (row: obj[]) (entity: unit -> obj[]) : obj =
    match shape with
    | Translate.Const v -> v
    | Translate.Leaf(i, _, _, _) -> row.[i]
    | Translate.Build(ctor, parts) -> ctor (parts |> List.map (fun p -> build p row entity) |> Array.ofList)
    | Translate.EntityLeaf(_, schema, optional) ->
        match entity () with
        | null -> null // None: the left-joined source had no match
        | values ->
            let record = schema.Shape.Construct values

            if optional then
                Reflection.makeSome (typedefof<option<_>>.MakeGenericType schema.Type) record
            else
                record

let readProjection (s: Session) (model: Model) (select: Sql.Select) (shape: Translate.Shape) (label: string) : Task<obj list> =
    let leaves = ResizeArray<int * Type * (obj -> Result<obj, string>) * string>()
    let entities = ResizeArray<int * SchemaInfo * bool>()

    let rec collect sh =
        match sh with
        | Translate.Leaf(i, t, c, l) -> leaves.Add(i, t, c, l)
        | Translate.EntityLeaf(start, schema, optional) ->
            entities.Add(start, schema, optional)
            // Raw provider values; the entity is built from them (build, below).
            let mutable j = start

            for f in schema.Fields do
                for p in f.Parts do
                    leaves.Add(j, p.ProviderType, (fun (v: obj) -> Ok v), $"{schema.Name}.{f.Name}")
                    j <- j + 1
        | Translate.Build(_, ps) -> ps |> List.iter collect
        | Translate.Const _ -> ()

    collect shape

    task {
        let cmd = Sql.renderSelect s.Provider.Dialect select

        let! rows =
            query s cmd (fun r ->
                let values = Array.zeroCreate<obj> select.Columns.Length

                for i, t, convert, l in leaves do
                    let raw = (columnReader t).Invoke(r, i)

                    match convert raw with
                    | Ok v -> values.[i] <- v
                    | Error msg ->
                        raise (
                            MaterializationFailure
                                { Model = label
                                  Property = l
                                  Value = describe raw
                                  Message = msg }
                        )

                values)

        // Each whole record's values, by record position then row; then what
        // the records own, loaded for all rows at once.
        let records =
            [| for start, schema, optional in entities -> [| for row in rows -> entityValues start schema optional row |] |]

        for n in 0 .. entities.Count - 1 do
            let _, schema, _ = entities.[n]
            do! loadOwned s model schema (records.[n] |> Array.filter (fun v -> not (isNull v)) |> List.ofArray)

        return
            rows
            |> Seq.mapi (fun i row ->
                let mutable next = 0

                build shape row (fun () ->
                    let v = records.[next].[i]
                    next <- next + 1
                    v))
            |> List.ofSeq
    }

/// One `including` level (including.feature): reads what it finds for the
/// `parents` rows, in batches of their values, and its own levels over those;
/// returns each parent row's list.
let rec private readLevel (s: Session) (model: Model) (batchSize: int) (parents: IReadOnlyList<obj[]>) (l: Planner.IncludingLevel) : Task<obj[] -> obj> =
    task {
        let parts = l.Parts
        let valueOf (r: obj[]) = keyOf parts.OuterField r.[parts.OuterField.Index]
        let keys = HashSet<obj>()

        for r in parents do
            valueOf r |> Option.iter (keys.Add >> ignore)

        let groups = Dictionary<obj, ResizeArray<obj>>()

        let add (key: obj option) (child: obj) =
            match key with
            | Some k ->
                match groups.TryGetValue k with
                | true, g -> g.Add child
                | _ -> groups.[k] <- ResizeArray [ child ]
            | None -> ()

        match parts.Projected with
        | Some shape ->
            // Each row is (the selected value, the matching column's value).
            for batch in Seq.chunkBySize batchSize keys do
                let select = parts.Build(providerArray parts.MatchField batch)
                let! got = readProjection s model select shape parts.Target.Name

                for row in got do
                    let value, key = unbox<obj * obj> row
                    add (keyOf parts.MatchField key) value
        | None ->
            let mutable childRows = ResizeArray<obj[]>()

            for batch in Seq.chunkBySize batchSize keys do
                let cmd = Sql.renderSelect s.Provider.Dialect (parts.Build(providerArray parts.MatchField batch))
                let! got = query s cmd (readEntity parts.Target)

                if childRows.Count = 0 then childRows <- got else childRows.AddRange got

            do! loadInto s childRows l.Children

            let lists = ResizeArray<obj[] -> obj>()

            for n in l.Nested do
                let! f = readLevel s model batchSize childRows n
                lists.Add f

            let element =
                if lists.Count = 0 then
                    fun (cr: obj[]) -> parts.Target.Shape.Construct cr
                else
                    fun cr -> FSharp.Reflection.FSharpValue.MakeTuple([| yield parts.Target.Shape.Construct cr; for f in lists -> f cr |], parts.ElementType)

            for cr in childRows do
                add (keyOf parts.MatchField cr.[parts.MatchField.Index]) (element cr)

        // A list of the items, or for includingOne an option of the one. The
        // tie is the inner key or a unique field, which proves at most one
        // only as far as the database keeps it: a second is an error, as for
        // an owned optional record, never a choice.
        let outer = parts.Outer.Root.Name
        let none = ResizeArray<obj>()

        let some =
            lazy (FSharp.Reflection.FSharpType.GetUnionCases(parts.HeldType) |> Array.find (fun c -> c.Name = "Some"))

        let hold (key: obj) (items: ResizeArray<obj>) : obj =
            if parts.One then
                if items.Count > 1 then
                    raise (
                        MaterializationFailure
                            { Model = outer
                              Property = parts.OuterField.Name
                              Value = $"{items.Count} rows"
                              Message =
                                $"includingOne found {items.Count} {parts.Target.Name}s for one {outer} (its {parts.OuterField.Name} is {key}) where its tie on {parts.Target.Name}.{parts.MatchField.Name} proves at most one; the database holds more than the model says (is a unique constraint missing?)" }
                    )

                if items.Count = 0 then
                    null
                else
                    FSharp.Reflection.FSharpValue.MakeUnion(some.Value, [| items.[0] |])
            else
                Reflection.makeList parts.ElementType (items :> obj seq)

        return
            fun (r: obj[]) ->
                match valueOf r with
                | Some k ->
                    match groups.TryGetValue k with
                    | true, g -> hold k g
                    | _ -> hold k none
                | None -> hold null none
    }

/// A query ending in `including`s (including.feature): the outer records,
/// each in a tuple with the list of each level, in the order written.
let readIncluding (s: Session) (model: Model) (p: Planner.Including) : Task<obj list> =
    task {
        let outerSchema = p.Outer.RootSchema
        let! rows = query s (Sql.renderSelect s.Provider.Dialect p.Outer.Root) (readEntity outerSchema)
        do! loadInto s rows p.Outer.Loads

        let batchSize =
            match p.Strategy with
            | LoadStrategy.BatchedByKeys n -> max 1 n

        let lists = ResizeArray<obj[] -> obj>()

        for l in p.Levels do
            let! f = readLevel s model batchSize rows l
            lists.Add f

        let tuple =
            FSharp.Reflection.FSharpType.MakeTupleType(
                [| yield outerSchema.Type
                   for l in p.Levels -> l.Parts.HeldType |]
            )

        return [ for r in rows -> FSharp.Reflection.FSharpValue.MakeTuple([| yield outerSchema.Shape.Construct r; for f in lists -> f r |], tuple) ]
    }

// ---------------------------------------------------------------------------
// Hand-written SQL
// ---------------------------------------------------------------------------

/// Turns an interpolated string into SQL text and parameters: every hole is a
/// parameter, never text. `{{` and `}}` are literal braces.
let rawCommand (model: Model) (dialect: ISqlDialect) (sql: FormattableString) : Sql.Command =
    let format = sql.Format
    let args = sql.GetArguments()

    // Each hole's provider value, and the database type to cast it to when its
    // domain type is stored as one (Scalar.asPgEnum): (type, is an array).
    let toProvider (v: obj) : obj * (string list * bool) option =
        let scalar (x: obj) =
            if isNull x then
                box DBNull.Value
            else
                match model.ScalarToDb(x.GetType(), x) with
                | Ok p -> p
                | Error m -> invalidArg "sql" $"interpolated value: {m}"

        let cast (t: Type) array =
            model.StoreTypeOf t |> Option.map (fun st -> st, array)

        match v with
        | null -> box DBNull.Value, None
        | :? string
        | :? (byte[]) -> v, None
        | _ when Reflection.isOption (v.GetType()) ->
            let castTo = cast (Reflection.elementType (v.GetType())) false

            match Reflection.getOptionValue (v.GetType()) v with
            | Some x -> scalar x, castTo
            | None -> box DBNull.Value, castTo
        | :? System.Collections.IEnumerable as items ->
            // Lists and arrays become one array parameter (`= ANY({ids})`).
            let values = items |> Seq.cast<obj> |> Seq.toArray
            let elemType =
                let t = v.GetType()
                if t.IsArray then t.GetElementType()
                elif t.IsGenericType then t.GetGenericArguments().[0]
                else typeof<obj>

            let providerType = model.ProviderTypeOf elemType |> Option.defaultValue elemType
            let arr = Array.CreateInstance(providerType, values.Length)
            values |> Array.iteri (fun i x -> arr.SetValue(scalar x, i))
            box arr, cast elemType true
        | _ -> scalar v, cast (v.GetType()) false

    let sb = Text.StringBuilder()
    let ps = ResizeArray<obj>()
    let mutable i = 0

    while i < format.Length do
        let c = format.[i]

        if (c = '{' || c = '}') && i + 1 < format.Length && format.[i + 1] = c then
            sb.Append c |> ignore
            i <- i + 2
        elif c = '{' then
            let close = format.IndexOf('}', i)
            let spec = format.Substring(i + 1, close - i - 1)
            let n = int (spec.Split([| ','; ':' |]).[0])
            let value, castTo = toProvider args.[n]
            ps.Add value
            let name = dialect.ParameterName(ps.Count - 1)

            match castTo with
            | Some(t, array) -> sb.Append($"CAST({name} AS {Sql.typeName dialect t array})") |> ignore
            | None -> sb.Append name |> ignore
            i <- close + 1
        else
            sb.Append c |> ignore
            i <- i + 1

    { Sql = sb.ToString()
      Parameters = List.ofSeq ps }

let private snake (name: string) =
    let sb = Text.StringBuilder()

    name
    |> String.iteri (fun i c ->
        if Char.IsUpper c && i > 0 then sb.Append('_') |> ignore
        sb.Append(Char.ToLowerInvariant c) |> ignore)

    sb.ToString()

/// A reader for rows of hand-written SQL as `t`: a mapped record (columns by
/// name), an unmapped record (fields by name or snake_case name), a tuple
/// (columns in order) or a single scalar.
let rawReader (model: Model) (t: Type) (label: string) : DbDataReader -> obj =
    let fail property value message =
        raise (
            MaterializationFailure
                { Model = label
                  Property = property
                  Value = value
                  Message = message }
        )

    let ordinal (r: DbDataReader) (names: string list) (property: string) =
        let found =
            names
            |> List.tryPick (fun n ->
                [ 0 .. r.FieldCount - 1 ]
                |> List.tryFind (fun i -> String.Equals(r.GetName i, n, StringComparison.OrdinalIgnoreCase)))

        match found with
        | Some i -> i
        | None ->
            let available = [ for i in 0 .. r.FieldCount - 1 -> r.GetName i ] |> String.concat ", "
            fail property "" $"""column "{List.head names}" is missing from the SQL result (columns: {available})"""

    let value (r: DbDataReader) (i: int) = if r.IsDBNull i then null else r.GetValue i

    let convert (target: Type) property (raw: obj) =
        match model.ScalarFromDb(target, raw) with
        | Ok v -> v
        | Error m -> fail property (if isNull raw then "NULL" else string raw) m

    match model.TrySchema t with
    | Some schema ->
        let mutable ordinals: int[] = null

        fun r ->
            if isNull ordinals then
                ordinals <- schema.Fields |> Array.collect (fun f -> f.Parts |> List.map (fun p -> ordinal r [ p.Name ] f.Name) |> Array.ofList)

            let values = Array.zeroCreate<obj> schema.FieldCount
            let mutable j = 0

            for f in schema.Fields do
                let raws =
                    f.Parts
                    |> List.map (fun p ->
                        let raw = value r ordinals.[j]
                        j <- j + 1

                        if isNull raw || p.ProviderType.IsInstanceOfType raw then
                            raw
                        else
                            match Reflection.exactly p.ProviderType raw with
                            | Ok v -> v
                            | Error m -> fail f.Name (string raw) m)

                match f.FromColumns raws with
                | Ok v -> values.[f.Index] <- v
                | Error m -> fail f.Name (String.Join(", ", raws |> List.map (fun v -> if isNull v then "NULL" else string v))) m

            for a in schema.Assocs do
                values.[a.Index] <- a.Ctors.Empty

            // A record that owns or links lists: its field values, completed
            // by rawRecords.
            if schema.Assocs |> Array.exists (fun a -> a.Owned || a.Linked) then
                box values
            else
                schema.Shape.Construct values
    | None when Microsoft.FSharp.Reflection.FSharpType.IsRecord(t, BindingFlags.Public ||| BindingFlags.NonPublic) ->
        let fields = Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(t, BindingFlags.Public ||| BindingFlags.NonPublic)
        let ctor = Microsoft.FSharp.Reflection.FSharpValue.PreComputeRecordConstructor(t, BindingFlags.Public ||| BindingFlags.NonPublic)
        let mutable ordinals: int[] = null

        fun r ->
            if isNull ordinals then
                ordinals <- fields |> Array.map (fun f -> ordinal r [ f.Name; snake f.Name ] f.Name)

            fields
            |> Array.mapi (fun j (f: PropertyInfo) -> convert f.PropertyType f.Name (value r ordinals.[j]))
            |> ctor
    | None when Microsoft.FSharp.Reflection.FSharpType.IsTuple t ->
        let elements = Microsoft.FSharp.Reflection.FSharpType.GetTupleElements t
        let ctor = Microsoft.FSharp.Reflection.FSharpValue.PreComputeTupleConstructor t

        fun r ->
            if r.FieldCount <> elements.Length then
                fail "(tuple)" $"{r.FieldCount} columns" $"the SQL returns {r.FieldCount} columns for a {elements.Length}-element tuple"

            elements |> Array.mapi (fun i et -> convert et $"Item{i + 1}" (value r i)) |> ctor
    | None ->
        fun r ->
            if r.FieldCount <> 1 then
                fail "(value)" $"{r.FieldCount} columns" $"the SQL returns {r.FieldCount} columns for a single {Reflection.typeName t}"

            convert t "(value)" (value r 0)

/// The rows of hand-written SQL as 'R values: a record that owns or links
/// lists is completed with them, as a query of it would be.
let rawRecords (s: Session) (model: Model) (t: Type) (cmd: Sql.Command) : Task<obj list> =
    task {
        let! rows = query s cmd (rawReader model t "SQL")

        match model.TrySchema t with
        | Some schema when schema.Assocs |> Array.exists (fun a -> a.Owned || a.Linked) ->
            let values = rows |> Seq.map (fun v -> v :?> obj[]) |> List.ofSeq
            do! loadOwned s model schema values
            return values |> List.map schema.Shape.Construct
        | _ -> return List.ofSeq rows
    }
