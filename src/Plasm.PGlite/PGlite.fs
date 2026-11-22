namespace Plasm.PGlite

open System
open System.Collections
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Plasm

/// The transport to one PGlite instance. The host (a browser app, through
/// [<JSImport>]) implements it; the provider never touches JavaScript itself.
///
/// - `Query` runs one statement with parameters and returns JSON:
///   `{"fields":[[name,oid],...],"rows":[[text|null,...],...],"rowCount":n}`
///   or `{"error":{"code":..,"constraint":..,"message":..}}`. Values are
///   PostgreSQL's text output; parameters arrive as a JSON array of text
///   values, nulls and arrays of those, with a parallel array of type OIDs.
/// - `Execute` runs one parameterless statement synchronously (transaction
///   control, which ADO.NET's synchronous `DbTransaction` API requires) and
///   returns "" or the same error JSON.
type IPGliteBridge =
    abstract Query: sql: string * paramsJson: string * typesJson: string -> Task<string>
    abstract Execute: sql: string -> string

/// A PostgreSQL error reported by PGlite.
type PGliteException(sqlState: string, constraintName: string, messageText: string, detail: string) =
    inherit DbException(if isNull sqlState then messageText else $"{sqlState}: {messageText}")
    override _.SqlState = sqlState
    member _.ConstraintName = constraintName
    member _.MessageText = messageText
    member _.Detail = detail

module internal Oid =
    let bool = 16
    let bytea = 17
    let int8 = 20
    let int2 = 21
    let int4 = 23
    let text = 25
    let json = 114
    let float4 = 700
    let float8 = 701
    let date = 1082
    let time = 1083
    let timestamp = 1114
    let timestamptz = 1184
    let interval = 1186
    let numeric = 1700
    let uuid = 2950
    let jsonb = 3802

    /// Array type OID -> element type OID.
    let arrays =
        dict
            [ 1000, bool
              1001, bytea
              1005, int2
              1007, int4
              1016, int8
              1009, text
              1015, 1043
              1021, float4
              1022, float8
              1182, date
              1115, timestamp
              1185, timestamptz
              1231, numeric
              2951, uuid ]

    /// What Npgsql would send for a CLR value (so PostgreSQL resolves
    /// parameter types the same way). 0 = let the server infer.
    let rec ofType (t: Type) (v: obj) =
        match v with
        | :? DateTime as d -> if d.Kind = DateTimeKind.Utc then timestamptz else timestamp
        | _ ->
            match t with
            | t when t = typeof<string> -> text
            | t when t = typeof<int64> -> int8
            | t when t = typeof<int> -> int4
            | t when t = typeof<int16> -> int2
            | t when t = typeof<bool> -> bool
            | t when t = typeof<decimal> -> numeric
            | t when t = typeof<float> -> float8
            | t when t = typeof<float32> -> float4
            | t when t = typeof<Guid> -> uuid
            | t when t = typeof<DateTimeOffset> -> timestamptz
            | t when t = typeof<DateOnly> -> date
            | t when t = typeof<TimeOnly> -> time
            | t when t = typeof<TimeSpan> -> interval
            | t when t = typeof<byte[]> -> bytea
            | t when t.IsArray ->
                let e = t.GetElementType()

                // A DateTime[] is timestamptz[] when its values are UTC (as Npgsql).
                let elem =
                    if e = typeof<DateTime> then
                        let a = v :?> Array

                        if a.Length > 0 && (a.GetValue 0 :?> DateTime).Kind <> DateTimeKind.Utc then
                            timestamp
                        else
                            timestamptz
                    else
                        ofType e null

                arrays |> Seq.tryFind (fun kv -> kv.Value = elem) |> Option.map (fun kv -> kv.Key) |> Option.defaultValue 0
            | _ -> 0

