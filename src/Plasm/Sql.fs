namespace Plasm

open System
open System.Text

/// What a provider can execute faithfully. The planner rejects requests that
/// exceed these rather than substituting weaker semantics.
type ProviderCapabilities =
    { /// Per-parent limits via window functions.
      PerParentLimit: bool
      /// Multi-command reads inside one snapshot (e.g. REPEATABLE READ).
      SnapshotReads: bool
      /// Key lists can be sent as one array parameter.
      ArrayParameters: bool
      /// Upper bound on keys sent in one relationship command.
      MaxKeysPerCommand: int
      /// INSERT/UPDATE ... RETURNING (single and bulk writes read back rows).
      Returning: bool
      /// Inserts that resolve conflicts (ON CONFLICT ...): upserts.
      Upserts: bool
      /// SELECT ... FOR UPDATE (replacing children locks them first).
      RowLocking: bool }

/// Dialect-specific rendering hooks.
type ISqlDialect =
    abstract Name: string
    abstract QuoteIdentifier: string -> string
    abstract ParameterName: int -> string
    abstract Capabilities: ProviderCapabilities
    /// Catalogue type names (information_schema data_type) a column may have
    /// to hold values of this provider type; empty when not checked.
    abstract ColumnTypes: Type -> string list
    /// The null-safe comparison operator: equality treating NULLs as equal
    /// (`IS NOT DISTINCT FROM`), or its negation when `negated`.
    abstract NullSafeComparison: negated: bool -> string
    /// The dialect's name for a function the core uses (char_length, btrim,
    /// lower, upper, COALESCE, ...); return the name unchanged if it is the same.
    abstract FunctionName: string -> string

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
        /// Literal SQL text interleaved with translated expressions (Raw.fragment).
        | Fragment of parts: Choice<string, Expr> list

    and OrderItem =
        { Expr: Expr
          Desc: bool
          NullsFirst: bool option }

    and Source =
        | Table of name: string * alias: string
        | Subquery of Select * alias: string

    and Join =
        { Left: bool
          Table: string
          Alias: string
          On: Expr }

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
          ForUpdate: bool }

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
          ForUpdate = false }

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

    /// RETURNING every field of a table, each read as `readColumn` reads it.
    let returningFields (table: string) (fields: FieldMap seq) =
        fields |> Seq.map (fun f -> readColumn f (Column(table, f.Column)), f.Column) |> List.ofSeq

    type private Renderer(d: ISqlDialect) =
        let sb = StringBuilder()
        let ps = ResizeArray<obj>()
        let q (s: string) = d.QuoteIdentifier s

        member _.Param(v: obj) =
            ps.Add v
            d.ParameterName(ps.Count - 1)

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
            | Binary(op, a, b) ->
                w "("
                r.Expr a
                w $" {op} "
                r.Expr b
                w ")"
            | Not a ->
                w "(NOT "
                r.Expr a
                w ")"
            | IsNotTrue a ->
                w "("
                r.Expr a
                w " IS NOT TRUE)"
            | IsNull a ->
                w "("
                r.Expr a
                w " IS NULL)"
            | IsNotNull a ->
                w "("
                r.Expr a
                w " IS NOT NULL)"
            | NotDistinctFrom(a, b) ->
                w "("
                r.Expr a
                w $" {d.NullSafeComparison false} "
                r.Expr b
                w ")"
            | DistinctFrom(a, b) ->
                w "("
                r.Expr a
                w $" {d.NullSafeComparison true} "
                r.Expr b
                w ")"
            | AnyOf(a, values, cast) ->
                let typed (p: string) array =
                    match cast with
                    | Some t -> $"CAST({p} AS {typeName d t array})"
                    | None -> p

                if values.Length = 0 then
                    w "FALSE"
                elif d.Capabilities.ArrayParameters then
                    w "("
                    r.Expr a
                    w " = ANY("
                    w (typed (r.Param values) true)
                    w "))"
                else
                    w "("
                    r.Expr a
                    w " IN ("

                    values
                    |> Seq.cast<obj>
                    |> Seq.iteri (fun i v ->
                        if i > 0 then w ", "
                        w (typed (r.Param v) false))

                    w "))"
            | Cast(e, t, array) ->
                w "CAST("
                r.Expr e
                w $" AS {typeName d t array})"
            | AsText e ->
                w "CAST("
                r.Expr e
                w " AS text)"
            | Like(a, p) ->
                w "("
                r.Expr a
                w " LIKE "
                r.Expr p
                w " ESCAPE '\\')"
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
                r.Select s
                w ")"
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
            | Fragment parts ->
                w "("

                for p in parts do
                    match p with
                    | Choice1Of2 text -> w text
                    | Choice2Of2 x -> r.Expr x

                w ")"

        member r.Order(items: OrderItem list) =
            items
            |> List.iteri (fun i o ->
                if i > 0 then sb.Append(", ") |> ignore
                r.Expr o.Expr
                sb.Append(if o.Desc then " DESC" else " ASC") |> ignore

                match o.NullsFirst with
                | Some true -> sb.Append(" NULLS FIRST") |> ignore
                | Some false -> sb.Append(" NULLS LAST") |> ignore
                | None -> ())

        member r.Select(s: Select) =
            let w (x: string) = sb.Append(x) |> ignore
            w "SELECT "

            s.Columns
            |> List.iteri (fun i (e, alias) ->
                if i > 0 then w ", "
                r.Expr e
                w " AS "
                w (q alias))

            if s.Columns.IsEmpty then w "1"
            w " FROM "

            match s.From with
            | Table(n, a) ->
                w (q n)
                w " AS "
                w (q a)
            | Subquery(inner, a) ->
                w "("
                r.Select inner
                w ") AS "
                w (q a)

            for j in s.Joins do
                w (if j.Left then " LEFT JOIN " else " INNER JOIN ")
                w (q j.Table)
                w " AS "
                w (q j.Alias)
                w " ON "
                r.Expr j.On

            if not s.Where.IsEmpty then
                w " WHERE "
                r.Expr(conj s.Where)

            if not s.GroupBy.IsEmpty then
                w " GROUP BY "

                s.GroupBy
                |> List.iteri (fun i g ->
                    if i > 0 then w ", "
                    r.Expr g)

            if not s.Having.IsEmpty then
                w " HAVING "
                r.Expr(conj s.Having)

            if not s.OrderBy.IsEmpty then
                w " ORDER BY "
                r.Order s.OrderBy

            match s.Limit with
            | Some l ->
                w " LIMIT "
                r.Expr l
            | None -> ()

            match s.Offset with
            | Some o ->
                w " OFFSET "
                r.Expr o
            | None -> ()

            if s.ForUpdate then w " FOR UPDATE"

        member r.Insert(i: Insert) =
            let w (x: string) = sb.Append(x) |> ignore
            w $"INSERT INTO {q i.Table} "

            if i.Columns.IsEmpty then
                w "DEFAULT VALUES"
            else
                w "("
                w (i.Columns |> List.map q |> String.concat ", ")
                w ") VALUES ("

                i.Values
                |> List.iteri (fun n e ->
                    if n > 0 then w ", "
                    r.Expr e)

                w ")"

            r.Returning i.Returning

        member r.InsertMany(i: InsertMany) =
            let w (x: string) = sb.Append(x) |> ignore
            w $"INSERT INTO {q i.Table} ("
            w (i.Columns |> List.map q |> String.concat ", ")
            w ") VALUES "

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
                w " ON CONFLICT ("
                w (target |> List.map q |> String.concat ", ")
                w ") DO NOTHING"
            | Some(DoUpdate(target, set)) ->
                w " ON CONFLICT ("
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
            w $"UPDATE {q u.Table} AS {q u.Table} SET "

            u.Set
            |> List.iteri (fun n (c, e) ->
                if n > 0 then w ", "
                w (q c)
                w " = "
                r.Expr e)

            if not u.Where.IsEmpty then
                w " WHERE "
                r.Expr(conj u.Where)

            r.Returning u.Returning

        member r.Delete(x: Delete) =
            let w (s: string) = sb.Append(s) |> ignore
            w $"DELETE FROM {q x.Table} AS {q x.Table}"

            if not x.Where.IsEmpty then
                w " WHERE "
                r.Expr(conj x.Where)

            r.Returning x.Returning

        member r.Returning(cols: (Expr * string) list) =
            if not cols.IsEmpty then
                sb.Append(" RETURNING ") |> ignore

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
