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
let private bindArgs (s: Session) (cmd: Sql.Command) : Sql.Command =
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

let private parameters (s: Session) (cmd: Sql.Command) (create: unit -> DbParameter) (add: DbParameter -> unit) =
    cmd.Parameters
    |> List.iteri (fun i v ->
        let p = create ()
        p.ParameterName <- s.Provider.Dialect.ParameterName i
        p.Value <- v
        add p)

let private prepare (s: Session) (cmd: Sql.Command) =
    let c = s.Connection.CreateCommand()
    c.CommandText <- cmd.Sql

    match s.Transaction with
    | Some t -> c.Transaction <- Sandboxes.unwrap t
    | None -> ()

    parameters s cmd c.CreateParameter (c.Parameters.Add >> ignore)
    c

let mutable private roundTrips = 0L

let private nextRoundTrip () = Interlocked.Increment(&roundTrips)

let private report (s: Session) (cmd: Sql.Command) (elapsed: TimeSpan) rows ok roundTrip =
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
              Succeeded = ok
              RoundTrip = roundTrip }

// ---------------------------------------------------------------------------
// Round trips (round-trips.feature)
// ---------------------------------------------------------------------------

/// One statement of a round trip, and how its rows are read.
type Statement =
    { Command: Sql.Command
      /// Reads one row; None for a statement that returns none, whose result
      /// is the rows it affected.
      Read: (DbDataReader -> unit) option }

/// What became of one statement of a round trip.
type Sent =
    /// It ran: the rows it read, or else affected.
    | Ran of rows: int
    | Threw of exn
    /// An earlier statement failed, so it was not run.
    | Skipped