/// Converts between CLR values and PostgreSQL's text formats.
module internal Values =
    let inv = CultureInfo.InvariantCulture

    let rec toText (v: obj) : string =
        match v with
        | null -> null
        | :? DBNull -> null
        | :? string as s -> s
        | :? bool as b -> if b then "t" else "f"
        | :? decimal as d -> d.ToString(inv)
        | :? float as f ->
            if Double.IsPositiveInfinity f then "Infinity"
            elif Double.IsNegativeInfinity f then "-Infinity"
            else f.ToString("R", inv)
        | :? float32 as f -> f.ToString("R", inv)
        | :? DateTime as d ->
            if d.Kind = DateTimeKind.Utc then
                d.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", inv)
            else
                d.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", inv)
        | :? DateTimeOffset as d -> d.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", inv)
        | :? DateOnly as d -> d.ToString("yyyy-MM-dd", inv)
        | :? TimeOnly as t -> t.ToString("HH:mm:ss.ffffff", inv)
        | :? TimeSpan as t -> Xml.XmlConvert.ToString t
        | :? Guid as g -> g.ToString()
        | :? (byte[]) as b -> "\\x" + Convert.ToHexString(b).ToLowerInvariant()
        | :? IFormattable as f -> f.ToString(null, inv)
        | v -> string v

    let writeParams (values: obj list) =
        use ms = new MemoryStream()

        do
            use w = new Utf8JsonWriter(ms)
            w.WriteStartArray()

            for v in values do
                match v with
                | :? Array as a when not (a :? (byte[])) ->
                    w.WriteStartArray()

                    for x in a do
                        match toText x with
                        | null -> w.WriteNullValue()
                        | s -> w.WriteStringValue s

                    w.WriteEndArray()
                | v ->
                    match toText v with
                    | null -> w.WriteNullValue()
                    | s -> w.WriteStringValue s

            w.WriteEndArray()

        Encoding.UTF8.GetString(ms.ToArray())

    let writeTypes (values: obj list) =
        values
        |> List.map (fun v ->
            match v with
            | null
            | :? DBNull -> "0"
            | v -> string (Oid.ofType (v.GetType()) v))
        |> String.concat ","
        |> fun items -> "[" + items + "]"

    let private timestampFormats =
        [| "yyyy-MM-dd HH:mm:ss.FFFFFFzzz"; "yyyy-MM-dd HH:mm:ss.FFFFFFzz" |]

    let private parseInterval (s: string) =
        if s.StartsWith "P" || s.StartsWith "-P" then
            Xml.XmlConvert.ToTimeSpan s
        else
            TimeSpan.Parse(s, inv)

    /// A CLR value for a text value of a type OID: what Npgsql's GetValue returns.
    let rec ofText (oid: int) (s: string) : obj =
        match oid with
        | 16 -> box (s = "t")
        | 20 -> box (Int64.Parse(s, inv))
        | 21 -> box (Int16.Parse(s, inv))
        | 23 -> box (Int32.Parse(s, inv))
        | 26 -> box (UInt32.Parse(s, inv))
        | 700 -> box (Single.Parse(s, inv))
        | 701 -> box (Double.Parse(s, inv))
        | 1700 -> box (Decimal.Parse(s, NumberStyles.Float, inv))
        | 1184 ->
            let d = DateTimeOffset.ParseExact(s, timestampFormats, inv, DateTimeStyles.None)
            box d.UtcDateTime
        | 1114 -> box (DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss.FFFFFF", inv, DateTimeStyles.None))
        | 1082 -> box (DateTime.ParseExact(s, "yyyy-MM-dd", inv, DateTimeStyles.None))
        | 1083 -> box (TimeSpan.Parse(s, inv))
        | 1186 -> box (parseInterval s)
        | 2950 -> box (Guid.Parse s)
        | 17 -> box (Convert.FromHexString(s.Substring 2))
        | oid when Oid.arrays.ContainsKey oid -> parseArray Oid.arrays[oid] s
        | _ -> box s

    /// One-dimensional PostgreSQL array text ({a,"b c",NULL}) to a typed array.
    and parseArray (elemOid: int) (s: string) : obj =
        let items = ResizeArray<string>()
        let mutable i = 1
        let sb = StringBuilder()

        while i < s.Length - 1 do
            if s[i] = '"' then
                i <- i + 1

                while s[i] <> '"' do
                    if s[i] = '\\' then i <- i + 1
                    sb.Append s[i] |> ignore
                    i <- i + 1

                items.Add(sb.ToString())
                sb.Clear() |> ignore
                i <- i + 2 // closing quote and separator
            else
                let next = s.IndexOf(',', i)
                let stop = if next < 0 || next > s.Length - 1 then s.Length - 1 else next
                let raw = s.Substring(i, stop - i)
                items.Add(if raw = "NULL" then null else raw)
                i <- stop + 1

        let values = items |> Seq.map (fun x -> if isNull x then null else ofText elemOid x) |> Array.ofSeq

        let elemType =
            values |> Array.tryFind (isNull >> not) |> Option.map (fun v -> v.GetType()) |> Option.defaultValue typeof<obj>

        let arr = Array.CreateInstance(elemType, values.Length)
        values |> Array.iteri (fun j v -> arr.SetValue(v, j))
        box arr

    let typeName (oid: int) =
        match oid with
        | 16 -> "boolean"
        | 20 -> "bigint"
        | 21 -> "smallint"
        | 23 -> "integer"
        | 25 -> "text"
        | 700 -> "real"
        | 701 -> "double precision"
        | 1043 -> "character varying"
        | 1082 -> "date"
        | 1114 -> "timestamp without time zone"
        | 1184 -> "timestamp with time zone"
        | 1700 -> "numeric"
        | 2950 -> "uuid"
        | o -> $"oid {o}"

    /// Converts a stored value to the type a reader asks for.
    let convert<'T> (v: obj) : 'T =
        match v with
        | :? 'T as t -> t
        | _ ->
            let target = typeof<'T>

            let converted: obj =
                match v with
                | :? DateTime as d when target = typeof<DateOnly> -> box (DateOnly.FromDateTime d)
                | :? DateTime as d when target = typeof<DateTimeOffset> -> box (DateTimeOffset(d))
                | :? TimeSpan as t when target = typeof<TimeOnly> -> box (TimeOnly.FromTimeSpan t)
                | :? string as s when target = typeof<char> -> box s[0]
                | :? IConvertible -> Convert.ChangeType(v, target, inv)
                | _ -> raise (InvalidCastException $"cannot read a {v.GetType().Name} as {target.Name}")

            unbox<'T> converted

    let private errorOf (e: JsonElement) =
        let str (name: string) =
            match e.TryGetProperty name with
            | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
            | _ -> null

        PGliteException(str "code", str "constraint", str "message", str "detail")

    /// The BEGIN a transaction at this level sends, and OnCommand reports.
    let beginAt (isolation: IsolationLevel) =
        match isolation with
        | IsolationLevel.Serializable -> "BEGIN ISOLATION LEVEL SERIALIZABLE"
        | IsolationLevel.RepeatableRead
        | IsolationLevel.Snapshot -> "BEGIN ISOLATION LEVEL REPEATABLE READ"
        | IsolationLevel.ReadUncommitted -> "BEGIN ISOLATION LEVEL READ UNCOMMITTED"
        | _ -> "BEGIN ISOLATION LEVEL READ COMMITTED"

    /// Raises the error a bridge reported, if any.
    let throwIfError (json: string) =
        if not (String.IsNullOrEmpty json) then
            use doc = JsonDocument.Parse json

            match doc.RootElement.TryGetProperty "error" with
            | true, e -> raise (errorOf e)
            | _ -> ()

    type Result =
        { Names: string[]
          Oids: int[]
          Rows: ResizeArray<obj[]>
          RowCount: int }

    let parseResult (json: string) : Result =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        match root.TryGetProperty "error" with
        | true, e -> raise (errorOf e)
        | _ -> ()

        let fields = root.GetProperty("fields").EnumerateArray() |> Seq.toArray
        let names = fields |> Array.map (fun f -> f[0].GetString())
        let oids = fields |> Array.map (fun f -> f[1].GetInt32())
        let rows = ResizeArray<obj[]>()

        for r in root.GetProperty("rows").EnumerateArray() do
            let values = Array.zeroCreate<obj> oids.Length
            let mutable j = 0

            for c in r.EnumerateArray() do
                values[j] <-
                    if c.ValueKind = JsonValueKind.Null then
                        box DBNull.Value
                    else
                        ofText oids[j] (c.GetString())

                j <- j + 1

            rows.Add values

        let rowCount =
            match root.TryGetProperty "rowCount" with
            | true, n when n.ValueKind = JsonValueKind.Number -> n.GetInt32()
            | _ -> -1

        { Names = names
          Oids = oids
          Rows = rows
          RowCount = rowCount }

    /// Plasm's `@pN` parameters (zero-based) to PostgreSQL's `$N+1`, outside
    /// string literals, quoted identifiers and comments.
    let positional (sql: string) =
        let sb = StringBuilder(sql.Length)
        let mutable i = 0

        let copyUntil (close: string) =
            let stop = sql.IndexOf(close, i + 1, StringComparison.Ordinal)
            let stop = if stop < 0 then sql.Length else stop + close.Length
            sb.Append(sql, i, stop - i) |> ignore
            i <- stop

        while i < sql.Length do
            match sql[i] with
            | '\'' -> copyUntil "'"
            | '"' -> copyUntil "\""
            | '-' when i + 1 < sql.Length && sql[i + 1] = '-' -> copyUntil "\n"
            | '/' when i + 1 < sql.Length && sql[i + 1] = '*' -> copyUntil "*/"
            | '@' when i + 2 < sql.Length && sql[i + 1] = 'p' && Char.IsDigit sql[i + 2] ->
                let mutable j = i + 2

                while j < sql.Length && Char.IsDigit sql[j] do
                    j <- j + 1

                let n = Int32.Parse(sql.AsSpan(i + 2, j - i - 2), provider = inv)
                sb.Append('$').Append(n + 1) |> ignore
                i <- j
            | c ->
                sb.Append c |> ignore
                i <- i + 1

        sb.ToString()

[<AllowNullLiteral>]
type PGliteParameter() =
    inherit DbParameter()
    let mutable name = ""
    let mutable value: obj = null
    override val DbType = DbType.Object with get, set
    override val Direction = ParameterDirection.Input with get, set
    override val IsNullable = true with get, set

    override _.ParameterName
        with get () = name
        and set v = name <- (if isNull v then "" else v)

    override val SourceColumn = "" with get, set
    override val SourceColumnNullMapping = false with get, set
    override val Size = 0 with get, set

    override _.Value
        with get () = value
        and set v = value <- v

    override this.ResetDbType() = this.DbType <- DbType.Object

type PGliteParameterCollection() =
    inherit DbParameterCollection()
    let items = ResizeArray<PGliteParameter>()
    member _.Items = items
    override _.Count = items.Count
    override _.SyncRoot = box items

    override _.Add(value: obj) =
        items.Add(value :?> PGliteParameter)
        items.Count - 1

    override _.AddRange(values: Array) =
        for v in values do
            items.Add(v :?> PGliteParameter)

    override _.Clear() = items.Clear()
    override _.Contains(value: obj) = items.Contains(value :?> PGliteParameter)
    override _.Contains(value: string) = items.Exists(fun p -> p.ParameterName = value)
    override _.CopyTo(array: Array, index: int) = (items :> ICollection).CopyTo(array, index)
    override _.GetEnumerator() = (items :> IEnumerable).GetEnumerator()
    override _.IndexOf(value: obj) = items.IndexOf(value :?> PGliteParameter)
    override _.IndexOf(name: string) = items.FindIndex(fun p -> p.ParameterName = name)
    override _.Insert(index: int, value: obj) = items.Insert(index, value :?> PGliteParameter)
    override _.Remove(value: obj) = items.Remove(value :?> PGliteParameter) |> ignore
    override _.RemoveAt(index: int) = items.RemoveAt index
    override this.RemoveAt(name: string) = items.RemoveAt(this.IndexOf name)
    override _.GetParameter(index: int) = items[index] :> DbParameter
    override this.GetParameter(name: string) = items[this.IndexOf name] :> DbParameter
    override _.SetParameter(index: int, value: DbParameter) = items[index] <- value :?> PGliteParameter
    override this.SetParameter(name: string, value: DbParameter) = items[this.IndexOf name] <- value :?> PGliteParameter

/// A fully read result, presented as a forward-only reader.
type PGliteDataReader internal (result: Values.Result) =
    inherit DbDataReader()
    let mutable row = -1
    let mutable closed = false
    let current (i: int) = result.Rows[row][i]

    override _.FieldCount = result.Names.Length
    override _.HasRows = result.Rows.Count > 0
    override _.IsClosed = closed
    override _.RecordsAffected = result.RowCount
    override _.Depth = 0

    override _.Read() =
        row <- row + 1
        row < result.Rows.Count

    override _.NextResult() = false
    override _.Close() = closed <- true
    override _.GetName i = result.Names[i]

    override _.GetOrdinal name =
        match Array.tryFindIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) result.Names with
        | Some i -> i
        | None -> raise (IndexOutOfRangeException name)

    override _.GetDataTypeName i = Values.typeName result.Oids[i]

    override this.GetFieldType i =
        // The type of the first non-null value; text when every value is null.
        result.Rows
        |> Seq.map (fun r -> r[i])
        |> Seq.tryFind (fun v -> not (v :? DBNull))
        |> Option.map (fun v -> v.GetType())
        |> Option.defaultValue typeof<string>

    override _.IsDBNull i = current i :? DBNull
    override _.GetValue i = current i

    override _.GetValues(values: obj[]) =
        let n = min values.Length result.Names.Length

        for i in 0 .. n - 1 do
            values[i] <- current i

        n

    override _.Item
        with get (i: int) = current i

    override this.Item
        with get (name: string) = current (this.GetOrdinal name)

    override _.GetFieldValue<'T>(i: int) : 'T =
        match current i with
        | :? DBNull -> raise (InvalidCastException $"column {result.Names[i]} is null")
        | v -> Values.convert<'T> v

    override this.GetBoolean i = this.GetFieldValue<bool> i
    override this.GetByte i = this.GetFieldValue<byte> i
    override this.GetChar i = this.GetFieldValue<char> i
    override this.GetDateTime i = this.GetFieldValue<DateTime> i
    override this.GetDecimal i = this.GetFieldValue<decimal> i
    override this.GetDouble i = this.GetFieldValue<float> i
    override this.GetFloat i = this.GetFieldValue<float32> i
    override this.GetGuid i = this.GetFieldValue<Guid> i
    override this.GetInt16 i = this.GetFieldValue<int16> i
    override this.GetInt32 i = this.GetFieldValue<int> i
    override this.GetInt64 i = this.GetFieldValue<int64> i
    override this.GetString i = this.GetFieldValue<string> i

    override this.GetBytes(i, offset, buffer, bufferOffset, length) =
        let b = this.GetFieldValue<byte[]> i

        if isNull buffer then
            int64 b.Length
        else
            let n = max 0 (min length (b.Length - int offset))
            Array.blit b (int offset) buffer bufferOffset n
            int64 n

    override this.GetChars(i, offset, buffer, bufferOffset, length) =
        let s = this.GetString i

        if isNull buffer then
            int64 s.Length
        else
            let n = max 0 (min length (s.Length - int offset))
            s.CopyTo(int offset, buffer, bufferOffset, n)
            int64 n

    override this.GetEnumerator() = (new DbEnumerator(this)) :> IEnumerator

