namespace Plasm

open System
open System.Data
open System.Text

/// What a provider can execute faithfully. The planner rejects requests that
/// exceed these rather than substituting weaker semantics.
type ProviderCapabilities =
    { /// Multi-command reads inside one snapshot (e.g. REPEATABLE READ).
      SnapshotReads: bool
      /// The isolation levels a transaction can be run at (Repo.Transact): one
      /// not among them is refused as UnsupportedByProvider, nothing sent.
      IsolationLevels: IsolationLevel list
      /// Key lists can be sent as one array parameter.
      ArrayParameters: bool
      /// Upper bound on keys sent in one command loading an owned or linked list.
      MaxKeysPerCommand: int
      /// INSERT/UPDATE ... RETURNING (single and bulk writes read back rows).
      Returning: bool
      /// Inserts that resolve conflicts (ON CONFLICT ...): upserts.
      Upserts: bool
      /// SELECT ... FOR UPDATE: a query's `locking`, and the read of what a
      /// record owns before a write replaces it (deleting an owner locks what
      /// it owns first).
      RowLocking: bool
      /// One writer holds the database at a time (SQLite): a write that
      /// replaces what a record owns reads it without a row lock, since no
      /// other writer can change it meanwhile.
      WritersTakeTurns: bool
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

/// The catalogue queries the schema check and scaffolding send
/// (schema-check.feature), each given its parameter's name and reading the
/// columns stated, in order. A list of names is sent as the dialect sends a
/// list (ListValue).
type ICatalogueDialect =
    /// table, column, data type (an array's as its elements' type followed by
    /// []), "YES" when nullable, whether it has a default or is generated, its
    /// type's schema, its type's name: the columns of the tables in `tables`.
    abstract Columns: tables: string -> string
    /// The schema an unqualified name is in, as one value.
    abstract CurrentSchema: string
    /// schema, type, label: the labels of the enum types named in `names`, each
    /// type's in their order.
    abstract EnumLabels: names: string -> string
    /// table, key columns joined by commas in order, index name, whether a
    /// constraint holds it: the unique indexes of the tables in `tables`.
    abstract UniqueIndexes: tables: string -> string
    /// table, column, referenced table, constraint name: the single-column
    /// foreign keys of the tables in `tables`.
    abstract References: tables: string -> string
    /// table, key columns joined by commas in order: the primary keys of the
    /// tables in `tables`.
    abstract PrimaryKeys: tables: string -> string
    /// table, referenced table, columns joined by commas in order: the foreign
    /// keys of the tables in `tables`.
    abstract ForeignKeys: tables: string -> string
    /// column, data type, nullable, identity, in the primary key, has a default
    /// (the last five booleans): the columns of the one table `table`, in order.
    abstract TableColumns: table: string -> string
    /// table, text: where the named CHECK constraints of the tables in
    /// `tables` are written, `constraint <name> check (...)` (column-checks
    /// feature): each table's CREATE TABLE (SQLite), or each constraint's
    /// definition (PostgreSQL).
    abstract Checks: tables: string -> string

/// How Ddl prints a model's tables in the database's own DDL (ddl.feature).
/// How text is matched (text-matching.feature): String.StartsWith,
/// EndsWith or Contains in a query.
[<RequireQualifiedAccess>]
type TextMatchKind =
    | StartsWith
    | EndsWith
    | Contains

/// How a recursive query leaves out the rows that close a cycle
/// (recursive-queries.feature): a row whose key a row above it holds is not
/// read, nor followed.
[<RequireQualifiedAccess>]
type CycleCheck =
    /// A clause after the recursive query's parentheses, made from its key's
    /// columns, the flag column and the path column, each quoted: PostgreSQL's
    /// `CYCLE k SET flag USING path`. The rows read are those whose flag is
    /// not set.
    | Clause of clause: (string list -> string -> string -> string)
    /// A path of the keys found, which each row carries in a column of its
    /// own, where the dialect has no such clause (SQLite). `start write key`
    /// writes the start's path of its key's parts; `extend write path key`
    /// the step's, the row found's path with the key added; `holds write path
    /// key` whether a path holds a key. A step's row whose key the path of the
    /// row it follows holds is left out.
    | Path of
        start: ((string -> unit) * (unit -> unit) list -> unit) *
        extend: ((string -> unit) * (unit -> unit) * (unit -> unit) list -> unit) *
        holds: ((string -> unit) * (unit -> unit) * (unit -> unit) list -> unit)

/// What a column holds, as Ddl tells a dialect asked for its check
/// (IDdlDialect.ColumnCheck).
[<RequireQualifiedAccess>]
type ColumnHolds =
    /// One value of this provider type (a string, a DateTime).
    | Scalar of providerType: Type
    /// A list of scalars of this element type (array-columns.feature).
    | List of elementType: Type
    /// A [<Json>] document (json-columns.feature).
    | Document
    /// A union of cases without fields, stored by these labels.
    | Enum of labels: string list

type IDdlDialect =
    /// The words a table or column may not be named without quotes.
    abstract Reserved: Set<string>
    /// What follows a generated key column's type and NOT NULL:
    /// ` generated always as identity primary key` on PostgreSQL.
    abstract GeneratedKey: string
    /// The column type of a database type of its own (an enum type, a document
    /// type), given its name as Ddl writes it.
    abstract StoreColumn: typeName: string -> string
    /// The statement that creates an enum type with its labels, printed before
    /// the tables; None when the dialect has no enum types.
    abstract EnumType: typeName: string * labels: string list -> string option
    /// The check holding a column to what it holds (`check (status in
    /// ('draft', ...))` for an enum, SQLite's JSON array for a list, its form
    /// for a time), printed as a constraint in its table; None where the
    /// column's type holds it (PostgreSQL's enum types, arrays, timestamptz
    /// and jsonb), or for what it never checks.
    abstract ColumnCheck: column: string * holds: ColumnHolds -> string option
    /// Foreign keys are declared in their tables' create statements, not
    /// added after them with alter table.
    abstract ForeignKeysInTables: bool

/// How a provider stores and reads JSON documents (json-columns.feature).
/// Each member writes SQL through `write`; `doc`, `value` and `condition`
/// write their own sub-expressions.
type IJsonDialect =
    /// A document's member: `(doc -> 'key')`.
    abstract Member: write: (string -> unit) * doc: (unit -> unit) * key: string -> unit
    /// The database type a document is stored as (`["jsonb"]`).
    abstract StoreType: string list
    /// A document holding a scalar, as text: `(doc #>> '{}')`.
    abstract Text: write: (string -> unit) * doc: (unit -> unit) -> unit
    /// Whether an element of an array document, named `alias`, meets `condition`.
    abstract AnyElement: write: (string -> unit) * doc: (unit -> unit) * alias: string * condition: (unit -> unit) -> unit
    /// The element named `alias`, inside AnyElement's condition.
    abstract Element: write: (string -> unit) * alias: string -> unit
    /// Whether an array document contains every element of `value` (a document
    /// sent as text).
    abstract Contains: write: (string -> unit) * doc: (unit -> unit) * value: (unit -> unit) -> unit

/// How a provider makes decimals exact when it does not store them as a type
/// the database compares, orders and adds by value (SQLite stores them as
/// text). Translation marks what reads a decimal by value; a dialect with no
/// such hooks (PostgreSQL's numeric is exact) writes it as it is.
type IExactDialect =
    /// `left op right` by value, op one of = <> < <= > >=.
    abstract Compare: write: (string -> unit) * op: string * left: (unit -> unit) * right: (unit -> unit) -> unit
    /// Whether `left` and `right` are equal by value, NULLs equal (or, when
    /// `negated`, whether they differ, NULLs equal).
    abstract NullSafe: write: (string -> unit) * negated: bool * left: (unit -> unit) * right: (unit -> unit) -> unit
    /// `left op right` by value, op one of + - * /.
    abstract Arithmetic: write: (string -> unit) * op: string * left: (unit -> unit) * right: (unit -> unit) -> unit
    /// A decimal aggregate by value: SUM, AVG, MIN or MAX of `operand`.
    abstract Aggregate: write: (string -> unit) * name: string * operand: (unit -> unit) -> unit
    /// A decimal aggregate by value over a window: SUM or AVG of `operand`,
    /// `over` writing its OVER clause (` OVER (...)`).
    abstract WindowAggregate: write: (string -> unit) * name: string * operand: (unit -> unit) * over: (unit -> unit) -> unit
    /// An operand ordered or grouped by value, as ORDER BY and GROUP BY take
    /// it, the operand of IN, which compares the values in its list so, and a
    /// column of SELECT DISTINCT or of a set operation's left operand, which
    /// tell rows apart so.
    abstract Ordered: write: (string -> unit) * operand: (unit -> unit) -> unit
    /// The value a decimal is sent as.
    abstract Value: decimal -> obj

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
    /// The type a value sent as text is cast to, to be stored as, or compared
    /// with, a database type of its own (an enum type, a document type): given
    /// that type's name, and whether it is its array type. None sends the
    /// value as it is, for a dialect that stores such values as text.
    abstract StoreCast: typeName: string list * array: bool -> string option
    /// The type a column of a database type of its own is read as, to be read
    /// as text; None reads it as it is.
    abstract TextCast: string option
    /// The value a list is sent as, as one parameter (PostgreSQL: the array).
    abstract ListValue: Array -> obj
    /// Why the database cannot hold a value Plasm would send (a column's, a
    /// list's), asked before anything is sent: a write's refusal names its
    /// field, a query's is a translation failure. None for one it holds.
    abstract Refusal: value: obj -> string option
    /// Membership of `operand` in the list sent as the parameter `list`,
    /// cast as `cast` says when its elements have a database type of their
    /// own: `operand = ANY(list)` on PostgreSQL.
    abstract InList: write: (string -> unit) * operand: (unit -> unit) * list: string * cast: string option -> unit
    /// Rows of `columns` among the rows the lists make, the i-th list holding
    /// the i-th column's values, each with its cast: `(a, b) IN (SELECT * FROM
    /// unnest(@p0, @p1))` on PostgreSQL.
    abstract RowsIn: write: (string -> unit) * columns: (unit -> unit) list * lists: (string * string option) list -> unit
    /// `operand` not among the values of the list sent as the parameter
    /// `list`: `operand <> ALL(list)` on PostgreSQL.
    abstract NotInList: write: (string -> unit) * operand: (unit -> unit) * list: string * cast: string option -> unit
    /// The rows the lists make, the i-th list the i-th column, as a table a
    /// FROM reads, named `alias(columns)` when given: `unnest(@p0, @p1) AS
    /// "u"("k1", "k2")` on PostgreSQL.
    abstract ListRows: write: (string -> unit) * lists: (string * string option) list * alias: (string * string list) option -> unit
    /// An empty array's literal: a list aggregate's value over no rows
    /// (`'{}'`).
    abstract EmptyArray: string
    /// Whether a list column holds `element` (List.contains over a list of
    /// scalars, array-columns.feature): `element = ANY(column)` on PostgreSQL.
    abstract ListHas: write: (string -> unit) * element: (unit -> unit) * column: (unit -> unit) -> unit
    /// Whether a list column holds nothing (List.isEmpty):
    /// `cardinality(column) = 0` on PostgreSQL.
    abstract ListIsEmpty: write: (string -> unit) * column: (unit -> unit) -> unit
    /// The catalogue queries the schema check and scaffolding send.
    abstract Catalogue: ICatalogueDialect
    /// How Ddl prints the model's tables.
    abstract Ddl: IDdlDialect
    /// What LIMIT takes for no limit, when the dialect needs a LIMIT before an
    /// OFFSET (SQLite's -1); None when OFFSET stands on its own (PostgreSQL).
    abstract UnboundedLimit: string option
    /// How a recursive query leaves out the rows that close a cycle
    /// (recursive-queries.feature).
    abstract Cycle: CycleCheck
    /// A division or remainder (`op`, "/" or "%") of two numbers, so that a
    /// divisor of zero fails the statement (division-by-zero.feature):
    /// PostgreSQL's `dividend / divisor`, which fails so; SQLite's, which
    /// answers NULL, made to fail.
    abstract Division: write: (string -> unit) * op: string * dividend: (unit -> unit) * divisor: (unit -> unit) -> unit
    /// The null-safe comparison operator: equality treating NULLs as equal
    /// (`IS NOT DISTINCT FROM`), or its negation when `negated`.
    abstract NullSafeComparison: negated: bool -> string
    /// `operand` starts with, ends with or contains a text, matched with its
    /// case and every character as itself (text-matching.feature): `operand
    /// LIKE @p` on PostgreSQL, the text escaped and `%` added. `pattern shape`
    /// writes the text's parameter, its value `shape` of the text.
    abstract TextMatch: write: (string -> unit) * kind: TextMatchKind * operand: (unit -> unit) * pattern: ((string -> string) -> unit) -> unit
    /// The dialect's name for a function the core uses (char_length, btrim,
    /// lower, upper, COALESCE, ...); return the name unchanged if it is the same.
    abstract FunctionName: string -> string
    /// JSON documents ([<Json>] fields); None when the provider has none, and
    /// then a repository refuses a model with a [<Json>] field.
    abstract Json: IJsonDialect option
    /// How decimals are made exact; None when the database's own decimal
    /// type is (PostgreSQL's numeric).
    abstract Exact: IExactDialect option
    /// The BEGIN the provider sends for a transaction at this isolation level,
    /// as the database logs it; OnCommand reports it (transaction-events.feature).
    abstract Begin: IsolationLevel -> string

module internal Sql =
    /// A query that cannot be translated or rendered, as DbError.Translation.
    exception TranslationFailure of TranslationError


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
        /// An empty array, as the dialect writes one: a list aggregate's value
        /// over no rows.
        | EmptyArray
        /// A list column holds the element (ISqlDialect.ListHas).
        | ListHas of element: Expr * column: Expr
        /// A list column holds nothing (ISqlDialect.ListIsEmpty).
        | ListIsEmpty of column: Expr
        | Binary of op: string * Expr * Expr
        | Not of Expr
        /// `(a) IS NOT TRUE`: F#'s `not`, for which an unknown (NULL) operand,
        /// like a false one, makes the negation true.
        | IsNotTrue of Expr
        | IsNull of Expr
        | IsNotNull of Expr
        | NotDistinctFrom of Expr * Expr
        | DistinctFrom of Expr * Expr
        /// Rows of several columns (a key of several parts) among the rows the
        /// lists make, one list a column, each sent as one parameter as the
        /// dialect sends a list; `casts` name the database type each list's
        /// elements are cast to.
        | RowsInLists of columns: Expr list * lists: Array list * casts: string list option list
        /// Membership in a list sent as one parameter (a prepared query's list
        /// argument, a Sql.Bound resolving to an array), as the dialect sends a
        /// list; `cast` names the database type its elements are cast to.
        | InListParam of Expr * list: obj * cast: string list option
        /// Membership in a list of values; `cast` names the database type the
        /// values are cast to (an enum type), when they are not sent as it.
        | AnyOf of Expr * values: Array * cast: string list option
        /// `CAST(e AS "type")` or, for an array, `CAST(e AS "type"[])`: a value
        /// sent as text, given the database type it is compared with or stored as.
        | Cast of Expr * typeName: string list * array: bool
        /// `CAST(e AS text)`: a column read as text (an enum type's label).
        | AsText of Expr
        /// A text match (ISqlDialect.TextMatch): `text` the text itself, a
        /// parameter or a prepared argument, which the dialect shapes.
        | TextMatch of kind: TextMatchKind * operand: Expr * text: Expr
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
        /// A recursive query's path of the keys found (CycleCheck.Path): the
        /// start's of its key, or the step's, `path` with its key added.
        | CyclePath of path: Expr option * key: Expr list
        /// Whether a recursive query's path holds a key (CycleCheck.Path).
        | CycleHolds of path: Expr * key: Expr list
        /// A comparison, null-safe comparison, arithmetic, aggregate or window
        /// aggregate of decimals, made by value as the dialect's IExactDialect makes it, or
        /// a decimal taken by value as a GROUP BY takes its keys, IN its
        /// operand and DISTINCT its columns; as it is where the dialect has
        /// none.
        | Exact of Expr

    and OrderItem =
        { Expr: Expr
          Desc: bool
          NullsFirst: bool option
          /// Ordered by value as the dialect's IExactDialect orders (a decimal).
          ByValue: bool }

    and Source =
        | Table of name: string * alias: string
        | Subquery of Select * alias: string
        /// Two selects combined (UNION, UNION ALL, INTERSECT, EXCEPT) and read
        /// as one table named `alias` (set-operations.feature).
        | Compound of kind: string * left: Select * right: Select * alias: string
        /// A recursive query (recursive-queries.feature): WITH RECURSIVE `name`
        /// AS (start UNION ALL step), its cycles stopped on `key` as the
        /// dialect stops them (ISqlDialect.Cycle), read as one table named
        /// `alias`, without the rows that closed a cycle.
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

    /// The database type a store type names for this dialect: a document's is
    /// the JSON dialect's.
    let storeName (d: ISqlDialect) (parts: string list) =
        if parts = Store.jsonDocument then
            match d.Json with
            | Some j -> j.StoreType
            | None -> failwith $"{d.Name} has no JSON documents"
        else
            parts

    /// The type a value of a store type is cast to on this dialect, or None
    /// when it is sent as it is.
    let castName (d: ISqlDialect) (parts: string list) (array: bool) = d.StoreCast(storeName d parts, array)

    /// The database type a field's values are stored as, when it is not the
    /// provider type's own (Scalar.asPgEnum).
    let storeType (f: FieldMap) = f.Mapping |> Option.bind (fun m -> m.StoreType)

    /// A field value as a parameter, cast to the field's database type if it has one.
    /// A provider value as a parameter: a list (an array column's value) as
    /// the dialect sends a list (ListValue).
    let private sent (v: obj) =
        match v with
        | :? Array as a when not (v :? byte[]) -> ArrayParam a
        | v -> Param v

    let fieldValue (f: FieldMap) (value: obj) =
        let p = sent (f.ToDb value)

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
        let x = sent providerValue

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

        /// What a mark stands for, as precedence sees it: where the dialect
        /// has no exact hooks, what is written in its place.
        let unmarked (e: Expr) =
            match d.Exact, e with
            | None, Exact(NotDistinctFrom(a, b)) -> Binary(d.NullSafeComparison false, a, b)
            | None, Exact(DistinctFrom(a, b)) -> Binary(d.NullSafeComparison true, a, b)
            | _, Exact x -> x
            | _, e -> e

        let json () =
            match d.Json with
            | Some j -> j
            | None -> failwith $"{d.Name} has no JSON documents"

        /// `v`, unless the database cannot hold it (ISqlDialect.Refusal).
        member _.Held(v: obj) =
            match d.Refusal v with
            | Some why -> raise (TranslationFailure(TranslationError.InvalidQuery why))
            | None -> v

        member r.Param(v: obj) =
            // A prepared argument's is asked when it is bound (Execution), so
            // it stays the one object it is wherever it is written.
            let v =
                match v with
                | :? Bound -> v
                | v -> r.Held v
            // A decimal is sent as the dialect's exact hooks send it.
            let v =
                match d.Exact, v with
                | Some x, (:? decimal as m) -> x.Value m
                | Some x, (:? Bound as b) ->
                    box (
                        Bound(
                            (fun args ->
                                match b.Resolve args with
                                | :? decimal as m -> x.Value m
                                | other -> other),
                            b.Label
                        )
                    )
                | _ -> v

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
            | ArrayParam v -> w (r.Param(d.ListValue(r.Held v :?> Array)))
            | Literal l -> w l
            | EmptyArray -> w d.EmptyArray
            | ListHas(element, column) -> d.ListHas(w, (fun () -> r.Expr element), (fun () -> r.Expr column))
            | ListIsEmpty column -> d.ListIsEmpty(w, (fun () -> r.Expr column))
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

                        match unmarked x with
                        // An OR inside an AND needs them; an AND inside an OR is
                        // bracketed to be read at a glance.
                        | Binary(("AND" | "OR"), _, _) -> r.Wrapped x
                        | Binary(o, _, _) when not (comparison o) && not (arithmetic o) -> r.Wrapped x
                        | _ -> r.Expr x)
            | Exact x ->
                match d.Exact, x with
                | None, NotDistinctFrom(a, b) -> r.Expr(Binary(d.NullSafeComparison false, a, b))
                | None, DistinctFrom(a, b) -> r.Expr(Binary(d.NullSafeComparison true, a, b))
                | None, x -> r.Expr x
                | Some ex, Binary(op, a, b) when comparison op ->
                    ex.Compare(w, op, (fun () -> r.Expr a), (fun () -> r.Expr b))
                | Some ex, Binary(op, a, b) when arithmetic op ->
                    ex.Arithmetic(w, op, (fun () -> r.Expr a), (fun () -> r.Expr b))
                | Some ex, NotDistinctFrom(a, b) -> ex.NullSafe(w, false, (fun () -> r.Expr a), (fun () -> r.Expr b))
                | Some ex, DistinctFrom(a, b) -> ex.NullSafe(w, true, (fun () -> r.Expr a), (fun () -> r.Expr b))
                | Some ex, Aggregate(name, false, Some a) -> ex.Aggregate(w, name, (fun () -> r.Expr a))
                | Some ex, Window(name, [ a ], partition, order, frame) ->
                    ex.WindowAggregate(w, name, (fun () -> r.Expr a), (fun () -> r.Over(partition, order, frame)))
                // A value taken by value: a GROUP BY's key, an IN's operand, a
                // DISTINCT's column.
                | Some ex, x -> ex.Ordered(w, (fun () -> r.Expr x))
            | Binary(op, a, b) when comparison op ->
                r.Operand(a, true)
                w $" {op} "
                r.Operand(b, true)
            | Binary(op, a, b) when op = "/" || op = "%" -> d.Division(w, op, (fun () -> r.Operand(a, false)), (fun () -> r.Operand(b, false)))
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
            | RowsInLists(columns, lists, casts) ->
                let sent =
                    List.map2 (fun (l: Array) (c: string list option) -> r.Param(d.ListValue(r.Held l :?> Array)), c |> Option.bind (fun t -> castName d t true)) lists casts

                w "("
                d.RowsIn(w, columns |> List.map (fun c () -> r.Expr c), sent)
                w ")"
            | InListParam(a, list, cast) ->
                let sent =
                    match list with
                    | :? Bound as b -> box (Bound((fun args -> d.ListValue(r.Held(b.Resolve args) :?> Array)), b.Label))
                    | v -> d.ListValue(r.Held v :?> Array)

                let name = r.Param sent
                d.InList(w, (fun () -> r.Operand(a, true)), name, cast |> Option.bind (fun t -> castName d t true))
            | AnyOf(a, values, cast) ->
                let typed (p: string) array =
                    match cast |> Option.bind (fun t -> castName d t array) with
                    | Some name -> $"CAST({p} AS {name})"
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
                match castName d t array with
                | Some name ->
                    w "CAST("
                    r.Expr e
                    w $" AS {name})"
                | None -> r.Expr e
            | AsText e ->
                match d.TextCast with
                | Some name ->
                    w "CAST("
                    r.Expr e
                    w $" AS {name})"
                | None -> r.Expr e
            | TextMatch(kind, a, text) ->
                let pattern (shape: string -> string) =
                    match text with
                    | Param(:? Bound as b) -> w (r.Param(box (Bound((fun args -> box (shape (string (b.Resolve args)))), b.Label))))
                    | Param v -> w (r.Param(box (shape (string v))))
                    | other -> invalidOp $"a text match's text is a parameter, not {other}"

                d.TextMatch(w, kind, (fun () -> r.Operand(a, true)), pattern)
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
            | CyclePath(path, key) ->
                let parts = key |> List.map (fun k -> fun () -> r.Expr k)

                match d.Cycle, path with
                | CycleCheck.Path(start, _, _), None -> start (w, parts)
                | CycleCheck.Path(_, extend, _), Some p -> extend (w, (fun () -> r.Expr p), parts)
                | CycleCheck.Clause _, _ -> invalidOp "a dialect with a cycle clause has no path of its own"
            | CycleHolds(path, key) ->
                match d.Cycle with
                | CycleCheck.Path(_, _, holds) -> holds (w, (fun () -> r.Expr path), key |> List.map (fun k -> fun () -> r.Expr k))
                | CycleCheck.Clause _ -> invalidOp "a dialect with a cycle clause has no path of its own"
            | RowNumber(p, order) ->
                w "ROW_NUMBER() OVER (PARTITION BY "
                r.Expr p

                if not order.IsEmpty then
                    w " ORDER BY "
                    r.Order order

                w ")"
            | Aggregate(name, distinct, arg) ->
                w (d.FunctionName name)
                w "("

                match arg with
                | None -> w "*"
                | Some a ->
                    if distinct then w "DISTINCT "
                    r.Expr a

                w ")"
            | Window(name, args, partition, order, frame) ->
                w (d.FunctionName name)
                w "("

                args
                |> List.iteri (fun i a ->
                    if i > 0 then w ", "
                    r.Expr a)

                w ")"
                r.Over(partition, order, frame)
            | AggregateBy(name, args, order, filter) ->
                w (d.FunctionName name)
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
        /// A window function's OVER clause.
        member r.Over(partition: Expr list, order: OrderItem list, frame: string option) =
            let w (s: string) = sb.Append(s) |> ignore
            w " OVER ("
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

        member r.Operand(e: Expr, ofComparison: bool) =
            match e with
            // Bracketed as what it marks is: what the dialect writes for it
            // binds as tightly.
            | Exact _ ->
                match unmarked e with
                | Binary(op, _, _) when ofComparison && arithmetic op -> r.Expr e
                | Binary _
                | NotDistinctFrom _
                | DistinctFrom _ -> r.Wrapped e
                | _ -> r.Expr e
            | Column _
            | Param _
            | ArrayParam _
            | Literal _
            | EmptyArray
            | Func _
            | Cast _
            | AsText _
            | Aggregate _
            | RowNumber _
            | Case _
            | Fragment _
            | ListHas _
            | Exists _ -> r.Expr e
            | Binary(op, _, _) when ofComparison && arithmetic op -> r.Expr e
            | _ -> r.Wrapped e

        member r.Order(items: OrderItem list) =
            items
            |> List.iteri (fun i o ->
                if i > 0 then sb.Append(", ") |> ignore

                match d.Exact with
                | Some ex when o.ByValue -> ex.Ordered((fun x -> sb.Append(x) |> ignore), (fun () -> r.Expr o.Expr))
                | _ -> r.Expr o.Expr

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

                match unmarked e with
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
                // Where the dialect has no CYCLE clause, each row carries the
                // path of the keys above it, and the step leaves out a row
                // whose key the path of the row it follows holds.
                let start, step =
                    match d.Cycle with
                    | CycleCheck.Clause _ -> start, step
                    | CycleCheck.Path _ ->
                        // The recursive query's columns are named by the start's;
                        // the step's are its columns in the same places.
                        let places =
                            key
                            |> List.map (fun k ->
                                let named (e, a) =
                                    a = k
                                    || (String.IsNullOrEmpty a
                                        && match e with
                                           | Column(_, c) -> c = k
                                           | _ -> false)

                                match start.Columns |> List.tryFindIndex named with
                                | Some i -> i
                                | None -> invalidOp $"a recursive query's start has no column {k} of its key")

                        let keyOf (s: Select) = places |> List.map (fun i -> fst s.Columns.[i])

                        let found =
                            match step.Joins |> List.tryFind (fun j -> j.Table = name && j.Sub.IsNone) with
                            | Some j -> Column(j.Alias, "plasm_path")
                            | None -> invalidOp "a recursive query's step does not join the rows found"

                        { start with Columns = start.Columns @ [ CyclePath(None, keyOf start), "plasm_path" ] },
                        { step with
                            Columns = step.Columns @ [ CyclePath(Some found, keyOf step), "plasm_path" ]
                            Where = step.Where @ [ Not(CycleHolds(found, keyOf step)) ] }

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
                w ")"

                match d.Cycle with
                | CycleCheck.Clause clause ->
                    w (" " + clause (key |> List.map q) (q "plasm_cycle") (q "plasm_path"))
                    w "\n"
                    w indent
                    w ("SELECT * FROM " + q name + " WHERE NOT " + q "plasm_cycle")
                | CycleCheck.Path _ ->
                    w "\n"
                    w indent
                    w ("SELECT * FROM " + q name)
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

            // A page is one line: LIMIT and OFFSET together. Rows skipped with
            // no limit take the dialect's unbounded limit, if it needs one.
            let limited =
                match s.Limit, s.Offset, d.UnboundedLimit with
                | Some l, _, _ ->
                    clause "LIMIT "
                    r.Expr l
                    true
                | None, Some _, Some unbounded ->
                    clause $"LIMIT {unbounded}"
                    true
                | _ -> false

            match s.Offset with
            | Some o ->
                if limited then w " OFFSET " else clause "OFFSET "
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
