namespace Plasm

open System
open System.Data
open System.Text

/// What a provider can execute faithfully. The planner rejects requests that
/// exceed these rather than substituting weaker semantics.
type ProviderCapabilities =
    { /// Multi-command reads inside one snapshot (e.g. REPEATABLE READ).
      SnapshotReads: bool
      /// Key lists can be sent as one array parameter.
      ArrayParameters: bool
      /// Upper bound on keys sent in one command loading an owned or linked list.
      MaxKeysPerCommand: int
      /// INSERT/UPDATE ... RETURNING (single and bulk writes read back rows).
      Returning: bool
      /// Inserts that resolve conflicts (ON CONFLICT ...): upserts.
      Upserts: bool
      /// SELECT ... FOR UPDATE (deleting an owner locks what it owns first).
      RowLocking: bool
      /// Several statements sent in one round trip (a DbBatch): a write's
      /// statements, and one level of loads (round-trips.feature).
      Batching: bool
      /// BEGIN and SAVEPOINT wait for whatever the connection sends next and
      /// travel with it (Npgsql), so OnCommand reports them in that round
      /// trip; false when each is a round trip of its own
      /// (transaction-events.feature).
      DefersBegin: bool
      /// A statement can be described without running it (PostgreSQL's
      /// Parse and Describe): Repo.Start has the database describe every
      /// statement a module defines (statement-check.feature).
      Describe: bool
      /// A field can be stored as a database enum type (Scalar.asPgEnum);
      /// without, a model that stores one is refused when the repository is
      /// made.
      EnumTypes: bool
      /// A list of scalars can be stored as one array column
      /// (array-columns.feature); without, a model that stores one is refused
      /// when the repository is made.
      ArrayColumns: bool }

/// How a provider stores and reads JSON documents (json-columns.feature).
/// Each member writes SQL through `write`; `doc`, `value` and `condition`
/// write their own sub-expressions.
type IJsonDialect =
    /// A document's member: `(doc -> 'key')`.
    abstract Member: write: (string -> unit) * doc: (unit -> unit) * key: string -> unit
    /// A document holding a scalar, as text: `(doc #>> '{}')`.
    abstract Text: write: (string -> unit) * doc: (unit -> unit) -> unit
    /// Whether an element of an array document, named `alias`, meets `condition`.
    abstract AnyElement: write: (string -> unit) * doc: (unit -> unit) * alias: string * condition: (unit -> unit) -> unit
    /// The element named `alias`, inside AnyElement's condition.
    abstract Element: write: (string -> unit) * alias: string -> unit
    /// Whether an array document contains every element of `value` (a document
    /// sent as text).
    abstract Contains: write: (string -> unit) * doc: (unit -> unit) * value: (unit -> unit) -> unit

/// Dialect-specific rendering hooks.
type ISqlDialect =
    abstract Name: string
    abstract QuoteIdentifier: string -> string
    abstract ParameterName: int -> string
    abstract Capabilities: ProviderCapabilities
    /// The type a column holding values of this provider type is declared as
    /// (what Ddl prints); None when the dialect has none.
    abstract ColumnType: Type -> string option
    /// The type names the database's catalogue may give such a column (what
    /// the schema check accepts, and scaffolding maps back); empty when not
    /// checked.
    abstract CatalogueTypes: Type -> string list
    /// The type a scalar read out of a JSON document as text is cast to, to be
    /// compared and read as this provider type; None when the dialect has none.
    abstract CastType: Type -> string option
    /// The null-safe comparison operator: equality treating NULLs as equal
    /// (`IS NOT DISTINCT FROM`), or its negation when `negated`.
    abstract NullSafeComparison: negated: bool -> string
    /// What follows a LIKE pattern so that a backslash escapes `%` and `_`:
    /// `" ESCAPE '\\'"`, or "" where that is the dialect's default (PostgreSQL).
    abstract LikeEscape: string
    /// The dialect's name for a function the core uses (char_length, btrim,
    /// lower, upper, COALESCE, ...); return the name unchanged if it is the same.
    abstract FunctionName: string -> string
    /// JSON documents ([<Json>] fields); None when the provider has none, and
    /// then a repository refuses a model with a [<Json>] field.
    abstract Json: IJsonDialect option
    /// The BEGIN the provider sends for a transaction at this isolation level,
    /// as the database logs it; OnCommand reports it (transaction-events.feature).
    abstract Begin: IsolationLevel -> string