/// A transaction on PGlite's single session: BEGIN / COMMIT / ROLLBACK and
/// savepoints, sent synchronously (ADO.NET's DbTransaction API is synchronous
/// and Plasm's sandbox uses it so).
[<AllowNullLiteral>]
type PGliteTransaction internal (connection: PGliteConnection, bridge: IPGliteBridge, isolation: IsolationLevel) =
    inherit DbTransaction()
    let mutable finished = false

    let run sql = Values.throwIfError (bridge.Execute sql)

    let checkName (name: string) =
        if String.IsNullOrEmpty name || not (name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')) then
            invalidArg "savepoint" $"invalid savepoint name '{name}'"

        name

    member _.Finished = finished
    override _.DbConnection = connection :> DbConnection
    override _.IsolationLevel = isolation

    override _.Commit() =
        if finished then invalidOp "the transaction has already been committed or rolled back"
        finished <- true
        run "COMMIT"

    override _.Rollback() =
        if finished then invalidOp "the transaction has already been committed or rolled back"
        finished <- true
        run "ROLLBACK"

    override _.SupportsSavepoints = true
    override _.Save(name: string) = run $"SAVEPOINT {checkName name}"
    override _.Release(name: string) = run $"RELEASE SAVEPOINT {checkName name}"
    override _.Rollback(name: string) = run $"ROLLBACK TO SAVEPOINT {checkName name}"

    override this.Dispose(disposing: bool) =
        if disposing && not finished then
            finished <- true

            try
                run "ROLLBACK"
            with _ ->
                ()

        base.Dispose disposing

and PGliteCommand internal (connection: PGliteConnection, bridge: IPGliteBridge) =
    inherit DbCommand()
    let parameters = PGliteParameterCollection()
    let mutable text = ""
    let mutable transaction: DbTransaction = null

    let send () =
        let values =
            parameters.Items
            |> Seq.map (fun p -> p.Value)
            |> List.ofSeq

        bridge.Query(Values.positional text, Values.writeParams values, Values.writeTypes values)

    let sync () =
        raise (
            NotSupportedException
                "PGlite runs in the browser's event loop: use the asynchronous command methods (ExecuteReaderAsync, ExecuteNonQueryAsync, ExecuteScalarAsync)"
        )

    override _.CommandText
        with get () = text
        and set v = text <- (if isNull v then "" else v)

    override val CommandTimeout = 30 with get, set
    override val CommandType = CommandType.Text with get, set
    override val DesignTimeVisible = false with get, set
    override val UpdatedRowSource = UpdateRowSource.None with get, set

    override _.DbConnection
        with get () = connection :> DbConnection
        and set _ = invalidOp "a PGlite command stays on its connection"

    override _.DbTransaction
        with get () = transaction
        and set v = transaction <- v

    override _.DbParameterCollection = parameters :> DbParameterCollection
    override _.Cancel() = ()
    override _.CreateDbParameter() = PGliteParameter() :> DbParameter
    override _.Prepare() = ()
    override _.ExecuteNonQuery() = sync ()
    override _.ExecuteScalar() = sync ()
    override _.ExecuteDbDataReader(_: CommandBehavior) : DbDataReader = sync ()

    override _.ExecuteDbDataReaderAsync(_: CommandBehavior, ct: CancellationToken) : Task<DbDataReader> =
        task {
            ct.ThrowIfCancellationRequested()
            let! json = send ()
            return new PGliteDataReader(Values.parseResult json) :> DbDataReader
        }

    override _.ExecuteNonQueryAsync(ct: CancellationToken) : Task<int> =
        task {
            ct.ThrowIfCancellationRequested()
            let! json = send ()
            let r = Values.parseResult json
            return r.RowCount
        }

    override _.ExecuteScalarAsync(ct: CancellationToken) : Task<obj> =
        task {
            ct.ThrowIfCancellationRequested()
            let! json = send ()
            let r = Values.parseResult json

            return
                if r.Rows.Count > 0 && r.Names.Length > 0 then
                    r.Rows[0][0]
                else
                    null
        }

/// A lease on PGlite's one session. PGlite has a single connection, so the
/// provider hands out one lease at a time; disposing it lets the next one in.
and PGliteConnection internal (bridge: IPGliteBridge, release: unit -> unit) =
    inherit DbConnection()
    let mutable state = ConnectionState.Open

    let close () =
        if state <> ConnectionState.Closed then
            state <- ConnectionState.Closed
            release ()

    override _.ConnectionString
        with get () = "PGlite"
        and set (_: string) = invalidOp "a PGlite connection cannot be reconfigured"

    override _.Database = "postgres"
    override _.DataSource = "PGlite"
    override _.ServerVersion = "PGlite"
    override _.State = state
    override _.ChangeDatabase(_: string) = invalidOp "PGlite has one database"
    override _.Open() = ()
    override _.Close() = close ()

    override this.BeginDbTransaction(isolation: IsolationLevel) : DbTransaction =
        Values.throwIfError (bridge.Execute(Values.beginAt isolation))
        new PGliteTransaction(this, bridge, isolation) :> DbTransaction

    override this.CreateDbCommand() = new PGliteCommand(this, bridge) :> DbCommand

    override _.Dispose(disposing: bool) =
        if disposing then close ()
        base.Dispose disposing

/// jsonb documents, rendered as the Postgres provider renders them.
type internal PGliteJson() =
    static member val Instance: IJsonDialect = PGliteJson()

    interface IJsonDialect with
        member _.Member(w, doc, key) =
            let quoted = key.Replace("'", "''")
            w "("
            doc ()
            w $" -> '{quoted}')"

        member _.StoreType = [ "jsonb" ]

        member _.Text(w, doc) =
            w "("
            doc ()
            w " #>> '{}')"

        member _.AnyElement(w, doc, alias, condition) =
            w "EXISTS (SELECT 1 FROM jsonb_array_elements("
            doc ()
            // Quoted like every name: the alias is the query's variable.
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            w (") AS " + q alias + "(\"element\") WHERE ")
            condition ()
            w ")"

        member _.Element(w, alias) =
            let q (n: string) = "\"" + n.Replace("\"", "\"\"") + "\""
            w (q alias + ".\"element\"")

        member _.Contains(w, doc, value) =
            w "("
            doc ()
            w " @> CAST("
            value ()
            w " AS jsonb))"

/// PostgreSQL's DDL (ddl.feature).
module internal PostgresDdl =
    /// The words PostgreSQL reserves, in both its reserved categories (its
    /// documentation's Appendix C): a table or column named by one, such as
    /// `from` or `order`, is printed quoted, as every query quotes it.
    let private reserved =
        set
                [ "all"; "analyse"; "analyze"; "and"; "any"; "array"; "as"; "asc"; "asymmetric"; "authorization"
                  "binary"; "both"; "case"; "cast"; "check"; "collate"; "collation"; "column"; "concurrently"
                  "constraint"; "create"; "cross"; "current_catalog"; "current_date"; "current_role"
                  "current_schema"; "current_time"; "current_timestamp"; "current_user"; "default"; "deferrable"
                  "desc"; "distinct"; "do"; "else"; "end"; "except"; "false"; "fetch"; "for"; "foreign"; "freeze"
                  "from"; "full"; "grant"; "group"; "having"; "ilike"; "in"; "initially"; "inner"; "intersect"
                  "into"; "is"; "isnull"; "join"; "lateral"; "leading"; "left"; "like"; "limit"; "localtime"
                  "localtimestamp"; "natural"; "not"; "notnull"; "null"; "offset"; "on"; "only"; "or"; "order"
                  "outer"; "overlaps"; "placing"; "primary"; "references"; "returning"; "right"; "select"
                  "session_user"; "similar"; "some"; "symmetric"; "system_user"; "table"; "tablesample"; "then"
                  "to"; "trailing"; "true"; "union"; "unique"; "user"; "using"; "variadic"; "verbose"; "when"
                  "where"; "window"; "with" ]

    let ddl =
        { new IDdlDialect with
            member _.Reserved = reserved
            member _.GeneratedKey = " generated always as identity primary key"
            member _.StoreColumn typeName = typeName
            member _.EnumType(typeName, labels) =
                let listed = labels |> List.map (fun l -> "'" + l + "'") |> String.concat ", "
                Some $"create type {typeName} as enum ({listed});"
            member _.ColumnCheck(_, _) = None
            member _.ForeignKeysInTables = false }

/// PostgreSQL's catalogue queries.
module internal PostgresCatalogue =
    /// PostgreSQL's catalogue: information_schema and pg_catalog, in the current
    /// schema (schema-check.feature, scaffolding).
    let catalogue =
        { new ICatalogueDialect with
            member _.Columns tables =
                "SELECT table_name, column_name, CASE WHEN data_type = 'ARRAY' THEN (SELECT format_type(t.typelem, NULL) || '[]' FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE t.typname = udt_name AND n.nspname = udt_schema) ELSE data_type END, " + "is_nullable, column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER', udt_schema, udt_name " + "FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ANY(" + tables + ")"
            member _.CurrentSchema = "SELECT current_schema()"
            member _.EnumLabels names =
                "SELECT n.nspname, t.typname, e.enumlabel FROM pg_type t " + "JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_enum e ON e.enumtypid = t.oid " + "WHERE t.typname = ANY(" + names + ") ORDER BY n.nspname, t.typname, e.enumsortorder"
            member _.UniqueIndexes tables =
                "SELECT t.relname::text, (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord) FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum WHERE k.ord <= i.indnkeyatts), ic.relname::text, " + "EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.conrelid = t.oid AND c.contype IN ('u', 'p')) FROM pg_index i " + "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "JOIN pg_class ic ON ic.oid = i.indexrelid " + "WHERE i.indisunique AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            member _.References tables =
                "SELECT t.relname::text, a.attname::text, r.relname::text, c.conname::text FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid " + "JOIN pg_namespace n ON n.oid = t.relnamespace " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = c.conkey[1] " + "WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1 AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            member _.PrimaryKeys tables =
                "SELECT t.relname::text, string_agg(a.attname::text, ',' ORDER BY k.ord) FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " + "WHERE c.contype = 'p' AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ") GROUP BY t.relname"
            member _.ForeignKeys tables =
                "SELECT t.relname::text, r.relname::text, (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord) FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) " + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum) FROM pg_constraint c " + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid JOIN pg_namespace n ON n.oid = t.relnamespace " + "WHERE c.contype = 'f' AND n.nspname = current_schema() AND t.relname::text = ANY(" + tables + ")"
            member _.TableColumns table =
                "SELECT c.column_name, c.data_type, c.is_nullable = 'YES', " + "c.is_identity = 'YES' OR coalesce(c.column_default LIKE 'nextval(%', false), " + "EXISTS (SELECT 1 FROM information_schema.table_constraints tc " + "JOIN information_schema.key_column_usage k ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema AND k.table_name = tc.table_name " + "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND k.column_name = c.column_name), " + "c.column_default IS NOT NULL OR c.is_identity = 'YES' OR c.is_generated <> 'NEVER' " + "FROM information_schema.columns c WHERE c.table_schema = current_schema() AND c.table_name = " + table + " ORDER BY c.ordinal_position" }