/// Statements to send and what follows from their results: each Send is one
/// round trip. `last` says that what follows sends nothing more, so a
/// statement that must come after these can travel with them.
type Round<'a> =
    | Done of 'a
    | Send of Statement list * last: bool * (Sent list -> Round<'a>)

/// A statement's result: its value, or the exception it raised (None when it
/// was not run because an earlier statement failed).
type Reply<'a> = Result<'a, exn option>

module Round =

    let rec map (f: 'a -> 'b) (r: Round<'a>) : Round<'b> =
        match r with
        | Done v -> Done(f v)
        | Send(stmts, last, k) -> Send(stmts, last, fun sent -> map f (k sent))

    /// What `f` does with `r`'s value, after it: a round trip later.
    let rec bind (f: 'a -> Round<'b>) (r: Round<'a>) : Round<'b> =
        match r with
        | Done v -> f v
        | Send(stmts, _, k) -> Send(stmts, false, fun sent -> bind f (k sent))

    /// Both, each round trip of one with the same of the other, `a`'s
    /// statements first.
    let rec both (a: Round<'a>) (b: Round<'b>) : Round<'a * 'b> =
        match a, b with
        | Done x, Done y -> Done(x, y)
        | Done x, _ -> map (fun y -> x, y) b
        | _, Done y -> map (fun x -> x, y) a
        | Send(sa, la, ka), Send(sb, lb, kb) ->
            let n = sa.Length
            Send(sa @ sb, la && lb, fun sent -> both (ka (List.take n sent)) (kb (List.skip n sent)))

    let all (rs: Round<'a> list) : Round<'a list> =
        List.foldBack (fun r acc -> both r acc |> map (fun (x, xs) -> x :: xs)) rs (Done [])

    /// `b` after `a` has sent everything: with `a`'s last round trip when `a`
    /// says which that is, else after it.
    let rec andThen (a: Round<'a>) (b: Round<'b>) : Round<'a * 'b> =
        match a with
        | Done x -> map (fun y -> x, y) b
        | Send(_, true, _) -> both a b
        | Send(stmts, false, k) -> Send(stmts, false, fun sent -> andThen (k sent) b)

    /// `r`, then what `f` sends when `r`'s value calls for it: a read that
    /// only a failure needs, which statements sent after `r` need not wait
    /// for (so `last` stays as `r` says).
    let rec followUp (f: 'a -> Round<'a> option) (r: Round<'a>) : Round<'a> =
        match r with
        | Done v ->
            match f v with
            | Some next -> next
            | None -> Done v
        | Send(stmts, last, k) -> Send(stmts, last, fun sent -> followUp f (k sent))

    let private reply (sent: Sent) (value: 'a) : Reply<'a> =
        match sent with
        | Ran _ -> Ok value
        | Threw ex -> Error(Some ex)
        | Skipped -> Error None

    /// The rows a query reads.
    let fetch (s: Session) (cmd: Sql.Command) (read: DbDataReader -> 'a) : Round<Reply<ResizeArray<'a>>> =
        let rows = ResizeArray<'a>()

        Send(
            [ { Command = bindArgs s cmd; Read = Some(fun r -> rows.Add(read r)) } ],
            true,
            fun sent -> Done(reply sent.Head rows)
        )

    /// The rows a statement affects.
    let exec (s: Session) (cmd: Sql.Command) : Round<Reply<int>> =
        Send(
            [ { Command = bindArgs s cmd; Read = None } ],
            true,
            fun sent ->
                Done(
                    match sent.Head with
                    | Ran n -> Ok n
                    | other -> reply other 0
                )
        )

    let private raised (r: Reply<'a>) : 'a =
        match r with
        | Ok v -> v
        | Error(Some ex) -> Err.rethrow ex
        | Error None -> invalidOp "a statement was not sent because an earlier one failed"

    /// The rows a query reads, raising what it raises.
    let rows (s: Session) (cmd: Sql.Command) (read: DbDataReader -> 'a) : Round<ResizeArray<'a>> =
        fetch s cmd read |> map raised

    let private one (s: Session) (st: Statement) : Task<Sent> =
        task {
            let trip = nextRoundTrip ()
            use c = prepare s st.Command
            let sw = Stopwatch.StartNew()
            let mutable n = 0

            try
                match st.Read with
                | Some read ->
                    use! r = c.ExecuteReaderAsync(s.Ct)
                    let mutable go = true

                    while go do
                        let! more = r.ReadAsync(s.Ct)

                        if more then
                            read r
                            n <- n + 1
                        else
                            go <- false
                | None ->
                    let! affected = c.ExecuteNonQueryAsync(s.Ct)
                    n <- affected

                report s st.Command sw.Elapsed n true trip
                return Ran n
            with ex ->
                report s st.Command sw.Elapsed n false trip
                return Threw ex
        }

    let private oneAtATime (s: Session) (stmts: Statement list) : Task<Sent list> =
        task {
            let sent = ResizeArray<Sent>()
            let mutable failed = false

            for st in stmts do
                if failed then
                    sent.Add Skipped
                else
                    let! r = one s st
                    sent.Add r

                    match r with
                    | Threw _ -> failed <- true
                    | _ -> ()

            return List.ofSeq sent
        }

    /// The statements as one DbBatch: each query's rows are its result set,
    /// in order; a statement without rows has none, and its count is its
    /// batch command's.
    let private together (s: Session) (stmts: Statement list) : Task<Sent list> =
        task {
            let stmts = Array.ofList stmts
            let trip = nextRoundTrip ()
            use batch = s.Connection.CreateBatch()

            match s.Transaction with
            | Some t -> batch.Transaction <- Sandboxes.unwrap t
            | None -> ()

            for st in stmts do
                let c = batch.CreateBatchCommand()
                c.CommandText <- st.Command.Sql
                parameters s st.Command c.CreateParameter (c.Parameters.Add >> ignore)
                batch.BatchCommands.Add c

            let rows = Array.zeroCreate<int> stmts.Length
            let sw = Stopwatch.StartNew()
            // The statement being read, and the last read whole.
            let mutable reading = -1
            let mutable read = -1
            let mutable failure = None

            try
                use! r = batch.ExecuteReaderAsync(s.Ct)
                let mutable first = true

                for i in 0 .. stmts.Length - 1 do
                    match stmts.[i].Read with
                    | Some readRow ->
                        reading <- i

                        if not first then
                            let! more = r.NextResultAsync(s.Ct)

                            if not more then
                                invalidOp $"statement {i + 1} of a batch returned no rows to read"

                        first <- false
                        let mutable go = true

                        while go do
                            let! more = r.ReadAsync(s.Ct)

                            if more then
                                readRow r
                                rows.[i] <- rows.[i] + 1
                            else
                                go <- false

                        read <- i
                    | None -> ()

                reading <- -1
                let mutable more = true

                while more do
                    let! m = r.NextResultAsync(s.Ct)
                    more <- m
            with ex ->
                failure <- Some ex

            let elapsed = sw.Elapsed

            let failedAt =
                match failure with
                | None -> stmts.Length
                | Some(:? DbException as d) when not (isNull d.BatchCommand) -> batch.BatchCommands.IndexOf d.BatchCommand
                | Some _ when reading >= 0 -> reading
                | Some _ -> read + 1

            return
                [ for i in 0 .. stmts.Length - 1 do
                      let st = stmts.[i]

                      if i < failedAt then
                          let n =
                              match st.Read with
                              | Some _ -> rows.[i]
                              | None -> batch.BatchCommands.[i].RecordsAffected

                          report s st.Command elapsed n true trip
                          Ran n
                      elif i = failedAt then
                          report s st.Command elapsed rows.[i] false trip
                          Threw failure.Value
                      else
                          Skipped ]
        }

    let private send (s: Session) (stmts: Statement list) : Task<Sent list> =
        match stmts with
        | [] -> Task.FromResult []
        | [ _ ] -> oneAtATime s stmts
        | _ when s.Provider.Dialect.Capabilities.Batching -> together s stmts
        | _ -> oneAtATime s stmts

    /// Runs `r`: each Send one round trip. Once a statement has failed, the
    /// transaction can run nothing more, so what follows is told its
    /// statements were not sent.
    let run (s: Session) (r: Round<'a>) : Task<'a> =
        task {
            let mutable current = r
            let mutable failed = false
            let mutable result = None

            while result.IsNone do
                match current with
                | Done v -> result <- Some v
                | Send(stmts, _, k) ->
                    let! sent =
                        if failed then
                            Task.FromResult(stmts |> List.map (fun _ -> Skipped))
                        else
                            send s stmts

                    if sent |> List.exists (function | Threw _ -> true | _ -> false) then
                        failed <- true

                    current <- k sent

            return result.Value
        }

/// Runs a query command, reading each row with `read`.
let query (s: Session) (cmd: Sql.Command) (read: DbDataReader -> 'a) : Task<ResizeArray<'a>> =
    Round.run s (Round.rows s cmd read)

/// Runs a non-query command, returning affected rows.
let execute (s: Session) (cmd: Sql.Command) : Task<int> =
    task {
        match! Round.run s (Round.exec s cmd) with
        | Ok n -> return n
        | Error(Some ex) -> return Err.rethrow ex
        | Error None -> return invalidOp "the statement was not sent"
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
                                      Message = $"the {node.Parent.Name} with key {key.Value} owns at most one {node.Target.Name}, and the database holds {found.Count} (is a unique constraint missing?)" }
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
                                        $"the {node.Parent.Name} with key {key} owns exactly one {node.Target.Name}, and the database holds {found.Count}" }
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

/// One load (loads.feature): reads what its query finds for the
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
                match parts.Return with
                | Some r -> fun (cr: obj[]) -> r.Build (parts.Target.Shape.Construct cr) [| for f in lists -> f cr |]
                | None -> fun (cr: obj[]) -> parts.Target.Shape.Construct cr

            for cr in childRows do
                add (keyOf parts.MatchField cr.[parts.MatchField.Index]) (element cr)

        // A list of the items, or for a let! of referredBy an option of the
        // one. The tie is the inner key, which proves at most one
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
                                $"a let! of referredBy found {items.Count} {parts.Target.Name}s for one {outer} (its {parts.OuterField.Name} is {key}) where its tie on {parts.Target.Name}.{parts.MatchField.Name} proves at most one; the database holds more than the model says (is a unique constraint missing?)" }
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

/// A query that binds loads (loads.feature): the outer records, each in the
/// row its `return` builds from it and what each load found for it.
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

        return [ for r in rows -> p.Return.Build (outerSchema.Shape.Construct r) [| for f in lists -> f r |] ]
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