module internal Sql =

    /// A parameter whose value is bound at execution from a prepared query's
    /// arguments. Rendered like any parameter; resolved just before sending.
    [<Sealed; AllowNullLiteral>]
    type Bound(resolve: obj[] -> obj, label: string) =
        member _.Resolve(args: obj[]) = resolve args
        member _.Label = label
        override _.ToString() = $"arg:{label}"

    type Expr =
        | Column of alias: string * name: string
        /// A provider value (already converted from the domain type).
        | Param of value: obj
        /// A typed provider array, used for key and membership lists.
        | ArrayParam of values: Array
        | Literal of string
        | Binary of op: string * Expr * Expr
        | Not of Expr
        /// `(a) IS NOT TRUE`: F#'s `not`, for which an unknown (NULL) operand,
        /// like a false one, makes the negation true.
        | IsNotTrue of Expr
        | IsNull of Expr
        | IsNotNull of Expr
        | NotDistinctFrom of Expr * Expr
        | DistinctFrom of Expr * Expr
        /// Membership in a list of values; `cast` names the database type the
        /// values are cast to (an enum type), when they are not sent as it.
        | AnyOf of Expr * values: Array * cast: string list option
        /// `CAST(e AS "type")` or, for an array, `CAST(e AS "type"[])`: a value
        /// sent as text, given the database type it is compared with or stored as.
        | Cast of Expr * typeName: string list * array: bool
        /// `CAST(e AS text)`: a column read as text (an enum type's label).
        | AsText of Expr
        | Like of Expr * pattern: Expr
        | Func of string * Expr list
        | Exists of Select
        | RowNumber of partitionBy: Expr * orderBy: OrderItem list
        /// An aggregate function: `NAME(arg)`, `NAME(DISTINCT arg)` or `COUNT(*)` (arg None).
        | Aggregate of name: string * distinct: bool * arg: Expr option
        /// A window function: name(args) OVER (PARTITION BY ... ORDER BY ... frame).
        | Window of name: string * args: Expr list * partition: Expr list * order: OrderItem list * frame: string option
        /// An aggregate with an order of its own and a filter:
        /// name(args ORDER BY ...) FILTER (WHERE filter); COUNT(*) with no args.
        | AggregateBy of name: string * args: Expr list * order: OrderItem list * filter: Expr option
        /// Literal SQL text interleaved with translated expressions (Raw.fragment).
        | Fragment of parts: Choice<string, Expr> list
        /// CASE WHEN condition THEN value ... ELSE otherwise END.
        | Case of branches: (Expr * Expr) list * otherwise: Expr
        /// A member of a document (json-columns.feature).
        | JsonMember of document: Expr * key: string
        /// A document holding a scalar, as text, cast to a provider type's column type.
        | JsonText of document: Expr * castTo: Type option
        /// Whether an element of an array document meets a condition over JsonElement alias.
        | JsonAny of document: Expr * alias: string * condition: Expr
        | JsonElement of alias: string
        /// Whether an array document contains every element of a document parameter.
        | JsonContains of document: Expr * value: Expr

    and OrderItem =
        { Expr: Expr
          Desc: bool
          NullsFirst: bool option }

    and Source =
        | Table of name: string * alias: string
        | Subquery of Select * alias: string
        /// Two selects combined (UNION, UNION ALL, INTERSECT, EXCEPT) and read
        /// as one table named `alias` (set-operations.feature).
        | Compound of kind: string * left: Select * right: Select * alias: string
        /// A recursive query (recursive-queries.feature): WITH RECURSIVE `name`
        /// AS (start UNION ALL step), with CYCLE on `key`, read as one table
        /// named `alias`, without the rows that closed a cycle.
        | Recursive of name: string * start: Select * step: Select * key: string list * alias: string

    and Join =
        { Left: bool
          Table: string
          Alias: string
          On: Expr
          /// A query joined in place of the table (set-operations.feature).
          Sub: Select option }

    and Select =
        { Columns: (Expr * string) list
          From: Source
          Joins: Join list
          Where: Expr list
          GroupBy: Expr list
          Having: Expr list
          OrderBy: OrderItem list
          /// Row count expressions (a parameter, possibly bound per execution).
          Limit: Expr option
          Offset: Expr option
          /// FOR UPDATE and its kin (row-locks.feature).
          Lock: Lock option
          Distinct: bool }

    /// A row lock: FOR <Strength> [OF <alias>] [<Wait>].
    and Lock =
        { /// UPDATE, NO KEY UPDATE, SHARE or KEY SHARE.
          Strength: string
          /// The alias whose rows are locked, when not every table's.
          Of: string option
          /// NOWAIT or SKIP LOCKED; None waits.
          Wait: string option }

    let select source =
        { Columns = []
          From = source
          Joins = []
          Where = []
          GroupBy = []
          Having = []
          OrderBy = []
          Limit = None
          Offset = None
          Lock = None
          Distinct = false }

    /// A fixed row count, sent as a 64-bit parameter.
    let count (n: int) = Param(box (int64 n))

    type Insert =
        { Table: string
          Columns: string list
          Values: Expr list
          Returning: (Expr * string) list }

    type Update =
        { Table: string
          Set: (string * Expr) list
          Where: Expr list
          Returning: (Expr * string) list }

    type Delete =
        { Table: string
          Where: Expr list
          Returning: (Expr * string) list }

    type Conflict =
        | DoNothing of target: string list
        | DoUpdate of target: string list * set: (string * Expr) list

    /// A multi-row insert, optionally an upsert.
    type InsertMany =
        { Table: string
          Columns: string list
          Rows: Expr list list
          OnConflict: Conflict option
          Returning: (Expr * string) list }

    type Command =
        { Sql: string
          Parameters: obj list }

    let rec conj (es: Expr list) =
        match es with
        | [] -> Literal "TRUE"
        | [ e ] -> e
        | e :: rest -> Binary("AND", e, conj rest)

    /// Any of the conditions; FALSE when there are none.
    let rec disj (es: Expr list) =
        match es with
        | [] -> Literal "FALSE"
        | [ e ] -> e
        | e :: rest -> Binary("OR", e, disj rest)

    /// A database type name, quoted by the dialect: `"sales"."order_status"`,
    /// with `[]` for its array type.
    let typeName (d: ISqlDialect) (parts: string list) (array: bool) =
        (parts |> List.map d.QuoteIdentifier |> String.concat ".") + (if array then "[]" else "")

    /// The database type a field's values are stored as, when it is not the
    /// provider type's own (Scalar.asPgEnum).
    let storeType (f: FieldMap) = f.Mapping |> Option.bind (fun m -> m.StoreType)

    /// A field value as a parameter, cast to the field's database type if it has one.
    let fieldValue (f: FieldMap) (value: obj) =
        let p = Param(f.ToDb value)

        match storeType f with
        | Some t -> Cast(p, t, false)
        | None -> p

    /// A provider value of a domain scalar as a parameter, cast likewise.
    let fieldScalar (f: FieldMap) (value: obj) =
        let p = Param(f.ScalarToDb value)

        match storeType f with
        | Some t -> Cast(p, t, false)
        | None -> p

    /// A field's column as it is read: as text when it has a database type of
    /// its own, whose values the driver may not know how to read.
    let readColumn (f: FieldMap) (column: Expr) =
        match storeType f with
        | Some _ -> AsText column
        | None -> column

    /// The database type one column's values are stored as, if not the provider type's.
    let partStoreType (p: ColumnPart) = p.Mapping |> Option.bind (fun m -> m.StoreType)

    /// One column of a field as it is read (as text when it has a type of its own).
    let readPart (p: ColumnPart) (column: Expr) =
        match partStoreType p with
        | Some _ -> AsText column
        | None -> column

    /// A provider value for one column, as a parameter cast to its type.
    let partValue (p: ColumnPart) (providerValue: obj) =
        let x = Param providerValue

        match partStoreType p with
        | Some t -> Cast(x, t, false)
        | None -> x

    /// Each column a field is stored in, with the parameter its value stores
    /// there: one column, or a union's several (union-fields.feature).
    let fieldColumns (f: FieldMap) (value: obj) : (string * Expr) list =
        List.map2 (fun (p: ColumnPart) v -> p.Name, partValue p v) f.Parts (f.ToColumns value)

    /// Every column of some fields, each read as `readPart` reads it.
    let readFields (table: string) (fields: FieldMap seq) =
        fields |> Seq.collect (fun f -> f.Parts |> List.map (fun p -> readPart p (Column(table, p.Name)), p.Name)) |> List.ofSeq

    /// RETURNING every column of a table's fields.
    let returningFields (table: string) (fields: FieldMap seq) = readFields table fields

    let private comparison (op: string) =
        match op with
        | "="
        | "<>"
        | "!="
        | "<"
        | "<="
        | ">"
        | ">=" -> true
        | _ -> false

    let private arithmetic (op: string) =
        match op with
        | "+"
        | "-"
        | "*"
        | "/"
        | "%"
        | "||" -> true
        | _ -> false

    /// A value written into a statement as NULL rather than sent: an
    /// assignment's NULL carries no user data.
    let private isNullValue (e: Expr) =
        match e with
        | Param v
        | Cast(Param v, _, _) -> isNull v || v :? DBNull
        | _ -> false

    type private Renderer(d: ISqlDialect) =
        let sb = StringBuilder()
        let ps = ResizeArray<obj>()
        let q (s: string) = d.QuoteIdentifier s
        // Each clause starts a line, a derived table indented under its
        // parenthesis; a subquery inside a condition stays on its line
        // (readable-sql.feature).
        let mutable indent = ""
        let mutable inline' = 0

        /// Starts a clause: on a line of its own, or after a space inside a condition.
        let clause (keyword: string) =
            if inline' > 0 then sb.Append(' ') |> ignore else sb.Append('\n').Append(indent) |> ignore
            sb.Append(keyword) |> ignore

        let json () =
            match d.Json with
            | Some j -> j
            | None -> failwith $"{d.Name} has no JSON documents"

        member _.Param(v: obj) =
            // One prepared argument written twice is one parameter, so its
            // type is the column's wherever it is read.
            match v with
            | :? Bound -> 
                match Seq.tryFindIndex (fun (x: obj) -> obj.ReferenceEquals(x, v)) ps with
                | Some i -> d.ParameterName i
                | None ->
                    ps.Add v
                    d.ParameterName(ps.Count - 1)
            | _ ->
                ps.Add v
                d.ParameterName(ps.Count - 1)

        /// An expression where nothing around it binds tighter: a select
        /// column, a condition, an argument. Parentheses are written only where
        /// precedence needs them (readable-sql.feature).
        member r.Expr(e: Expr) : unit =
            let w (s: string) = sb.Append(s) |> ignore

            match e with
            | Column(a, n) ->
                w (q a)
                w "."
                w (q n)
            | Param v -> w (r.Param v)
            | ArrayParam v -> w (r.Param v)
            | Literal l -> w l
            | Binary(("AND" | "OR") as op, _, _) ->
                let rec operands (x: Expr) =
                    match x with
                    | Binary(o, a, b) when o = op -> operands a @ operands b
                    | Literal "TRUE" when op = "AND" -> []
                    | Literal "FALSE" when op = "OR" -> []
                    | x -> [ x ]

                match operands e with
                | [] -> w (if op = "AND" then "TRUE" else "FALSE")
                | xs ->
                    xs
                    |> List.iteri (fun i x ->
                        if i > 0 then w $" {op} "

                        match x with
                        // An OR inside an AND needs them; an AND inside an OR is
                        // bracketed to be read at a glance.
                        | Binary(("AND" | "OR"), _, _) -> r.Wrapped x
                        | Binary(o, _, _) when not (comparison o) && not (arithmetic o) -> r.Wrapped x
                        | x -> r.Expr x)
            | Binary(op, a, b) when comparison op ->
                r.Operand(a, true)
                w $" {op} "
                r.Operand(b, true)
            | Binary(op, a, b) ->
                r.Operand(a, false)
                w $" {op} "
                r.Operand(b, false)
            | Not a ->
                w "NOT "
                r.Operand(a, false)
            | IsNotTrue a ->
                r.Operand(a, false)
                w " IS NOT TRUE"
            | IsNull a ->
                r.Operand(a, false)
                w " IS NULL"
            | IsNotNull a ->
                r.Operand(a, false)
                w " IS NOT NULL"
            | NotDistinctFrom(a, b) ->
                r.Operand(a, false)
                w $" {d.NullSafeComparison false} "
                r.Operand(b, false)
            | DistinctFrom(a, b) ->
                r.Operand(a, false)
                w $" {d.NullSafeComparison true} "
                r.Operand(b, false)
            | AnyOf(a, values, cast) ->
                let typed (p: string) array =
                    match cast with
                    | Some t -> $"CAST({p} AS {typeName d t array})"
                    | None -> p

                if values.Length = 0 then
                    w "FALSE"
                elif d.Capabilities.ArrayParameters then
                    r.Operand(a, true)
                    w " = ANY("
                    w (typed (r.Param values) true)
                    w ")"
                else
                    r.Operand(a, true)
                    w " IN ("

                    values
                    |> Seq.cast<obj>
                    |> Seq.iteri (fun i v ->
                        if i > 0 then w ", "
                        w (typed (r.Param v) false))

                    w ")"
            | Cast(e, t, array) ->
                w "CAST("
                r.Expr e
                w $" AS {typeName d t array})"
            | AsText e ->
                w "CAST("
                r.Expr e
                w " AS text)"
            | Like(a, p) ->
                r.Operand(a, true)
                w " LIKE "
                r.Operand(p, true)
                w d.LikeEscape
            | Func(name, args) ->
                w (d.FunctionName name)
                w "("

                args
                |> List.iteri (fun i a ->
                    if i > 0 then w ", "
                    r.Expr a)

                w ")"
            | Exists s ->
                w "EXISTS ("
                inline' <- inline' + 1
                r.Select s
                inline' <- inline' - 1
                w ")"
            | JsonMember(doc, key) -> json().Member(w, (fun () -> r.Expr doc), key)
            | JsonText(doc, castTo) ->
                match castTo with
                | Some t when t <> typeof<string> ->
                    w "CAST("
                    json().Text(w, (fun () -> r.Expr doc))

                    match d.CastType t with
                    | Some name -> w $" AS {name})"
                    | None -> failwith $"{d.Name} has no type to cast {t.Name} to"
                | _ -> json().Text(w, (fun () -> r.Expr doc))
            | JsonAny(doc, alias, condition) -> json().AnyElement(w, (fun () -> r.Expr doc), alias, (fun () -> r.Expr condition))
            | JsonElement alias -> json().Element(w, alias)
            | JsonContains(doc, value) -> json().Contains(w, (fun () -> r.Expr doc), (fun () -> r.Expr value))
            | RowNumber(p, order) ->
                w "ROW_NUMBER() OVER (PARTITION BY "
                r.Expr p

                if not order.IsEmpty then
                    w " ORDER BY "
                    r.Order order

                w ")"
            | Aggregate(name, distinct, arg) ->
                w name
                w "("

                match arg with
                | None -> w "*"
                | Some a ->
                    if distinct then w "DISTINCT "
                    r.Expr a

                w ")"
            | Window(name, args, partition, order, frame) ->
                w name
                w "("

                args
                |> List.iteri (fun i a ->
                    if i > 0 then w ", "
                    r.Expr a)

                w ") OVER ("
                let mutable gap = false

                if not partition.IsEmpty then
                    w "PARTITION BY "

                    partition
                    |> List.iteri (fun i p ->
                        if i > 0 then w ", "
                        r.Expr p)

                    gap <- true

                if not order.IsEmpty then
                    if gap then w " "
                    w "ORDER BY "
                    r.Order order
                    gap <- true

                match frame with
                | Some f ->
                    if gap then w " "
                    w f
                | None -> ()

                w ")"
            | AggregateBy(name, args, order, filter) ->
                w name
                w "("

                if args.IsEmpty then
                    w "*"
                else
                    args
                    |> List.iteri (fun i a ->
                        if i > 0 then w ", "
                        r.Expr a)

                if not order.IsEmpty then
                    w " ORDER BY "
                    r.Order order

                w ")"

                match filter with
                | Some f ->
                    w " FILTER (WHERE "
                    r.Expr f
                    w ")"
                | None -> ()
            | Case(branches, otherwise) ->
                // A CASE in another's ELSE is one CASE (if, elif, else).
                let rec flat bs o =
                    match o with
                    | Case(more, o2) -> flat (bs @ more) o2
                    | o -> bs, o

                let branches, otherwise = flat branches otherwise
                w "CASE"

                for c, v in branches do
                    w " WHEN "
                    r.Expr c
                    w " THEN "
                    r.Expr v

                w " ELSE "
                r.Expr otherwise
                w " END"
            | Fragment parts ->
                w "("

                for p in parts do
                    match p with
                    | Choice1Of2 text -> w text
                    | Choice2Of2 x -> r.Expr x

                w ")"

        /// An expression in parentheses.
        member r.Wrapped(e: Expr) =
            sb.Append("(") |> ignore
            r.Expr e
            sb.Append(")") |> ignore

        /// An operand of an operator: bare when it binds tighter (a column, a
        /// call, a CASE; an arithmetic operation inside a comparison), else in
        /// parentheses.
        member r.Operand(e: Expr, ofComparison: bool) =
            match e with
            | Column _
            | Param _
            | ArrayParam _
            | Literal _
            | Func _
            | Cast _
            | AsText _
            | Aggregate _
            | RowNumber _
            | Case _
            | Fragment _
            | Exists _ -> r.Expr e
            | Binary(op, _, _) when ofComparison && arithmetic op -> r.Expr e
            | _ -> r.Wrapped e

        member r.Order(items: OrderItem list) =
            items
            |> List.iteri (fun i o ->
                if i > 0 then sb.Append(", ") |> ignore
                r.Expr o.Expr
                if o.Desc then sb.Append(" DESC") |> ignore

                match o.NullsFirst with
                | Some true -> sb.Append(" NULLS FIRST") |> ignore
                | Some false -> sb.Append(" NULLS LAST") |> ignore
                | None -> ())

        member r.Select(s: Select) =
            let w (x: string) = sb.Append(x) |> ignore
            w (if s.Distinct then "SELECT DISTINCT " else "SELECT ")

            s.Columns
            |> List.iteri (fun i (e, alias) ->
                if i > 0 then w ", "
                r.Expr e

                match e with
                | Column(_, n) when n = alias -> ()
                | _ ->
                    w " AS "
                    w (q alias))

            if s.Columns.IsEmpty then w "1"
            clause "FROM "

            match s.From with
            | Table(n, a) ->
                w (q n)

                if a <> n then
                    w " AS "
                    w (q a)
            | Subquery(inner, a) when inline' > 0 ->
                w "("
                r.Select inner
                w ") AS "
                w (q a)
            | Subquery(inner, a) ->
                let outer = indent
                indent <- outer + "    "
                w "("
                w "\n"
                w indent
                r.Select inner
                indent <- outer
                w "\n"
                w indent
                w ") AS "
                w (q a)
            | Recursive(name, start, step, key, a) ->
                let outer = indent
                indent <- outer + "    "
                w "("
                w "\n"
                w indent
                w ("WITH RECURSIVE " + q name + " AS (")
                indent <- outer + "        "
                w "\n"
                w indent
                r.Select start
                w "\n"
                w indent
                w "UNION ALL"
                w "\n"
                w indent
                r.Select step
                indent <- outer + "    "
                w "\n"
                w indent
                w ") CYCLE "
                w (key |> List.map q |> String.concat ", ")
                w (" SET " + q "plasm_cycle" + " USING " + q "plasm_path")
                w "\n"
                w indent
                w ("SELECT * FROM " + q name + " WHERE NOT " + q "plasm_cycle")
                indent <- outer
                w "\n"
                w indent
                w ") AS "
                w (q a)
            | Compound(kind, left, right, a) ->
                let outer = indent
                indent <- outer + "    "
                w "("
                w "\n"
                w indent
                r.Select left
                w "\n"
                w indent
                w kind
                w "\n"
                w indent
                r.Select right
                indent <- outer
                w "\n"
                w indent
                w ") AS "
                w (q a)

            for j in s.Joins do
                clause (if j.Left then "LEFT JOIN " else "INNER JOIN ")

                match j.Sub with
                | Some sub ->
                    let outer = indent
                    indent <- outer + "    "
                    w "("
                    w "\n"
                    w indent
                    r.Select sub
                    indent <- outer
                    w "\n"
                    w indent
                    w ")"
                | None -> w (q j.Table)

                if j.Alias <> j.Table || j.Sub.IsSome then
                    w " AS "
                    w (q j.Alias)

                w " ON "
                r.Expr j.On

            if not s.Where.IsEmpty then
                clause "WHERE "
                r.Expr(conj s.Where)

            if not s.GroupBy.IsEmpty then
                clause "GROUP BY "

                s.GroupBy
                |> List.iteri (fun i g ->
                    if i > 0 then w ", "
                    r.Expr g)

            if not s.Having.IsEmpty then
                clause "HAVING "
                r.Expr(conj s.Having)

            if not s.OrderBy.IsEmpty then
                clause "ORDER BY "
                r.Order s.OrderBy

            // A page is one line: LIMIT and OFFSET together.
            match s.Limit with
            | Some l ->
                clause "LIMIT "
                r.Expr l
            | None -> ()

            match s.Offset with
            | Some o ->
                if s.Limit.IsSome then w " OFFSET " else clause "OFFSET "
                r.Expr o
            | None -> ()

            match s.Lock with
            | Some l ->
                clause $"FOR {l.Strength}"

                match l.Of with
                | Some alias -> w $" OF {q alias}"
                | None -> ()

                match l.Wait with
                | Some wait -> w $" {wait}"
                | None -> ()
            | None -> ()

        member r.Insert(i: Insert) =
            let w (x: string) = sb.Append(x) |> ignore
            w $"INSERT INTO {q i.Table}"

            if i.Columns.IsEmpty then
                clause "DEFAULT VALUES"
            else
                w " ("
                w (i.Columns |> List.map q |> String.concat ", ")
                w ")"
                clause "VALUES ("

                i.Values
                |> List.iteri (fun n e ->
                    if n > 0 then w ", "
                    if isNullValue e then w "NULL" else r.Expr e)

                w ")"

            r.Returning i.Returning

        member r.InsertMany(i: InsertMany) =
            let w (x: string) = sb.Append(x) |> ignore
            w $"INSERT INTO {q i.Table} ("
            w (i.Columns |> List.map q |> String.concat ", ")
            w ")"
            clause "VALUES "

            i.Rows
            |> List.iteri (fun n row ->
                if n > 0 then w ", "
                w "("

                row
                |> List.iteri (fun m e ->
                    if m > 0 then w ", "
                    r.Expr e)

                w ")")

            match i.OnConflict with
            | None -> ()
            | Some(DoNothing target) ->
                clause "ON CONFLICT ("
                w (target |> List.map q |> String.concat ", ")
                w ") DO NOTHING"
            | Some(DoUpdate(target, set)) ->
                clause "ON CONFLICT ("
                w (target |> List.map q |> String.concat ", ")
                w ") DO UPDATE SET "

                set
                |> List.iteri (fun n (c, e) ->
                    if n > 0 then w ", "
                    w (q c)
                    w " = "
                    r.Expr e)

            r.Returning i.Returning

        member r.Update(u: Update) =
            let w (x: string) = sb.Append(x) |> ignore
            w $"UPDATE {q u.Table}"
            clause "SET "

            u.Set
            |> List.iteri (fun n (c, e) ->
                if n > 0 then w ", "
                w (q c)
                w " = "
                if isNullValue e then w "NULL" else r.Expr e)

            if not u.Where.IsEmpty then
                clause "WHERE "
                r.Expr(conj u.Where)

            r.Returning u.Returning

        member r.Delete(x: Delete) =
            let w (s: string) = sb.Append(s) |> ignore
            w $"DELETE FROM {q x.Table}"

            if not x.Where.IsEmpty then
                clause "WHERE "
                r.Expr(conj x.Where)

            r.Returning x.Returning

        member r.Returning(cols: (Expr * string) list) =
            if not cols.IsEmpty then
                clause "RETURNING "

                cols
                |> List.iteri (fun i (e, name) ->
                    if i > 0 then sb.Append(", ") |> ignore

                    match e with
                    | Column(_, n) when n = name -> sb.Append(q n) |> ignore
                    | _ ->
                        r.Expr e
                        sb.Append(" AS ").Append(q name) |> ignore)

        member _.Result() =
            { Sql = sb.ToString()
              Parameters = List.ofSeq ps }

    let renderSelect d s =
        let r = Renderer(d)
        r.Select s
        r.Result()

    let renderInsert d i =
        let r = Renderer(d)
        r.Insert i
        r.Result()

    let renderInsertMany d i =
        let r = Renderer(d)
        r.InsertMany i
        r.Result()

    let renderUpdate d u =
        let r = Renderer(d)
        r.Update u
        r.Result()

    let renderDelete d x =
        let r = Renderer(d)
        r.Delete x
        r.Result()

    /// Renders an expression in isolation; used to compare load customisations.
    let renderExpr d e =
        let r = Renderer(d)
        r.Expr e
        r.Result()