type internal PGliteDialect(maxKeys: int) =
    // PostgreSQL SQL, rendered as the Postgres provider renders it.
    interface ISqlDialect with
        member _.Name = "PostgreSQL"
        member _.QuoteIdentifier s = "\"" + s.Replace("\"", "\"\"") + "\""
        member _.ParameterName i = $"@p{i}"

        // A column is declared, and a JSON scalar cast, as the first name the
        // catalogue gives for the type.
        member this.ColumnType t = List.tryHead ((this :> ISqlDialect).CatalogueTypes t)
        member this.CastType t = List.tryHead ((this :> ISqlDialect).CatalogueTypes t)

        member this.CatalogueTypes t =
            match t with
            // A list of scalars: one array column of its element's type.
            | t when t.IsArray && t <> typeof<byte[]> -> (this :> ISqlDialect).CatalogueTypes(t.GetElementType()) |> List.map (fun n -> n + "[]")
            | t when t = typeof<int16> -> [ "smallint" ]
            | t when t = typeof<int> -> [ "integer" ]
            | t when t = typeof<int64> -> [ "bigint" ]
            | t when t = typeof<string> -> [ "text"; "character varying"; "character"; "citext" ]
            | t when t = typeof<bool> -> [ "boolean" ]
            | t when t = typeof<decimal> -> [ "numeric"; "money" ]
            | t when t = typeof<float> -> [ "double precision" ]
            | t when t = typeof<float32> -> [ "real" ]
            | t when t = typeof<Guid> -> [ "uuid" ]
            | t when t = typeof<DateTime> -> [ "timestamp with time zone"; "timestamp without time zone" ]
            | t when t = typeof<DateTimeOffset> -> [ "timestamp with time zone" ]
            | t when t = typeof<DateOnly> -> [ "date" ]
            | t when t = typeof<TimeOnly> -> [ "time without time zone" ]
            | t when t = typeof<TimeSpan> -> [ "interval" ]
            | t when t = typeof<byte[]> -> [ "bytea" ]
            | _ -> []

        member _.Capabilities =
            { SnapshotReads = true
              ArrayParameters = true
              MaxKeysPerCommand = maxKeys
              Returning = true
              Upserts = true
              RowLocking = true
              WritersTakeTurns = false
              Batching = false
              DefersBegin = false
              Describe = false
              EnumTypes = true
              ArrayColumns = true }

        member _.Begin isolation = Values.beginAt isolation

        member this.StoreCast(typeName, array) =
            let d = this :> ISqlDialect
            Some((typeName |> List.map d.QuoteIdentifier |> String.concat ".") + (if array then "[]" else ""))

        member _.TextCast = Some "text"

        member _.EmptyArray = "'{}'"
        member _.ListHas(w, element, column) =
            element ()
            w " = ANY("
            column ()
            w ")"

        member _.ListIsEmpty(w, column) =
            w "cardinality("
            column ()
            w ") = 0"

        member _.ListValue values = box values
        member _.Refusal _ = None

        member _.NotInList(w, operand, list, cast) =
            operand ()

            match cast with
            | Some c -> w $" <> ALL(CAST({list} AS {c}))"
            | None -> w $" <> ALL({list})"

        member this.ListRows(w, lists, alias) =
            let q = (this :> ISqlDialect).QuoteIdentifier
            w "unnest("

            lists
            |> List.iteri (fun i (list, cast) ->
                if i > 0 then w ", "

                match cast with
                | Some c -> w $"CAST({list} AS {c})"
                | None -> w list)

            w ")"

            match alias with
            | Some(name, columns) ->
                let named = columns |> List.map q |> String.concat ", "
                w $" AS {q name}({named})"
            | None -> ()

        member _.RowsIn(w, columns, lists) =
            w "("

            columns
            |> List.iteri (fun i column ->
                if i > 0 then w ", "
                column ())

            w ") IN (SELECT * FROM unnest("

            lists
            |> List.iteri (fun i (list, cast) ->
                if i > 0 then w ", "

                match cast with
                | Some c -> w $"CAST({list} AS {c})"
                | None -> w list)

            w "))"

        member _.InList(w, operand, list, cast) =
            operand ()

            match cast with
            | Some c -> w $" = ANY(CAST({list} AS {c}))"
            | None -> w $" = ANY({list})"

        member _.Catalogue = PostgresCatalogue.catalogue

        member _.Ddl = PostgresDdl.ddl

        member _.UnboundedLimit = None

        member _.NullSafeComparison negated =
            if negated then "IS DISTINCT FROM" else "IS NOT DISTINCT FROM"

        // A backslash is PostgreSQL's own escape character in LIKE.
        // LIKE, whose escape is a backslash by default: the text's own \\, %
        // and _ escaped, then % where the match is open.
        member _.TextMatch(w, kind, operand, pattern) =
            operand ()
            w " LIKE "

            pattern (fun t ->
                let e = t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")

                match kind with
                | TextMatchKind.StartsWith -> e + "%"
                | TextMatchKind.EndsWith -> "%" + e
                | TextMatchKind.Contains -> "%" + e + "%")
        member _.FunctionName name = name
        member _.Json = Some PGliteJson.Instance
        member _.Exact = None

