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
/// with every relationship set to NotLoaded.
let readEntity (schema: SchemaInfo) : DbDataReader -> obj[] =
    let readers = readersFor schema

    fun r ->
        let values = Array.zeroCreate<obj> schema.FieldCount
        let mutable j = 0

        for f in schema.Fields do
            // A field reads one column, or a union with fields its several.
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
            values.[a.Index] <- a.Ctors.NotLoaded

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

            // (owner key, child row); for many-to-many one target row may
            // belong to several owners and is materialised once.
            let entries = ResizeArray<obj * obj[]>()
            let childRows = ResizeArray<obj[]>()

            let batchSize =
                match node.Strategy with
                | LoadStrategy.BatchedByKeys n
                | LoadStrategy.PerParentWindow(_, n) -> max 1 n

            match node.Through with
            | None ->
                for batch in Seq.chunkBySize batchSize keys do
                    let select = node.Build(providerArray node.KeyField batch)
                    let cmd = Sql.renderSelect s.Provider.Dialect select
                    let! got = query s cmd (readEntity node.Target)
                    childRows.AddRange got

                for cr in childRows do
                    match keyOf node.MatchField cr.[node.MatchField.Index] with
                    | Some k -> entries.Add(k, cr)
                    | None -> ()
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

            let built = Dictionary<obj[], obj>(HashIdentity.Reference)

            let construct (row: obj[]) =
                match built.TryGetValue row with
                | true, v -> v
                | _ ->
                    let v = node.Target.Shape.Construct row
                    built.[row] <- v
                    v

            let groups = Dictionary<obj, ResizeArray<obj>>()

            for k, cr in entries do
                match groups.TryGetValue k with
                | true, g -> g.Add(construct cr)
                | _ -> groups.[k] <- ResizeArray [ construct cr ]

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

let rec private build (shape: Translate.Shape) (row: obj[]) : obj =
    match shape with
    | Translate.Const v -> v
    | Translate.Leaf(i, _, _, _) -> row.[i]
    | Translate.Build(ctor, parts) -> ctor (parts |> List.map (fun p -> build p row) |> Array.ofList)
    | Translate.EntityLeaf(start, schema, optional) ->
        // Each field's columns start where the fields before it end.
        let offsets = schema.Fields |> Array.scan (fun at f -> at + f.Parts.Length) start
        let keyAt = offsets.[schema.Fields |> Array.findIndex (fun f -> f.Name = schema.Key.Name)]

        if optional && isNull row.[keyAt] then
            null // None: the left-joined source had no match
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
                values.[a.Index] <- a.Ctors.NotLoaded

            let record = schema.Shape.Construct values

            if optional then
                Reflection.makeSome (typedefof<option<_>>.MakeGenericType schema.Type) record
            else
                record

let readProjection (s: Session) (select: Sql.Select) (shape: Translate.Shape) (model: string) : Task<obj list> =
    let leaves = ResizeArray<int * Type * (obj -> Result<obj, string>) * string>()

    let rec collect sh =
        match sh with
        | Translate.Leaf(i, t, c, l) -> leaves.Add(i, t, c, l)
        | Translate.EntityLeaf(start, schema, _) ->
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

                for i, t, convert, label in leaves do
                    let raw = (columnReader t).Invoke(r, i)

                    match convert raw with
                    | Ok v -> values.[i] <- v
                    | Error msg ->
                        raise (
                            MaterializationFailure
                                { Model = model
                                  Property = label
                                  Value = describe raw
                                  Message = msg }
                        )

                values)

        return rows |> Seq.map (build shape) |> List.ofSeq
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
    | Some schema when schema.Assocs |> Array.exists (fun a -> a.Owned) ->
        let owned = (schema.Assocs |> Array.find (fun a -> a.Owned)).Name
        fun _ -> fail owned "(owned list)" $"hand-written SQL cannot read a whole {schema.Name}: its owned list {owned} is loaded with its owner. Query {schema.Name} itself, or read its columns into a record of your own"
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

                        if isNull raw || p.ProviderType.IsInstanceOfType raw then raw
                        else Convert.ChangeType(raw, p.ProviderType, Globalization.CultureInfo.InvariantCulture))

                match f.FromColumns raws with
                | Ok v -> values.[f.Index] <- v
                | Error m -> fail f.Name (String.Join(", ", raws |> List.map (fun v -> if isNull v then "NULL" else string v))) m

            for a in schema.Assocs do
                values.[a.Index] <- a.Ctors.NotLoaded

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