module internal Classify =
    let private kindOf (state: string) =
        match state with
        | "23505" -> DbFailureKind.UniqueViolation
        | "23503" -> DbFailureKind.ForeignKeyViolation
        | "23514" -> DbFailureKind.CheckViolation
        | "23502" -> DbFailureKind.NotNullViolation
        | "40001" -> DbFailureKind.SerializationFailure
        | "40P01" -> DbFailureKind.Deadlock
        | "55P03" -> DbFailureKind.LockNotAvailable
        | "57014" -> DbFailureKind.Timeout
        | s when s.StartsWith "08" -> DbFailureKind.ConnectionFailure
        | "57P01"
        | "57P02"
        | "57P03" -> DbFailureKind.ConnectionFailure
        | _ -> DbFailureKind.OtherDatabaseError

    let rec classify (ex: exn) : DbFailure option =
        match ex with
        | :? PGliteException as pe when not (isNull pe.SqlState) ->
            Some
                { Kind = kindOf pe.SqlState
                  SqlState = Some pe.SqlState
                  Constraint = Option.ofObj pe.ConstraintName
                  Table = None
                  Columns = []
                  Message = pe.MessageText
                  Exception = ex }
        | :? PGliteException as pe ->
            // PGlite itself failed (no SQLSTATE): the session is unusable.
            Some
                { Kind = DbFailureKind.ConnectionFailure
                  SqlState = None
                  Constraint = None
                  Table = None
                  Columns = []
                  Message = pe.MessageText
                  Exception = ex }
        | :? TimeoutException ->
            Some
                { Kind = DbFailureKind.Timeout
                  SqlState = None
                  Constraint = None
                  Table = None
                  Columns = []
                  Message = ex.Message
                  Exception = ex }
        | :? AggregateException as ae when ae.InnerExceptions.Count = 1 -> classify ae.InnerException
        | _ -> None

type PGliteOptions =
    { /// Keys sent per relationship command (as one array parameter).
      MaxKeysPerCommand: int
      /// How long an operation waits for PGlite's one session.
      SessionWait: TimeSpan }

    static member Default =
        { MaxKeysPerCommand = 5000
          SessionWait = TimeSpan.FromSeconds 30.0 }

/// Plasm provider over one PGlite instance. Operations share its single
/// session, so they take turns: each connection is a lease on the session.
type PGliteProvider(bridge: IPGliteBridge, ?options: PGliteOptions) =
    let options = defaultArg options PGliteOptions.Default
    let dialect = PGliteDialect(options.MaxKeysPerCommand) :> ISqlDialect
    let gate = new SemaphoreSlim(1, 1)

    member _.Bridge = bridge

    interface IProvider with
        member _.Dialect = dialect

        // Its dialect cannot describe (Capabilities.Describe is false), so
        // Plasm never asks.
        member _.Describe(_, _) =
            Task.FromException<DescribeOutcome>(NotSupportedException "PGlite cannot describe a statement without running it")

        member _.OpenConnection ct =
            task {
                let! entered = gate.WaitAsync(options.SessionWait, ct)

                if not entered then
                    raise (
                        TimeoutException
                            $"waited {options.SessionWait.TotalSeconds} s for PGlite's single session; another operation (or an open sandbox) holds it"
                    )

                let mutable released = 0

                let release () =
                    if Interlocked.Exchange(&released, 1) = 0 then
                        gate.Release() |> ignore

                return new PGliteConnection(bridge, release) :> DbConnection
            }

        member _.Classify ex = Classify.classify ex

        member _.BeginSnapshot(conn, _) =
            task {
                let tx = conn.BeginTransaction(IsolationLevel.RepeatableRead)
                return Some tx
            }

        member _.BeginTransaction(conn, isolation, _) = Task.FromResult(conn.BeginTransaction isolation)

[<RequireQualifiedAccess>]
module PGlite =
    /// A provider for a PGlite bridge. Sets the session to report timestamps
    /// in UTC and intervals in ISO 8601, which the value mapping reads.
    let connectWith (options: PGliteOptions) (bridge: IPGliteBridge) : Task<PGliteProvider> =
        task {
            Values.throwIfError (bridge.Execute "SET TimeZone TO 'UTC'")
            Values.throwIfError (bridge.Execute "SET IntervalStyle TO 'iso_8601'")
            return PGliteProvider(bridge, options)
        }

    let connect (bridge: IPGliteBridge) = connectWith PGliteOptions.Default bridge
