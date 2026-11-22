# Status: verified behaviour, measurements and gaps

*As of 2026-11-14: the one style.*

**Plasm is new, and its API may change before 1.0.** It is designed and verified end-to-end
against PostgreSQL:

- the application's own records stored by convention, each saying on itself
  what the types cannot (its table, unique fields, its version)
- aggregates that own lists and records, and link to shared records, always
  read and written whole
- typed immutable queries (`plasm { }`), joins, `exists`, aggregates and
  grouping
- typed transactions as the only way to write, with set-based bulk writes and
  upserts as steps
- typed escape hatches to hand-written SQL
- prepared queries that are built and planned once
- a start-up check of queries and schema, the DDL the model expects, and
  rolled-back test sandboxes

Its scope is deliberately narrow, its performance has had two tuning passes,
and it has had no production use. Its design comes from Ecto, but Ecto's
operational maturity does not transfer. That has to be earned.

**One style.** Store the domain's own records. Types state what they can:
wrappers and `create` functions, fieldless unions, unions with fields, and
typed ids as references. The record says how it is stored: `[<Table>]`, its
field named `Id`, owned and linked records found from its field types, and
`[<Unique>]`, `[<Version>]` and `[<OrderedBy>]` on its fields; there is no
separate schema. Other aggregates are referred
to by id and reached by queries. Write with `Tx<'T, 'E>` transactions, and
check queries and the schema at start-up. [`examples/Kanban`](../examples/Kanban)
is the reference application. There is no other style: the row-record API
(`Assoc<'T>`, `preload`, public changesets, pipeline queries, repository
writes) was removed on 2026-11-14; see [the ledger](#the-one-style-ledger).

## Verification

| Suite | What it proves | Result |
|---|---|---|
| `tests/Plasm.Specs` (Reqnroll, real PostgreSQL 18) | Every scenario of `features/*.feature`: the user's two specifications (`core.feature`, `elegance.feature`, with scenarios removed or rewritten for the one style, listed below) and every feature added since, each written and run red before it was built. See `docs/acceptance.md`. | 727 / 727 pass (outline examples counted separately) |
| `tests/Plasm.Tests`: `Translation.fs` | SQL-shape contracts (options, membership, `exists`, `List.exists`/`forall` over owned lists, pagination), unsupported-expression errors, model-configuration errors | 42 pass |
| `tests/Plasm.Tests`: `CompileTime.fs` | Typed API acceptance and rejection, checked with FSharp.Compiler.Service against the real assemblies; each rejection pins its error code and has a passing control snippet | 15 pass |
| `tests/Plasm.Tests`: `Properties.fs` (FsCheck, real database) | Predicate-order equivalence matching F# semantics; extending a query never alters it; an update records exactly the differing fields; the same aggregates for every batch size, with key-proportional command counts; owned lines keep their declared order per owner | 5 properties pass |
| `tests/Plasm.Tests`: `EctoCompat.fs` (real database) | Adapted Ecto repo and multi cases, including one deliberate difference. See `docs/ecto-compatibility.md`. | 14 pass |
| `tests/Plasm.Tests`: `Joins.fs` | Join SQL shape: ON conditions, composite keys, filters before and after joins, left-join option semantics, ordering, projections, misuse errors | 12 pass |
| `tests/Plasm.Tests`: `Behaviour.fs` (real database) | Edge paths: `One`, projections, materialisation errors, snapshot reads, key and version changes refused, owned items of another owner refused and rolled back, `Tx.traverse`, cancellation | 11 pass |
| `tests/Plasm.Tests`: `Batch2.fs` (SQL shape and real database) | Aggregates and grouping errors; bulk misuse errors, foreign-key failures, a 40,000-row insert split into two commands that roll back together, composite conflict targets; raw SQL with list, option and domain parameters; prepared patterns; join-table type checks | 26 pass |
| `tests/Plasm.Tests`: `ConventionCost.fs`, `Smoke.fs`, `WriteSmoke.fs` | A value read by convention allocates no more than a registered mapping (three types); a page read and a write path end to end | 5 pass |
| `koans/Plasm.Koans.Specs` | Every koan (22 lessons, 232 koans) passes with its reference answer and stops at its blank as shipped | 250 checks pass |
| `koans/browser` | The browser koans (PGlite) | 120 scenarios pass |

Run the library's tests with `dotnet test` from the repository root (Docker is
required, or set `PLASM_TEST_PG`): 727 scenarios and 219 unit tests.

## Supported behaviour

**Records and scalars:**

- F# records only; the records stored are the application's own.
- Built-in scalars: `int16`, `int`, `int64`, `string`, `bool`, `decimal`,
  `float`, `float32`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`,
  `TimeOnly`, `TimeSpan` and `byte[]`, each optionally as `option`.
- By convention, with nothing registered: a single-case union over a scalar
  (`OrderId of int64`) is stored as that scalar; a type with a `create:
  'P -> Result<'T, string>` (static member or in its module) is read back
  through it, so a stored value the domain refuses is a materialisation error
  with the domain's reason; a private representation without `create` is
  refused; a fieldless union is stored as its case name in snake case and
  ordered by its declaration. Conventions are compiled once per type, so a
  value read by convention costs what a registered mapping costs.
- A union with fields is spread over columns: the case name in the field's
  column, each case field in `<column>_<case field>`, nested unions likewise. A
  row that is no case, or holds a value in another case's column, is refused
  when read, naming the field.
- A record with no `[<Table>]` and no Id, held as one field (or an option of
  one), is a value stored in its owner's row: each field in `<field>_<subfield>`,
  nested values chaining the prefix. An optional value is None when every
  column is NULL and must have a field that is not optional; a partly stored
  value is refused when read, naming the field. `c.Address.City` is a column in
  queries; `c.Address = a` compares every column; `Ddl()` prints a CHECK that an
  optional value is all or nothing (`value-records.feature`). Selecting a whole
  value in a projection is not supported (select its fields, or the record), and
  a value cannot hold a list, a stored record, `[<Unique>]` or `[<Column>]`.
- A list of scalars is one PostgreSQL array column, from the type alone
  (`array-columns.feature`). The scalars are built-in types, single-case
  wrappers, types read through `create`, and fieldless unions. A list of
  stored records stays owned or linked, and a list of anything else is
  refused, saying `[<Json>]` stores it as one document. It is never NULL (an
  empty list is an empty array). Each element is read as its column would be,
  so a refused element is a read error naming `Field[i]`. Queries translate
  `List.contains` (`= ANY`) and `List.isEmpty` (`cardinality(...) = 0`) and
  refuse anything else over the array by name. `Ddl()` prints `text[]`,
  `integer[]` and the like, and `CheckSchema` compares the elements' type. A
  nullable array column is reported as for any required field. An option of
  a list (an empty list is already no elements), a list of options and an F#
  array field are refused.
- A record whose Id is a record has a key of several parts
  (`composite-keys.feature`): the key's parts are the table's own columns, by
  their own names, under one primary key; a value record anywhere else is
  prefixed. The parts are scalars, none optional, at least two; nothing is
  generated. A query reads a part as a column and compares a whole key with `=`,
  also as a prepared argument; ordering by the whole key, or comparing it with
  `<`, is refused in favour of its parts. Updates and deletes find the row by
  every part and the version, and a part cannot be changed. `Ddl()` prints
  `primary key (a, b)` and `CheckSchema` reports a primary key over other
  columns. A field of a key record refers over every part
  (`composite-references.feature`): its columns are the field's column followed
  by the key's parts, or `[<Column("a", "b")>]` names them. A key record inside
  an Id is part of the key and a reference, and `[<NotReference>]` says a field
  refers to no row. An optional one stores every part or none, and a row with
  some parts NULL is a read error naming the field. An owned list under such a
  key is read in one command a level, its owners' keys sent as an array per part
  through `unnest` (the composite index is used: 3,000 keys, 9,000 rows, 6.7
  ms). `Ddl()` prints the foreign keys over several columns, and `CheckSchema`
  reports one that is missing. An upsert, or an insert of the missing records,
  takes the whole key as its conflict target, every part; bulk updates and
  deletes find rows by any part. A linked list joins such records on either
  side, a join-table column per part. Not yet: an owned list of records keyed
  by several parts, refused by name.
- `[<Json>]` on a field stores it as one jsonb document: a record with no
  table and no Id, a list of them, a list of scalars, or an option of these
  (`json-columns.feature`). Plasm owns the mapping: a record is an object keyed
  in snake case, None is a missing key, a list is an array, a scalar is stored
  as its column would be (a typed id as its number, a fieldless union as its
  case name, a `DateTime` as UTC ISO 8601 text, a `DateTimeOffset` with its
  offset). Reads are strict and name the path: a missing or mistyped key, an
  unknown case, and a key the record does not have are all refused. Queries
  read document fields (`c.Settings.Theme`), `List.exists`/`List.forall` over a
  document list, `List.contains` over a document list of scalars, and compare
  whole documents; a change writes the whole document. `Ddl()` prints `jsonb`,
  `CheckSchema` requires it, and a provider without JSON (`ISqlDialect.Json =
  None`) refuses such a model when the repository is made.
- Registered mappings override the conventions: `Scalar.wrap`,
  `Scalar.tryWrap`, `Scalar.enum`, and `Scalar.asPgEnum "type"` to store a
  fieldless union as a PostgreSQL enum type (sent as text cast to the type, read
  as text; no Npgsql enum mapping needed; the schema check verifies the labels
  and their order). Comparisons, ordering and `Agg.min`/`Agg.max` on these
  types follow F#'s order (declaration order), not the stored text.
- A stored record names its table (`[<Table "orders">]`). Its key is its field
  named `Id`: database-generated when it is an integer or a typed id over one,
  otherwise assigned; `[<Assigned>]` for an integer key the application
  assigns. `[<Generated>]` columns; an integer `[<Version>]` field.
- A column is named after its field, in snake_case, unless `[<Column>]` says
  otherwise (for tables the application did not design; `repo.Scaffold`
  prints it).
- `Model.build scalars [ typeof<Order>; ... ]` lists the records that are not
  owned; the records they own come with them. `tableOf<Order>` is the query
  source.

**Records and aggregates:**

- `[<Unique>]` states a unique field. A typed id is inferred as a reference to
  the record whose key it is (built-in key types never are), unless the field
  says `[<NotReference>]`. A typed id that is the key of two records is a model
  error.
- A field `OrderLine list`, `Profile option` or `Settings` of a stored record
  that holds the owner's key is owned: the record's own rows, read in the order
  of their key unless `[<OrderedBy(nameof the<OrderLine>.UnitPrice)>]` names
  another field. A list of stored records that hold no key of the owner is
  linked through the join table its field names (`[<JoinTable
  "article_tags">]`, columns `article_id` and `tag_id` by default).
- What the types leave ambiguous is refused when the model is built, naming
  the record and field: no table, no `Id`, two fields holding the owner's key,
  a linked list without a join table or whose records are not in the model, an
  owned record also listed or owned twice, an unknown `[<OrderedBy>]` field, a
  list of records that are not stored, two version fields.
- Owned and linked fields are always read with their owner, at every level:
  one command per list per batch of owner keys (`= ANY(@keys)`), whatever the
  number of owners. A required owned record that is missing, or an owned
  record matched by two rows, is refused when read, naming the field.
- A whole owner selected in a join, a projection's record or hand-written SQL
  comes with what it owns, read the same way.
- Writing the owner writes what it owns: an insert inserts its owned rows and
  links; `Tx.update (original, changed)` writes the difference by key (new
  items inserted, changed ones updated in their changed columns, missing ones
  deleted) and replaces the links; a delete deletes owned rows and links,
  never linked records. A linked record changed through its owner is refused:
  it belongs to its own aggregate. An owned item carrying another owner's key,
  or the same stored item listed twice, is refused before any command.

**Queries:**

- Written only as `plasm { }` expressions (`Query.from (tableOf<Order>)` for every row,
  `Query.prepare` for prepared ones). Operations: `where`, `orderBy` /
  `orderByDescending` / `thenBy` / `thenByDescending`, `take` and `skip`
  (ordering required; the primary key is appended as a tiebreaker), `select`
  projections (tuples, records, anonymous records, arithmetic over columns),
  `join` / `leftJoin`, `groupBy` and `having`.
- Predicates:
  - comparisons, `&&`, `||`, `not` (two-valued over nullable operands, `IS NOT
    TRUE`) and arithmetic
  - equality with `None` (`IS NULL`), and option-to-option equality
    (`IS NOT DISTINCT FROM`); `.IsSome`, `.IsNone`, `.Value`,
    `Option.get`/`isSome`/`isNone`
  - a null that is not an option (a captured null string, or a null prepared
    argument) is refused, never read as `IS NULL` or `= NULL`
    (`query-semantics.feature`)
  - clauses keep the order they are written in: `where`, `orderBy`,
    `groupBy` or `having` after `take` or `skip`, a second `take` or `skip`,
    `skip` after `take`, and `where` after `groupBy` (use `having`) are
    refused, never reordered; `skip`, then `take`, pages as F# does
    (`query-semantics.feature`)
  - `List`/`Seq`/`Array.contains` and `Contains` over captured collections
    (`= ANY`, or `FALSE` when empty)
  - `StartsWith`, `EndsWith` and `Contains` (escaped `LIKE`); `ToLower`,
    `ToUpper`, `Trim` and `Length`
  - case tests (`c.Stage.IsDoing`), comparison with a case or a whole value,
    and `match` on case fields, for unions with fields; the domain's own
    `[<ReflectedDefinition>]` members and functions are inlined
  - `exists (plasm { ... })` over another aggregate: a correlated `EXISTS`
    reading the outer row and prepared arguments, in the same command
  - `List.exists` / `List.forall` over an owned or linked list, and
    `Option.exists` / `Option.forall` / `IsSome` / `IsNone` over an owned
    optional record, as correlated `EXISTS` / `NOT EXISTS`
- Everything else that depends on a row is rejected with
  `UnsupportedExpression`. Nothing is evaluated in memory.

**Joins:**

- `join c in tableOf<Customer> on (o.CustomerId = Some c.Id)` (inner) and `leftJoin`
  (the joined record is `'T option`), chained into flat tuples. Composite keys:
  `on ((a.X, a.Y) = (b.X, b.Y))`. Keys compare with SQL `=`, so NULL keys never
  match.
- Filters and orderings over every source; left-joined sources use option
  semantics (`c.IsNone`, `c = None`, `Option.exists`, `Option.forall`,
  `Option.map` / `Option.bind` to a field).
- Results are tuples of whole records (each with what it owns) or projections
  that read only the named columns.

**Counts and aggregates:**

- `repo.Count` (one row transferred; a page counts the page, a grouped query its
  groups), `repo.Exists` (reads at most one row), and `Tx.count`.
- `selectDistinct (...)`: each selected value once, as `List.distinct` (None
  is one value); ordered by selected values only (`distinct.feature`).
- `Agg.count`, `countDistinct`, `sum`, `min`, `max` and `avg` in `select`,
  `having` and orderings. `Agg.sum` of no rows is 0, as `List.sum []`; `min`,
  `max` and `avg` return options. Summing text or averaging an integer key does
  not compile. Domain types come back as domain types.
- `groupBy key` (a tuple for several keys), grouping across joins, ordering by
  aggregates; paged groups get the grouping keys as a tiebreaker. A `match`
  that produces values is a SQL `CASE`, so a member naming each case of a union
  can be grouped by.
- Checked when planned: selecting or ordering by a column that is neither
  grouped nor aggregated, aggregates in `where`, nested aggregates, grouped
  queries without `select`.

**Reading:**

- `repo.All`, `repo.One` (keeps the query's own ordering and `take`, reads at
  most two rows, `DbError.MoreThanOneRow` for a second match), `repo.Count`,
  `repo.Exists`, `repo.Explain` (the plan and its loads, with no database
  access).
- `ReadConsistency.Snapshot` reads a query and what it owns in one snapshot.
- Loads bound with `let!` and `and!`, the row built by `return`
  (`loads.feature`): after `for` and its `where`, `orderBy` and `take`, each
  `let!` or `and!` binds what one more query finds for each record, with its
  conditions, order and `take` per record, one more command per batch at any
  depth, so loads never multiply rows; there is no limit to how many. A query
  gives a list of its records or of what it selects (`select`,
  `selectDistinct`); `referredBy<Customer> o`, and what is selected of it,
  an option. A load may bind loads of its own. `return` builds a record or an
  anonymous record of the query's record, its fields or members, and the
  loads, each load once and in the field of its name (`let! newest` in
  `Newest`), so two loads of one type cannot be crossed; anything else is
  refused when the query is planned, so at start-up. A tie to an optional
  column is written `o.CustomerId = Some c.Id`.
- `referring<Payment> o` and `referredBy<Customer> o`: the tie the typed ids
  prove, so a plain relationship is never written by hand. `referring` is the
  Payments whose one field referring to o's record (plain or an option) holds
  o's key; `referredBy` is the Customer whose key o's one field referring to a
  Customer holds, at most one, so a `let!` of it binds an option and `repo.One`
  reads it for a record in hand. `referring` is a query: in `let!` and
  `exists`, as a source narrowed further, with a record in hand, in prepared
  queries. The key is never the tie, so a record referring to
  its own kind reads its children with one and its parent with the other. A
  field is a tie exactly when the model counts it as a reference: never one
  marked [<NotReference>], never one of a built-in type such as int64. Not
  exactly one such field is refused, naming them. A reflected function is for
  a relationship that says more than the tie, built on it
  (`referring.feature`).

**Writes** (only through `repo.Transact`):

- `Tx.insert record` (gives it back as stored, with its generated key and what
  it owns), `Tx.update (original, changed)` (only the columns that differ),
  `Tx.delete record`.
- Concurrency: a record with a `version` field is checked by its version; a
  record without one is checked against every column of `original` (`IS NOT
  DISTINCT FROM`) in the same command. A copy that came back from a client is
  read with `Tx.oneAsSeen (query, seen)`, a Conflict there if the stored
  version is no longer `seen`, so nothing is decided on a record the caller
  has not seen. A failed check is `TxFailure.Conflict`.
- Plasm validates no input; the domain's constructors do. A violated
  constraint with PostgreSQL's default name (`<table>_<column>_key`, `_fkey`,
  `_check`) is that field's error, `TxFailure.InvalidChanges (entity,
  errors)`; an owned record's violation names its path (`Lines[0].ProductId`).
  Any other constraint is a database failure.

**Bulk writes** (`BulkWrite<'R>` values, run as `Tx.execute` steps, inspected
with `repo.Explain`):

- `Bulk.update(query, fun o -> { o with ... })`: one `UPDATE`; assigned values
  may be computed from the row; unassigned fields are not written; the version
  is incremented. Returns the row count.
- `Bulk.delete query`: one `DELETE`; returns the row count.
- `Bulk.insert records`: multi-row `INSERT ... RETURNING`, split into several
  commands above 65,000 parameters; an empty list sends nothing.
- `Bulk.upsert(records, on, fun current incoming -> { current with ... })`
  (`ON CONFLICT ... DO UPDATE`) and `Bulk.insertMissing(records, on)`
  (`DO NOTHING`, returning only inserted rows). Conflict targets can be tuples.
- No per-record decision runs; database constraints apply and failures are
  classified.
- Rejected before execution: joins, ordering, paging or grouping in the query;
  assigning the key, version, generated fields or owned fields; changes that
  assign nothing.

**Hand-written SQL:**

- `repo.Sql<'R>($"...")` / `Tx.sql`: rows as a mapped record (with what it
  owns), an unmapped record (fields by name or snake_case name), a tuple
  (columns in order) or a scalar. A value is read only as a type that holds
  it exactly: a bigint 4 or a numeric 3 is an int, a numeric 2.5 or the text
  '42' is a materialisation error, never rounded or parsed (`raw-sql.feature`;
  every read, computed projections too).
- `Tx.executeSql($"...")`: affected row count, as a transaction step.
- The SQL is a `FormattableString`; every interpolated value is a parameter,
  including options, domain scalars and lists (one array parameter for
  `= ANY(...)`). A plain string does not type-check.
- `Raw.fragment<'T> "{0} ILIKE {1}" (c.Name, pattern)` inside typed queries; the
  SQL is an F# format string, so only a literal compiles.
- Missing columns and wrong tuple arity are materialisation errors that name
  the field.

**Prepared queries:**

- `Query.prepare` / `prepare2` / `prepare3 (fun (c: Arg<CustomerId>) -> plasm {...})`
  returns a function to `Query<'T>`; `prepare` and `prepare2` also take a
  builder that ends in `select` and return a function to `Projection<'R>`. The
  query is built once and planned once per repo; a call only binds arguments.
- Arguments work in filters, comparisons, `List.contains`, string patterns,
  inside `exists`, and in `take` / `skip`. Bound queries work with every read,
  `Tx.all` / `one` / `count` and bulk writes.
- Explicit errors: reading `.Value` while the query is built, ordering by an
  argument, arithmetic on an argument in `take` / `skip`, negative counts, and
  selecting an argument.

**Start-up checks and DDL:**

- `Repo.Start(model, provider, queriesIn = assembly, ?options)` is the one
  way to get a repository. It translates every module value of type
  `Query<_>` or `Projection<_>` and every prepared query the assembly defines,
  without running them, and runs the schema check below; `RepoOptions.Queries`
  and `RepoOptions.Schema` say what to do with what is found: `Crash` (the
  default) raises one `PlasmStartupException` listing every problem, queries
  by name then schema differences, with the counts first; `Warn` reports each
  through `RepoOptions.OnProblem` (standard error when None) and starts;
  `Ignore` does not check. An assembly with no query values is a problem: a
  check that checked nothing does not pass. Functions returning a query are
  counted and named in a note, never a problem. `RepoOptions.Unchecked` is
  Default with both written as Ignore (scripts, benchmarks). The constructor is
  internal (`query-check.feature`).
- `repo.CheckSchema()`, at start-up through `Repo.Start` and again whenever
  asked (after a migration, say), reports every `SchemaIssue`: missing tables and columns
  (join tables included), incompatible column types, non-optional fields over
  nullable columns, NOT NULL columns the model never writes, enum label
  mismatches, a `unique` field without a unique constraint, a reference without
  a foreign key (both sides of a join table), and a unique field or reference
  held by a constraint under another name (`MisnamedConstraint`, with the
  `rename` to run). A missing index is not reported: it is a matter of
  performance, and a database may have a better composite index.
- `repo.Ddl()` prints the DDL the model expects: enum types, tables (columns,
  NOT NULL, identity keys, unique constraints), join tables, every reference as
  `alter table ... add constraint <table>_<column>_fkey`, and an index
  (`<table>_<column>_idx`) on each owner column of an owned table, each
  join-table target column and each inferred reference, unless the column
  already leads a unique constraint or the primary key (11a6895). Plasm prints
  it and never runs it; keep your migration tool.

**Test sandbox:**

- `repo.Sandbox(fun repo -> task { ... })` gives the body a repository on one
  connection inside one transaction, always rolled back. Transactions inside it
  become savepoints, and each operation runs in its own savepoint, so a database
  error does not end the sandbox. Sandboxes nest.
- Inside a sandbox, isolation levels and snapshot reads see the sandbox's
  transaction (serialisation retries cannot be tested there). A repo call made
  from inside a `Tx.run` step waits for the step and fails after 30 s with an
  explanation; use the step's context.

**F# Interactive:**

- Queries display the SQL they would run, with parameter values and one command
  per owned or linked list, or why they cannot be translated (no setup call).
  Nothing is executed.
- `Plasm.Interactive.wait` awaits a repository call so the result is displayed.
  A `TransactionError` displays as what failed, then where.
- A query or model that uses a type from an earlier submission than the other
  fails with `TranslationError.RedefinedType`, which says which to re-evaluate.
- `repo.Playground()` keeps changes across submissions on one connection and
  transaction and never commits them; `Reset()` rolls them back, `Close()` ends
  it.
- `repo.Scaffold table` returns F# source for a `[<Table>]` record generated
  from the catalogue, its key field named `Id`, with `[<Column>]` where a
  column is not its field's snake case. Owned and linked fields are not
  inferred.

**Transactions:**

- `transaction { }` with `Tx.insert` / `update` / `delete` / `all` / `one` /
  `count` / `execute` / `sql` / `executeSql` / `run` / `traverse`, isolation
  levels, and an opt-in `RetryPolicy` (for example
  `RetryPolicy.OnSerializationFailure`).
- A `Tx<'T, 'E>` gives a `'T` or fails with `TransactionError<'E>`, whose
  `Failure` is a `TxFailure<'E>`: `Rejected of 'E` (the transaction's own
  reason, from `Tx.fail`, `Tx.orReject`, `Tx.ofResult` or a `Tx.run` Error),
  `InvalidChanges (entity, errors)`, `Conflict (entity, key)`, `Failed of DbError`, or
  `Misused` (a `Tx.run` command tried to control the transaction).
- Steps have no names: each is found by the file and line it is written on, and
  a failure reports that `TxStep` and the steps rolled back before it.
- A `Tx.run` step gets `TxContext.CreateCommand()` (enlisted in the
  transaction, refusing transaction-control statements) and the cancellation
  token; the connection and transaction are not exposed.

**Failures:**

- Reads return `Result<_, DbError>`: `Translation`, `Database` (classified by
  `DbFailureKind`), `Materialization`, `MoreThanOneRow`. A repository used
  after its sandbox ended or its playground was closed returns
  `DbFailureKind.RepositoryClosed`. Exceptions remain for programming errors
  found before any database work, and for cancellation.
- An operation the provider cannot perform is
  `TranslationError.UnsupportedByProvider`, naming every missing capability.
- Every public union with common case names is qualified (`TxFailure`,
  `DbError`, `TranslationError`, `DbFailureKind`, `FieldErrorKind`,
  `SortDirection`, `LoadStrategy`, `ReadConsistency`).

**Providers and execution:**

- Database-specific SQL comes from `ISqlDialect` or is guarded by a capability
  (`ArrayParameters`, `Returning`, `Upserts`, `RowLocking`, `WritersTakeTurns`,
  `SnapshotReads`, `EnumTypes`, `ArrayColumns`). A missing capability is
  refused before execution, or when the repository is made. The full check
  holds the core to it (`tests/Plasm.Boundary/check.sh`): it searches
  src/Plasm for PostgreSQL-only constructs and fails on any that
  `allowed.txt` does not name with its reason, recursive queries' CYCLE the
  one not yet behind the dialect.
- Diagnostics callback (`RepoOptions.OnCommand`: SQL, duration, rows; parameter
  values only with `IncludeParameterValues`), cancellation through to the
  server, classified failures.

## Measured performance

Full method and limitations are in `docs/benchmarks.md` (measured in the one
style: an Order owns its lines; payments are read by a second prepared query).
Apple M4 Max, PostgreSQL 18.4 in a local container, 10,000 orders × 10 lines ×
10 payments, EF Core 11 (rc.3 provider) with `AsNoTracking`.

- **Command count is constant in the number of parents:** 3 commands (orders,
  their lines, their succeeded payments) for 20, 100 and 1,000 orders.
- **No cartesian product.** One order with 10 lines and 10 payments transfers
  21 rows; EF's single query transfers 100.
- **A page of 1,000 orders** took 14.5 ms and 8.8 MB, against 21 ms and 7.7 MB
  for EF split query and 66 ms and 21 MB for EF single query, after scalar
  conventions were compiled once per type (dc6832f). Before that fix the same
  page took 108 ms and 69 MB.
- **Small reads.** One order with its lines and payments, queries built per
  call: 0.69 ms and 342 KB (EF split 0.45 ms and 97 KB); prepared: 0.38 ms and
  74 KB.

These are medians from a machine shared with other work; times varied by up to
40% between runs, allocations and command counts did not.

## Gaps and limitations

**Not built, and with no API.** Absent features are not partly supported:

- Providers other than PostgreSQL (`Plasm.PGlite`, used by the browser koans,
  is a spike and is not packaged).
- Migrations: Plasm prints the DDL the model expects and checks the database
  against the model, but generates no migration. Keep your migration tool.
- Right and full outer joins, joins on non-equality conditions, joins to
  subqueries.
- Subqueries in `select`, set operations (UNION, INTERSECT, EXCEPT), CTEs and
  window functions (use `Raw.fragment` or `repo.Sql` / `Tx.sql`).
- Join tables with extra columns (model the link as its own record).
- Bulk writes over joins (`UPDATE ... FROM`), streaming (`IAsyncEnumerable`)
  results.
- `Query.prepare3` for projections.

**Limits of the 2026-11-15 additions** (loads, `selectDistinct`, values,
documents), each refused by name rather than guessed:

- `selectDistinct` with `take` or `skip`: in the query they come first and
  would page the rows, while the database pages the distinct values. A paged
  list of distinct values is a real need, to be revisited with a real case.
- Loads after a `join` or `leftJoin`; a tie of more than one column
  (composite); `orderBy`, `take` or `skip` on what `referredBy` finds (they do
  not compile: one record has no order). A field of `return` computed from a
  load is refused: compute it from the rows read.
- In a value: `[<Unique>]`, `[<Column>]`, a list, or a stored record.
- In a document: a union with fields and a `Map`; reading a field of an
  optional document in a query (test `IsSome` and read the field from the
  records); `List.contains` with a prepared argument over a document list.

**Known limitations:**

- An optional union with fields is refused; give the union a case for absence.
  A case field must be a scalar or another union. A union's columns cannot be
  renamed one by one (they follow the field's column). Selecting a whole union
  in a projection is not supported; select the record, or a member that names
  its case or reads a case field.
- Grouping by a union's case needs a member that names each case (a `match`
  that produces a value): F# hides the compiled `Tag` property.
- A transaction value that never rejects leaves `'E` open, so a bare single
  write needs its type written: `repo.Transact(Tx.insert ada: Tx<Customer,
  unit>)`. Stored untyped as a top-level value it becomes `obj`. Transactions
  written as functions with a declared return type need nothing.
- A query that binds loads reads its records and what each load finds
  in separate commands, outside a snapshot (`ReadConsistency.Snapshot` is not
  offered for projections): a write committed between them can be seen by the
  second and not the first, as for what a record owns. Read it with `Tx.all`
  inside a transaction when that matters.
- A document is read strictly and written whole, so its shape changes need a
  data migration first: before removing a field from the record, remove the
  key from the stored documents (`update t set col = col - 'key'`); before
  adding a field that is not optional, add the key to them. A tolerant read
  would let the next write delete keys another writer added.
- An `int64` in a document (a typed id) is a JSON number: exact in
  PostgreSQL's jsonb, but not in a JavaScript reader beyond 2^53.
- What a record owns or links is always read with it: every query of the owner
  pays one more command per list. A relationship read only sometimes is
  another aggregate, reached by a query.
- Violations are mapped to fields by constraint name. A constraint under
  another name stays a database failure (`TxFailure.Failed` with
  `DbFailureKind.UniqueViolation` / `ForeignKeyViolation` / `CheckViolation`);
  `CheckSchema` reports a misnamed unique constraint or foreign key, but check
  constraints are not stated in the model and are not checked.
- Only queries defined as module values are checked by `Repo.Start`; a query
  built inside a function is checked when it is first used (`Repo.Start` says
  how many such functions there are). Bulk writes and transaction steps are
  not checked at start-up.
- `referring` and `referredBy` take the other record as `obj`, so the call
  needs one type argument and no `box`; F# cannot infer a second type argument
  when the first is written. A value that is not a stored record (an
  `Order option`, a number) compiles, and is refused when the query is
  translated, naming its type: at start-up by `Repo.Start` for a module
  value, otherwise at first use. Not at compile time.
- Plasm depends on FSharp.Core 8.0 or later (`FSharp.Core >= 8.0.403`), so it
  loads into hosts with an older FSharp.Core than the SDK's. The packages are
  built against that floor (`PlasmFSharpCoreFloor`) as a courtesy: a C# project,
  which has no FSharp.Core of its own, resolves the floor, and an F# project
  keeps its own. Everything that runs Plasm as an application here, the suites,
  the native check, the koans, the package check's consumers and the browser
  koans, runs on the .NET 11 SDK's FSharp.Core, 11.0.101, as a new project does.
  Kanban's F# server, client and shared code run on 10.1.202, the newest
  Fable.Remoting 6.1 allows; its C# host and Lambda, which do not reach
  Fable.Remoting, on 11.0.101, each checked by a scenario against what its
  build resolved.
- `DateTime` fields mapped to `timestamptz` must be UTC; Npgsql enforces this.
- The PostgreSQL error-detail text is redacted by Npgsql unless
  `Include Error Detail` is set. Plasm reports SQLSTATE, constraint name and
  message.
- With `<WarnOn>1182</WarnOn>` (unused variables) enabled, F# reports the row
  variable of `for o in ... do` as unused, because its only uses are inside
  quotations.
- A `join` cannot be the last clause of a `plasm { }` expression; follow it
  with `where`, `orderBy` or `select` (F# rule FS3098). A key type mismatch in
  `join` is reported as FS0041 because `join` is overloaded; the message names
  both key types.
- `Tx.run` steps can perform arbitrary effects; only database work through the
  given context is rolled back. Their commands refuse statements that start
  with COMMIT, ROLLBACK, END, BEGIN, START, ABORT, SAVEPOINT or RELEASE; a
  semicolon inside a string literal can make the check refuse a harmless
  command, never let a control statement through.
- Displays of query values use the most recently created live repository that
  maps the query's type, and show parameter values only when that repository
  includes them (`IncludeParameterValues`) or the types were defined in F#
  Interactive.
- The schema check and scaffolding read `information_schema` with
  `current_schema()`, and link writes use `unnest`; a second provider would
  need these behind the provider too.
- `TxStep.File` is the path the compiler saw. Build with
  `<PathMap>$(MSBuildProjectDirectory)=/src</PathMap>` (or deterministic CI
  builds) to keep a build machine's directories out of logged failures.
- Rows from a multi-row `INSERT ... RETURNING` are returned in insertion order.
  PostgreSQL does this in practice but does not formally guarantee it.
- `take` and `skip` quote their argument, so a count that is not a literal is
  evaluated with the F# quotation evaluator (a few microseconds).
- Grouping and fragment errors are found when the query is planned, before any
  command is sent, rather than at compile time.

## The one-style ledger

On 2026-11-14 the user decided that Plasm has one way to build an application
("There should not be two styles"). This ledger records what that cost.

### What left the original specification

`features/core.feature` and `features/elegance.feature` are the user's
specification; the originals are kept verbatim in `docs/specs/original/`. The
one style changed them as follows (each note is also in the feature file and in
`docs/acceptance.md`).

**Background (step a1, 15e2297).** "an Order belongs to a Customer", "has many
OrderLines", "has many Payments" and "an OrderLine belongs to a Product" became
"an Order refers to its Customer by id", "an Order owns its OrderLines", "a
Payment refers to its Order by id" and "an OrderLine refers to its Product by
id".

**Step a1 (15e2297): relationships became owned records and references.**

- Rule renamed: "Relationship loading is explicit, composable, and batched"
  became "An aggregate is loaded whole, and other aggregates are reached by
  query".
- Removed: "Unloaded and empty relationships are distinguishable", "Nested
  loads compose", "A child filter does not implicitly filter parents",
  "Per-parent limits preserve their stated meaning", "Unsupported loading
  semantics are rejected", "Conflicting relationship customizations are
  explicit" (the one scenario that had been deliberately changed) and "Loading
  onto existing values preserves the originals".
- Steps rewritten, titles kept: "Relationship loads are checked against the
  source type", "Reading a relationship never triggers hidden database access",
  "Sibling collections load without a cartesian product", "Relationship loading
  batches across parents", "Pagination applies to parents before collection
  expansion", "Parent filtering through relationships is explicit" and
  "Filtered relationship queries are reusable".
- Rule renamed: "Association writes have explicit lifecycle semantics" became
  "Writing an aggregate writes what it owns". Title rewritten: "Removing
  related records requires a configured policy" became "Removing an owned
  record deletes it" (ownership is the policy). Steps rewritten: "Omitting an
  association does not modify it" and "Updating an association matches
  existing identities".
- `elegance.feature`: removed "Filtered collections are not authoritative
  replacements" (an owned list is always loaded whole). Steps rewritten:
  "Optional relationships preserve parent rows" (a left join),
  "Duplicate child identities are rejected" and "Loading plans can be
  inspected". Reworded: "Domain scalar wrappers preserve their meaning" (stored
  by convention, not by a configured mapping).

**Step b (1cf5872): Plasm validates no input.**

- Rule renamed: "Changes are explicit and validation is inspectable" became
  "Changes are explicit and the database's refusals are inspectable".
- Removed: "Invalid changes can be inspected without execution" and "External
  input cannot change protected fields".
- Steps rewritten: "Invalid changes are not persisted" (a change the database
  refuses comes back as the field's errors, and nothing is written).

**Step d (84b971b): one write path.**

- Title rewritten: "Creating a changeset preserves its input" became "Creating
  a change preserves its input" (the change is written, and the stored Order
  compared).
- Reworded: "Unspecified and explicitly cleared values differ" ("a changeset"
  became "a change") and "Database constraints produce structured errors" (the
  model states that the email is unique).

**Before the one style (strict design, step 2, 0b9a97e).** "Named transaction
steps compose" became "Transaction steps compose", and "A failed transaction
identifies its failed step" says where the step is written: steps have no
names.

**Outside the original specification.** `fsi.feature` lost "Relationships that
were not loaded display as not loaded" (step a1); `tx-steps.feature` lost "A
step whose changeset is for a different action is rejected" (G3) and "The
repository rejects a mismatched changeset the same way" (step d). Three
features were deleted whole: `has-one.feature` and `many-to-many.feature` (step
a1; their behaviour is in `owned-records.feature` and
`linked-collections.feature`) and `query-pipeline.feature` (step c).

### Two capabilities dropped, then restored in another form

Both were dropped with the second style (2026-11-14) and came back on
2026-11-15, at the user's request, as `including`, replaced on 2026-11-17 by
loads bound with `let!` (`loads.feature`): the loaded records are in the
row's type, not in a field that may be unloaded.

- **Choosing per query which relationships to load** (`preload`,
  `replacePreload`, `repo.Preload`). An aggregate still always loads whole.
  Another aggregate's records come with a `let!` in the query, in the field
  of its name of the row `return` builds: one more command per batch of outer
  values.
- **Top-N children per parent** (`limitPerParent`). `take` inside a load's
  query applies per outer record, with the order it requires
  (`ROW_NUMBER() OVER (PARTITION BY ...)`).

### Still unproven

- Nothing has run in production.

## Changed

Changes that break code written against an earlier alpha, newest first.

- 2026-11-21: `ProviderCapabilities` has a field more, `IsolationLevels`: the
  levels a transaction can run at. `Repo.Transact` at another is
  `TxFailure.Failed (DbError.Translation (UnsupportedByProvider ("Chaos
  isolation", provider)))`, nothing sent, where it raised the driver's
  exception (Npgsql's NotSupportedException for Chaos on PostgreSQL; SQLite
  has neither Snapshot nor Chaos). A custom provider's capabilities list
  its levels; one that wraps another's copies them (`{ inner with ... }`
  does).
- 2026-11-21: `ICatalogueDialect` has a member more, `Checks tables`: SQL
  giving each table and the text its named CHECK constraints are written in,
  `constraint <name> check (...)`, which the schema check reads the column
  checks Ddl writes from (column-checks.feature). A catalogue whose Ddl
  writes no column check, as PostgreSQL's, gives no rows: `SELECT NULL::text,
  NULL::text WHERE false`. A catalogue that wraps another passes its `Checks`
  on.
- 2026-11-21: `ISqlDialect` has a member more, `Cycle: CycleCheck`: how a
  recursive query leaves out the rows that close a cycle. A dialect with
  PostgreSQL's clause gives `CycleCheck.Clause (fun key flag path -> "CYCLE "
  + String.concat ", " key + " SET " + flag + " USING " + path)`; one without
  gives `CycleCheck.Path (start, extend, holds)`, writing a path of the keys
  found, as SQLite's does with JSON (recursive-queries.feature). A dialect
  that wraps another passes its `Cycle` on. PostgreSQL's SQL is the same.
- 2026-11-21: `DbFailure` has two fields more, `Table` and `Columns`: the
  table a violated constraint is on and the columns a violated unique
  constraint or foreign key holds, where the database gives them (SQLite
  gives them, naming no such constraint; PostgreSQL gives the table and the
  name, and Columns is empty). Code that builds a `DbFailure`, a custom
  provider's `Classify` or a test double, adds `Table = None; Columns = []`
  (or what its database says). Code that reads one is unchanged. A violation
  is the field's error by the constraint's name, as before, or, where no
  name is given, by its table and columns (constraints.feature).
- 2026-11-21: Only UTC times are stored, and nothing a provider throws while
  sending a value leaves Repo, Transact, Stream or a prepared query as an
  exception. Every time Plasm stores, a column's or one anywhere inside a
  `[<Json>]` document, is an instant in UTC: a DateTime whose Kind is not
  Utc, or a DateTimeOffset whose offset is not zero, is refused before
  anything is sent: a write as `TxFailure.InvalidChanges` naming the field
  (kind `Custom "utc"`, and a document's path, `at [0].note.seen in the
  document`), and a
  query, stream, bulk write, prepared query's argument (a list's elements
  too) or hand-written SQL's value as `DbError.Translation`. A value the
  driver cannot send is `DbError.Database` (kind `OtherDatabaseError`), and a
  hand-written SQL value of a type Plasm does not map is
  `DbError.Translation (UnmappedType)`, where both were exceptions.
  Upgrading from 0.1.0: `DateTime.Now`, `DateTime.Parse` without
  `DateTimeStyles.AdjustToUniversal`, and `DateTime(y, m, d)` are now
  refused, where 0.1.0 stored them shifted by the server's time zone: Npgsql
  sent them as a timestamp without a time zone, which the server read in its
  own (08:00 was stored as 07:00Z on a server in Paris); inside a document,
  0.1.0 read such a DateTime as the client machine's local time and stored it
  shifted by that machine's zone instead, and kept a DateTimeOffset's offset.
  Read back, a document's DateTime is of Kind Utc and its DateTimeOffset at
  the offset zero, as a column's are. Rows written that
  way are off by the server zone's offset at that instant (`SHOW timezone`
  says the zone; `now() - now() AT TIME ZONE 'UTC'` its offset now); find
  them by when and where they were written, and correct them by that offset.
  Nothing rewrites stored data. Give times Kind Utc (`DateTime.UtcNow`,
  `DateTime.SpecifyKind`, `ToUniversalTime()`), or offset zero.
- 2026-11-21: `ISqlDialect.LikeEscape` is gone, and `ISqlDialect.TextMatch`
  writes `String.StartsWith`, `EndsWith` and `Contains` in a query: told the
  `TextMatchKind`, it writes the operand and its own pattern (`pattern
  shape`, the text's parameter as `shape` makes it), matching the text with
  its case and every character as itself. PostgreSQL's is the `LIKE` it was,
  so its SQL is the same. A dialect written against 0.1.0 writes `operand
  LIKE` and the text escaped (`\`, `%`, `_`), with `%` where the match is
  open, and its old escape clause after.
- 2026-11-21: `ISqlDialect` has a member more, `Refusal: obj -> string
  option`: why the database cannot hold a value Plasm would send (a column's,
  a list's, a prepared argument's), asked before anything is sent. A write's
  refusal is `TxFailure.InvalidChanges` naming the field (kind `Custom
  "unstorable"`), a query's `DbError.Translation`. A dialect written against
  0.1.0 answers `None` for every value to keep what it did.
- 2026-11-21: `ISqlDialect` has two members more, `ListHas` (a list column
  holds an element: `element = ANY(column)` on PostgreSQL) and `ListIsEmpty`
  (`cardinality(column) = 0`). `ColumnType` and `CatalogueTypes` are now asked for a list
  column's array type itself (`string[]`), where before the core asked for its
  element's and wrote `[]` after it: a dialect with array columns answers for
  arrays. Breaks a dialect written against 0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `Exact`, an `IExactDialect
  option`: how a dialect whose database does not compare, order and add its
  stored decimals by value makes them exact (`Compare`, `NullSafe`,
  `Arithmetic`, `Aggregate`, `WindowAggregate`, `Ordered`, and `Value`, what a
  decimal is sent as). None for PostgreSQL, whose numeric is exact. Breaks a dialect
  written against 0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `Ddl`, an `IDdlDialect`: the
  reserved words Ddl quotes, a generated key's clause, a store type's column
  type, an enum type's statement, a column's check, and whether foreign keys
  are declared in their tables. A column's check is one member,
  `ColumnCheck(column, holds)`, told what the column holds as a
  `ColumnHolds`: `Scalar` of its provider type (a time's form), `List` of its
  element type, `Document`, or `Enum` with its labels; None where the
  column's type holds it, which is every answer on PostgreSQL. What a later
  check needs is said by `ColumnHolds`, not by another parameter. Breaks a
  dialect written against 0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `Catalogue`, an
  `ICatalogueDialect`: the queries the schema check and scaffolding send to
  the database's catalogue (columns, the current schema, enum labels, unique
  indexes, references, primary and foreign keys, one table's columns), each
  reading the columns its documentation states. Breaks a dialect written
  against 0.1.0.
- 2026-11-21: `ISqlDialect` has two members more, `NotInList` (`<> ALL(@p)`
  on PostgreSQL) and `ListRows` (lists read as a table's rows, `unnest(...)
  AS "u"("k")`), and a dialect without `ArrayParameters` now writes links,
  as its own lists send them, where before it was refused. Breaks a dialect
  written against 0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `RowsIn`: rows of several
  columns among the rows that lists, one a column, make (`(a, b) IN (SELECT
  * FROM unnest(@p0, @p1))` on PostgreSQL). Breaks a dialect written against
  0.1.0.
- 2026-11-21: `ISqlDialect` has two members more, `ListValue` (the value a
  list is sent as, as one parameter; the array itself on PostgreSQL) and
  `InList` (membership in a list sent so: `= ANY(@p)` on PostgreSQL). Breaks
  a dialect written against 0.1.0.
- 2026-11-21: `ProviderCapabilities` has a field more, `WritersTakeTurns`:
  one writer holds the database at a time, so a write that replaces what a
  record owns reads it without a row lock. A provider that builds the
  record gives it; PostgreSQL's is false. Breaks a provider written against
  0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `EmptyArray` (an empty
  array's literal, a list aggregate's value over no rows; `'{}'` on
  PostgreSQL), and aggregate and window function names now pass through
  `FunctionName` as other functions' do: a dialect that renames a function
  by its upper-case name sees `COUNT`, `SUM`, `BOOL_OR`, `ROW_NUMBER` and the
  rest. Breaks a dialect written against 0.1.0.
- 2026-11-21: `ISqlDialect` has two members more, `StoreCast` (the type a
  value is cast to, to be stored as or compared with a database type of its
  own, or None to send it as it is) and `TextCast` (the type such a column is
  read as text through, or None), and `IJsonDialect` one, `StoreType` (the
  database type a document is stored as). Breaks a dialect written against
  0.1.0.
- 2026-11-21: `IProvider` has a member more, `Describe: DbCommand *
  CancellationToken -> Task<DescribeOutcome>`, which describes a statement
  without running it for the statement check (`Described`, `Refused of
  DbFailure`, or `CannotType` when a parameter's type cannot be told). It is
  asked only of a provider whose dialect's `Capabilities.Describe` is true;
  one that cannot describe may raise. A provider that wraps another
  delegates it. Breaks a provider written against 0.1.0.
- 2026-11-21: `ISqlDialect` has a member more, `UnboundedLimit`: what LIMIT
  takes for no limit when the dialect needs a LIMIT before an OFFSET (None
  for PostgreSQL). Breaks a dialect written against 0.1.0.
- 2026-11-21: `ProviderCapabilities` has two fields more, `EnumTypes` and
  `ArrayColumns`: whether a field can be stored as a database enum type
  (`Scalar.asPgEnum`) and a list of scalars as one array column. A provider
  that builds the record gives both; one that copies another's (`{ caps with
  ... }`) keeps theirs. Breaks a provider written against 0.1.0.
- 2026-11-21: `ISqlDialect.ColumnTypes` is three members, one for each of
  its uses: `ColumnType` (the type Ddl declares a column as), `CatalogueTypes`
  (the names the schema check accepts and scaffolding maps back) and
  `CastType` (the type a JSON scalar is cast to). A custom dialect gives
  the three; for PostgreSQL's types, `ColumnType` and `CastType` are the
  first name the old list gave, and `CatalogueTypes` is the list. Breaks a
  dialect written against 0.1.0.
- 2026-11-20: `TxFailure.Conflict` carries the stale record's key as well as
  its type's name: `Conflict of entity: string * key: obj`, the key being the
  value of its key field (its id, or the key record of a composite key). A
  match `TxFailure.Conflict _` still compiles; `TxFailure.Conflict entity`
  becomes `TxFailure.Conflict(entity, key)`, and code that builds a Conflict
  gives the key. Breaks code written against 0.1.0-alpha.2 and earlier.
- 2026-11-20: `CommandEvent` has a field `Error` (the SQLSTATE, or the
  exception's type, of a command that failed); code that builds a
  `CommandEvent` itself gives it.
- 2026-11-20: Repo.Start has the database describe every statement the
  modules define (statement-check.feature), so a module-level query, bulk
  write or FormattableString the database refuses now fails start-up under
  `Queries = Crash`. `StartupProblem` has two cases more, `Refused` and
  `NotDescribed`; a `match` over every case needs them.
- 2026-11-20: `DbFailureKind` has a case `LockNotAvailable` (a NOWAIT row
  lock on a row another transaction holds). A `match` over every case of it
  needs one more arm.
- 2026-11-20: `Tx.traverse f items` applies `f` to every item when the
  traverse is built, not as each item runs. Descriptions are values, so this
  matters only for an `f` with side effects of its own.
- 2026-11-19: a typed id among a value's fields (a record with no table,
  stored in its owner's row) is a reference, named by its path (`Home.City`).
  A database whose column for it has no foreign key now fails the start-up
  check by default; add the key Plasm's DDL prints, `<table>_<column>_fkey`.
- 2026-11-19: an array column's refusal names its type as F# writes it
  (`Stage list`, not `FSharpList<Stage>`).
- 2026-11-19: a typed id among a union case's fields is a reference
  (union-references.feature). A database whose column for it has no foreign key
  now fails the start-up check by default, as a MissingReference on
  `<Field>.<case field>`. Add the key Plasm's DDL prints,
  `<table>_<column>_fkey`, or start with `Schema = Warn` until it is added.
- 2026-11-19: a model with a wrapper type whose `create` is not
  `'P -> Result<'T, string>` is refused when it is built, saying why. Before,
  such a public type was read past its `create`, unchecked. Give `create` that
  signature, or register a mapping.
- 2026-11-19: `RepoOptions.OnCommand` reports a transaction's own commands
  as well as its statements: BEGIN, COMMIT, ROLLBACK and savepoints, with
  `CommandEvent.Kind` `Begin`, `Commit` or `Rollback`. A subscriber that counts
  commands keeps `Kind = CommandKind.Statement`. A dialect of your own
  implements `ISqlDialect.Begin` (the BEGIN it sends at each isolation level)
  and sets `ProviderCapabilities.DefersBegin`.
- 2026-11-19 (bfc9bda): `Tx.update (original, changed, expectVersion = seen)`
  is gone. Read a caller's copy with `Tx.oneAsSeen (query, seen)`, a Conflict
  there if the stored version is no longer `seen`, and update what it gives
  with `Tx.update (original, changed)`.

## Decision record

| Date | Decision | Where |
|---|---|---|
| 2026-11-12 | Adopt Ecto's responsibilities; express queries as quotations in a computation expression; ADO.NET with Npgsql directly; EF Core only as a benchmark baseline | ADR 0001 |
| 2026-11-12 | `Assoc<'T> = NotLoaded \| Loaded of 'T`, with the payload encoding cardinality (superseded 2026-11-14: removed) | ADR 0001 |
| 2026-11-12 | `take` in a load query is rejected; `limitPerParent` uses a window function; unsupported providers reject it (superseded 2026-11-14: removed) | ADR 0001 |
| 2026-11-12 | Load conflicts are reported at plan time (total combination); `replacePreload` resolves them (superseded 2026-11-14: removed) | ADR 0001 |
| 2026-11-12 | Child replacement reads the database's children in scope, `FOR UPDATE`; the default `OnReplace.Reject` (superseded 2026-11-14: an owned list is written by difference) | ADR 0001 |
| 2026-11-12 | BDD with Reqnroll: a C# host generates the tests from `features/*.feature`, and every step is in F# | `tests/Plasm.Specs*` |
| 2026-11-12 | Union cases with common names are qualified (`DbFailureKind`, `FieldErrorKind`, `ReadConsistency`, and `WriteError` until it was removed) after domain unions shadowed them in tests | API review |
| 2026-11-12 | No overloads on `Changeset` statics (superseded 2026-11-14: `Changeset` is internal) | API review |
| 2026-11-12 | Joins: `join` / `leftJoin` custom operations with quotation keys; left-joined sources are `'T option` with option semantics in SQL; joined queries run as one command (since 2026-11-14 a whole owner in a joined row comes with what it owns) | `features/joins.feature`, ADR 0001 |
| 2026-11-12 | Plans cached per repo per query value unless translation evaluated a volatile expression; compiled delegates for record construction | `docs/benchmarks.md` |
| 2026-11-12 | Aggregates are typed functions (`Agg.*`) inside the query; sum of nothing is 0, min/max/avg are options; grouping mistakes are planning errors | `features/aggregates.feature`, ADR 0001 |
| 2026-11-12 | Bulk writes are `BulkWrite<'R>` values; updates are copy-and-update lambdas and increment versions (run as `Tx.execute` steps since 2026-11-14) | `features/bulk-writes.feature`, ADR 0001 |
| 2026-11-12 | Hand-written SQL takes `FormattableString` (holes are parameters); fragment SQL is a format string (literal only) | `features/raw-sql.feature`, ADR 0001 |
| 2026-11-12 | Many-to-many: declared with its join table; links written only by putLinks/addLinks/removeLinks (superseded 2026-11-14: `links`, written with the owner) | ADR 0001 |
| 2026-11-12 | No migrations: `repo.CheckSchema` reports drift against the live catalogue | `features/schema-check.feature`, ADR 0001 |
| 2026-11-12 | Test sandbox: one rolled-back transaction, a savepoint per operation and per inner transaction | `features/sandbox.feature`, ADR 0001 |
| 2026-11-12 | Prepared queries: probe `Arg<'T>` values, built once, parameters bound per call; take/skip quote their count instead of overloading (which broke inference) | `features/prepared-queries.feature`, ADR 0001 |
| 2026-11-13 | Architecture audit follow-up: extended prepared queries plan their own description; `not` is two-valued (`IS NOT TRUE`); only immutable captured values allow plan caching; sandboxes nest; qualified error cases and closed/unreadable/unsupported failures as values; `TxContext` without the transaction; complete pipeline module (superseded 2026-11-14); `hasOne` tested with `Rel.exists` (superseded 2026-11-14); dialect hooks and capability checks | the six audit features, ADR 0001 |
| 2026-11-13 | Plasm built against FSharp.Core 8.0 (not the SDK's 11.0): a notebook kernel with FSharp.Core 10 could not load it; the koans notebook now runs in a real kernel in CI | `koans/features/notebook.feature` |
| 2026-11-13 | Second audit follow-up: only immutable module values and captures allow plan reuse; `not` is two-valued only over nullable operands; pipeline joins nest and translate (superseded 2026-11-14); `repo.One` keeps `take` and reports `MoreThanOneRow`; displays hide values unless enabled; prepared load queries bind their own arguments; all public unions qualified; typed `StepFailure`/`Rejection`; one shape for unsupported operations; `Tx.run` commands refuse transaction control | `reading.feature` and new rules in six features, ADR 0001 |
| 2026-11-14 | Strict design, steps 1 to 7 and 9: domain scalars are stored by convention (single-case unions over built-in types, `create` functions, fieldless unions), with registered mappings as overrides; transactions are `Tx<'T, 'E>` with a typed rejection, steps found by file and line instead of names, and `Tx.one`, `Tx.count`, `Tx.orReject`, `Tx.ofResult`; `repo.CheckQueries` translates every query value and prepared query an assembly defines, without running them; schemas state `unique` fields, typed ids are inferred as references, violations map to fields by PostgreSQL's default constraint names, and the schema check reports missing uniqueness and foreign keys; a record without a version field is checked against the record a change was made from (every column, in the same command), and a version field remains the opt-in alternative; a union with fields is stored across columns by convention and queried as F# reads it; a record owns plain lists (`owns`) and links to shared records as plain lists (`links`), always loaded with it, and `List.exists`/`List.forall` over them filter; `repo.Ddl()` prints the DDL the model expects. Step 8 (deleting the old API) was first closed without deletions (next row) and then done, later the same day, as the one style (the row after) | `scalar-conventions.feature`, `typed-transactions.feature`, `query-check.feature`, `constraints.feature`, `concurrency.feature`, `union-fields.feature`, `owned-collections.feature`, `linked-collections.feature`, `ddl.feature`, ADR 0001 amended |
| 2026-11-14 | **Superseded later the same day by the one-style decision (next row).** Step 8 closed without deletions, keeping both styles, the row-record style because the original specification and koans lesson 22 relied on it. What still holds from this decision: `key` and `identityKey` stay separate, because whether the database generates a key is a fact the model needs; the wire shape is the application's own (Plasm has no serialization); the version field and original-values concurrency both stay; there is no fluent surface, no analyzer, no type provider and no migration generation | this section |
| 2026-11-14 | **One style.** "There should not be two styles." G1 (1cf93bd): `exists (plasm { ... })` as a correlated `EXISTS`. 1c5e90a: the schema check reports a misnamed constraint, with the rename. G2 (7e70c31): `ownsOptional` and `ownsOne`. G3 (f09a66e): `Tx.insert`, `Tx.update (original, changed, ?expectVersion)` and `Tx.delete` take records. Step b (1cf5872): Plasm validates no input; `Changeset.validate*`, `cast`, `castWith`, `set`, `clear`, `forKey`, `getField`, `tryGetChange` and `addError` removed. Step c (d6435f7): `plasm { }` is the only query syntax; the pipeline `Query.*` functions and `query-pipeline.feature` removed. Step a1 (15e2297): the test domain in the one style (an Order owns its lines; customers, payments and products referred to by id); a joined or hand-written-SQL record comes with what it owns; an owned item with another owner's key is refused; `has-one.feature` and `many-to-many.feature` folded into `owned-records.feature` and `linked-collections.feature`. Koans, examples, F# Interactive tour and website rewritten (98a60d2 to c884ed7, 24e14e6, e1179ae, 66180ab, 137f385). dc6832f: scalar conventions compiled once per type (reads were 5× slower). ba9f042: the same stored record listed twice in an owned list is refused; `Option` predicates over an owned optional record translate. Step a2 (f37403d): `Assoc`, `NotLoaded`/`Loaded`, `hasMany`/`hasOne`/`belongsTo`/`manyToMany`, `preload`/`replacePreload`/`limitPerParent`, `repo.Preload`, `Rel`, `putChildren`/`replaceChildren`/`putLinks`/`addLinks`/`removeLinks`, `ReplaceScope` and `OnReplace` removed; `AssocKind` internal. Step d (84b971b): `repo.Insert`/`Update`/`Delete`/`Execute`/`ExecuteSql`, `WriteError` and `Tx.write` removed; `Changeset`, `ChangeAction` and `FieldChange` internal; a `TransactionError` displays as what failed and where. 11a6895: `repo.Ddl()` prints an index on each column the model's reads filter on | the ledger above, ADR 0001 amendment, `docs/acceptance.md` |
| 2026-11-14 to 15 | **A record says how it is stored.** The user asked why a `Card` needs a `schema<Card>` beside it. 567f734: a typed id that is the key of two schemas is a model error. Step a: `Plasm.Annotations` (attribute classes only, no dependencies, compiled by Fable from its sources) with `[<Table>]`, `[<Unique>]`, `[<Version>]`, `[<Generated>]`, `[<Assigned>]`, `[<NotReference>]`, `[<OrderedBy>]` and `[<JoinTable>]`; `Model.build scalars [ typeof<...> ]` derives the model from the types, owned records coming with their owner; `tableOf<'T>` is the query source; `schema { }` stayed as `Model.buildSchemas` until everything was converted. Every table name is written (a join table's too); every column name is derived. **Reversal:** key versus identityKey was called a fact to state; the key's generation is now read from its type (an integer key, or a typed id over one, is generated by the database; any other key is assigned), with `[<Assigned>]` for an integer key the application assigns. A `[<Generated>]` column's default is not printed by `repo.Ddl()`: the model cannot know it. Step b: the test domain and every step file in the attribute style, `repo.Scaffold` prints a `[<Table>]` record, and `[<Column>]` was added for a column that is not its field's snake case (a scaffolded table's key column, say), cc3d0ec. Step c (e480562): the koans, lesson 05 now About stored records; `stored<'T>` renamed `tableOf<'T>`, because a local named `stored` shadowed it eight times in our own code. Step d (9b03362): README, ADR 0001 amendment of 2026-11-15, comparisons, website, examples and the F# Interactive tour. Step e: `schema<'T> { }`, `SchemaBuilder`, every schema operation and `Model.buildSchemas` removed; the query source's type is `Table<'T>`; `ISchema` is internal. Kanban was converted by the architecture session at each step (bc974d8, be6c6d7) | `features/stored-records.feature`, `src/Plasm/Stored.fs` |
| 2026-11-15 | `including (plasm { ... })`: another aggregate's records in the result's type (`Order * Payment list`), tied by one equality, read with one more command per batch, `take` per record; restores per-query loading and top-N per parent without field state, at the user's request | `features/including.feature`, ADR 0001 amendment |
| 2026-11-15 | `selectDistinct` as a second terminal operation (not a `distinct` flag, which could only be refused at translation); ordered outside the distinct rows; `take`/`skip` with it refused in v1 | `features/distinct.feature` |
| 2026-11-15 | A record with no table and no Id is a value in its owner's row (`<field>_<subfield>`); an optional one is all or nothing, printed as a CHECK by `Ddl()`; a registered scalar mapping over a record still wins | `features/value-records.feature` |
| 2026-11-16 | Several `including`s (up to eight, a flat tuple in the order written, at the user's request: "We can only do one including whereas EF Core can do as many includes as it wants?") and an inner query ending in its own (EF's `ThenInclude`); a tie may compare an optional column with `Some` of a plain one | `features/including.feature` |
| 2026-11-16 | `includingOne`: the one record a tie can find, as an option, only when the tie proves at most one (the inner key or a unique field), never checked at run time; chained sources take any element type, so it mixes with `including` under the one limit of eight | `features/including.feature` |
| 2026-11-16 | The limit of side-by-side `including`s and `includingOne`s raised from eight to sixteen at the user's word ("Raise it to 16."): 64 overloads; type checking measured unchanged within the spread (60 queries: median 2.64 s at 8, 2.86 s at 16); only an inner that is not a query gives a longer error | `features/including.feature` |
| 2026-11-16 | **Reversal** (strictness review): `includingOne` took the first of several matches when the database broke the uniqueness the tie proves; a second match is now a materialization error naming the records and the tie, as for an owned optional record | `features/including.feature` |
| 2026-11-16 | **Reversal** (strictness review): `referring` and `referredBy` chose the tie by scalar type alone, so a field marked [<NotReference>], or any lone int64 beside an int64 key, became the tie; the tie is now a field the model counts as a reference to the keyed record | `features/referring.feature` |
| 2026-11-16 | Strictness review: a captured null of a type that is not an option became `IS NULL` in an equality, and a null prepared argument `= NULL` (so `<>` found no rows where F# finds every one); both are refused, as string patterns and membership already were | `features/query-semantics.feature` |
| 2026-11-16 | Strictness review: clauses were flattened, so `take` then `where` filtered before the limit, a second `take` replaced the first, `take` then `skip` read SQL's page rather than F#'s, and `where` after `groupBy` stayed a WHERE; each is now a problem recorded as the query is built and refused when it is planned (`QueryDef.Problems`, never filled before). A paged type that makes these compile errors is a separate question for the user | `features/query-semantics.feature` |
| 2026-11-16 | Strictness review: reads converted with `Convert.ChangeType`, so a numeric read as an int was rounded and text was parsed into a number; a value now converts only when converting back gives it again (`Reflection.exactly`), otherwise a materialisation error naming both types | `features/raw-sql.feature` |
| 2026-11-16 | Strictness review: an attribute where Plasm does not read it was dropped silently; the model is now refused, naming it: `[<Table "">]`; `[<Assigned>]` off the key or on a key that is not an integer; `[<OrderedBy>]` off a list; `[<JoinTable>]` off a list of stored records or on an owned list; `[<NotReference>]` on a field of no record's key type; `[<Unique>]` or `[<Generated>]` on a union with fields, a value, a `[<Json>]` field or a list of stored records; and every attribute inside a value, as `[<Column>]` already was | `features/stored-records.feature` |
| 2026-11-16 | Strictness and elegance reviews: refusal messages and doc comments that named removed API now name what exists: a plain list says to mark it `[<Json>]`, two fields of one column are named, an owned record found twice says so, a key-only versioned change points to `Tx.update`'s `expectVersion`; the doc comments of `Tx`, `transaction`, `BulkWrite`, `repo.All`, `repo.Scaffold`, `referring`/`referredBy` and the misplaced `CheckSchema` summary; README's scenario count | `features/stored-records.feature` |
| 2026-11-16 | Two loads of one type side by side are refused at translation, so at start-up once queries are checked there, at the user's question ("Is there *any* way we could flag that in the compiler if a user doesn't notice? Or it just a timebomb?"): `Board * Title list * Title list` compiled with its includings in either order. The remedy is the newtype Kanban shows (6f3cfe0, `NewestTitle`, `BlockedTitle`); compared per level, never with the root. The sixteen-list scenario now gives each list a type of its own | `features/including.feature` |
| 2026-11-17 | The SQL compared with EF Core's, side by side (the user: "attempting to make the translation to SQL as elegant as EF Core's generated SQL"), and changed in four steps, each red first. Bugs: `Trim`, `TrimStart` and `TrimEnd` remove what .NET removes; a match on a union's case gives each case a WHEN and ELSE NULL, so an unknown stored case is refused as reading refuses it; an owned child's write carries its owner in its WHERE, read only when it writes nothing. Readability (`readable-sql.feature`): no alias repeating a column, one AND or OR list, no ASC, the opposite comparison for a negation, one CASE, no self-alias, NULL written, ESCAPE behind the dialect. Nothing the result does not need: `repo.One` reads LIMIT 2 in no order, Plasm's own numbers are written, no tie-break a unique column or an own-key join settles, no sort for a load by key, a page ordered by row number. Plans: no NULLS clause where the conditions prove the column present (Kanban's finishedOn page then walks `cards_stage_at_idx`: 0.73 ms to 0.04 ms on 50,000 cards), and an optional prepared argument as `= @p OR (IS NULL AND @p IS NULL)`, which an index can serve. Held: unquoted identifiers, JSON containment, LATERAL, batched child writes, RETURNING less, per-column union updates | `features/readable-sql.feature` |
| 2026-11-17 | The SQL laid out as EF Core's is, at the user's request ("Do the first 2" of three causes of its readability; every identifier stays quoted): each clause of a statement starts a line, a derived table is indented under its parenthesis, an EXISTS stays on its condition's line, from one renderer, so the text is the same in logs, `Explain`, errors and F# Interactive (whose own clause-breaking, and the koans', are gone). A source is named after the F# variable that reads it (`for c in ...` is `"c"`, a join's, a load's, an EXISTS's and a list lambda's likewise); one with no variable (a load by a tie, an owned or linked list, a tuple or `_` pattern) is its table under its own name, unaliased; a name a source of the same statement already has, or one of Plasm's derived tables (`ranked`, `d`, `q`), is numbered (`o2`), not refused, since the SQL is unambiguous and F# accepted the shadowing. No plan is shared between queries, so names cannot mix two queries' plans | `features/readable-sql.feature` |
| 2026-11-17 | A write is one round trip (the user: "go on batched writes, stages 1 and 2"): a record's statement and every statement of what it owns and links travel as one DbBatch, after the generated key they need (an Order inserted with Lines is two, a change of a record read is one). Each statement's result is still read, and a statement that wrote nothing fails the write as it would alone, its follow-up read sent after the batch; once one fails the transaction can run nothing more, so a record whose follow-up could not be sent is a conflict. One plan of statements, sent as a batch or, by a provider without `Batching`, one at a time; each statement is reported with its round trip (`CommandEvent.RoundTrip`). New records of one list stay one INSERT each, not one of several rows, so that a broken constraint still names its record (`Lines[1].UnitPrice`), which the database cannot say of one row of several. An owned record's update and delete compare its owner once | `features/round-trips.feature` |
| 2026-11-17 | Each level of loads is one round trip (stage 2 of the user's "go on batched writes, stages 1 and 2"): loads that need only the keys of the records read before them travel together, what a record owns and links and the loads a query binds with `let!` and `and!` alike, and a load of what a load read waits for those records. The benchmark's page of orders (lines and payments) is three commands in two round trips, not three; a Card's comments and labels are read with it in two. The commands and their SQL are unchanged | `features/round-trips.feature` |
| 2026-11-17 | Native AOT, first step (the user: "I think AOT could be a real goal"): a native binary builds the model of a record with a typed id, inserts one and reads it by its id, printing what the runtime prints (`tests/Plasm.Aot/run.sh`, docs/aot.md). F# types reach Plasm through one interface of shapes: generated by Plasm's build step (`src/Plasm.Build`, run by `src/Plasm/build/Plasm.targets`, reading the domain's metadata) where an application is built, read by reflection where there is no build (F# Interactive, the koans); a native binary without them fails naming the step. What queries name is kept by a trimming descriptor in Plasm.dll; FSharp.Core's operators are matched by name, its SpecificCall needing metadata tokens a native binary lacks; a `[<ReflectedDefinition>]` member is refused there for now. Column readers are written out per provider type | `docs/aot.md` |
| 2026-11-17 | Native AOT without generated code (docs/aot.md): Plasm reads an application's records and unions by reflection in a native binary as under the runtime, so the application's own records and unions, declared beside its queries, work there, and an API contract beside them needs nothing. A union is read through FSharp.Reflection where it sees the union's fields, else by its compiled structure (its Tags class, NewX methods and case properties), a probe deciding once; the layout is pinned by a test over every layout the compiler produces and every union of the test domain, the koan shop and Kanban's domain. Options and lists of the provider's value types are written out. Plasm's targets keep the application's and its project references' assemblies whole when a publish trims. The build step that generated shapes (src/Plasm.Build) is gone | `docs/aot.md` |
| 2026-11-17 | Native AOT, Kanban's query shapes (docs/aot.md): the trimming descriptor keeps Plasm.dll and every scalar type Plasm maps whole, since a quotation names members nothing compiled calls (a prepared query's `Arg.Value`, Plasm's builder) and a query may call a scalar's members; prepared queries, lists over owned rows and JSON, let!/and! loads, pages, counts and the write workflows run natively. `Repo.Start` reports a query value whose module cannot be read, or whose definition cannot be, by its name. Known: a quotation reading a union case's field fails natively in FSharp.Core's decoder; the cause is the F# compiler's union field getters lacking `SpecialName`, which makes a native binary drop their attributes | `docs/aot.md` |
| 2026-11-17 | Native AOT, F# union fields and what only a quotation names (docs/aot.md): the F# compiler emits a union case field's getter without SpecialName, and a native binary then drops that property's attributes (docs/aot-repro/union-field-attributes); in a native publish only, Plasm's targets run src/Plasm.FieldGetters, which sets that flag in copies of every F# assembly the binary reads and nothing else, so FSharp.Reflection and FSharp.Core's decoder work there, to be removed when the compiler marks them. Nothing Plasm does to a type or method only a quotation names needs that instantiation's code: a query written in place is rebuilt by reading its builder calls, not invoking them, and a selected tuple is made by setting its fields; Kanban runs natively 17 of 17, as the runtime does | `docs/aot.md` |
| 2026-11-17 | Native AOT, one reader of F# types (docs/aot.md): with Plasm's build step a native binary reads unions through FSharp.Reflection as the runtime does, so the structural reader, its switch and its layout tests are gone; a union whose case a native binary shows without the fields its constructor takes is refused when first read, naming it, its assembly and the step that was not applied, rather than built wrong. A typed id is wrapped by method invokers natively, by code compiled once under the runtime | `docs/aot.md` |
| 2026-11-17 | Native AOT: the refusal of a union a native binary cannot see the fields of is tested, by a second publish in the native check without Plasm's step for F# union fields (`PlasmFieldGetters=false`), which must refuse its typed id naming it, its assembly and the step | `docs/aot.md` |
| 2026-11-17 | Native AOT: an application's native publish shows no IL warning naming Plasm, only FSharp.Core's two summary lines, which any F# program using a quotation or printfn gets (docs/aot.md, "Trimming warnings"). Each warning is suppressed per module and code in src/Plasm/Trimming.fs with what keeps that reflection safe, the module being the finest scope an F# closure leaves; tests/Plasm.Aot/trim-audit.sh fails unless what warns without them is exactly what they suppress, and the check fails on a warning naming Plasm | `docs/aot.md` |
| 2026-11-17 | Native AOT, cold start and size measured (docs/aot.md, "Cold start and size"): natively a process builds its model, starts and returns its first prepared query in about 45 ms against a localhost PostgreSQL, against about 320 ms under the runtime; checking 16 queries at start costs about 9 ms natively. Keeping Plasm.dll whole is 13.5 MB of the 25 MB and hides Npgsql's slim builder's 4 MB | `docs/aot.md` |
| 2026-11-17 | Native AOT, what a quotation names (docs/aot.md): the members a query names inside its quotations are marked once (`QuerySurface`), and a unit test fails when the check's queries name one not marked or leave one unnamed; the check gains nested queries with each builder operation, every aggregate, a raw fragment and referredBy. Found by it: a raw fragment's format constructor was not kept, so `Raw.fragment` failed natively; kept, and pinned. Plasm stays whole: keeping only its public types (20.6 MB against 25.5) is measured and not done | `docs/aot.md` |
| 2026-11-17 | Native AOT on Linux (docs/aot.md): `tests/Plasm.Aot/run-linux.sh` runs the native check in a container with PostgreSQL beside it, building in a copy outside the working tree; linux-arm64 passes in 58 s (27 MB) and linux-x64 under emulation in 162 s (26 MB), every step identical to the runtime, with no change to Plasm | `docs/aot.md` |
| 2026-11-17 | Native AOT, messages without printf over a value type (docs/aot.md): a transaction rejected with an int threw natively, F#'s printf having no code for a value type it captures; it formats over obj, which prints the same. An unsaved key is compared with its type's zero made uninitialised; start-up's warnings and PGlite's arrays format without printf. The check gains the rejection and a refused query naming literal values, both reading the same natively | `docs/aot.md` |
| 2026-11-17 | What a condition reads of no row is computed by Plasm's own evaluator, the same under the runtime and natively (query-semantics.feature, docs/aot.md): FSharp.Core's quotation evaluator, which a native binary cannot run, is gone. It decides by form: values and what is read of them, records, unions, tuples, options, lists, arithmetic and conversions, calls; a function generic over a value type is refused everywhere, naming it. Nothing the features, koans, examples or Kanban compute is newly refused | `features/query-semantics.feature` |
| 2026-11-18 | Plasm behind a connection pooler (docs/aot.md): a snapshot read is a REPEATABLE READ transaction alone, without `SET TRANSACTION READ ONLY`, which a pooler in transaction mode may pin a connection on; Plasm sends only SELECTs in it, so the guard protected nothing (PostgreSQL and PGlite alike). What Plasm sends was audited: nothing prepared on the server, no advisory locks or session state, a batch one implicit transaction; the check prints the same through PgBouncer 1.24.1 in transaction mode, and `No Reset On Close=true` removes Npgsql's own `DISCARD ALL` | `features/core.feature` |
| 2026-11-18 | A C# host over Kanban's F# domain (docs/csharp.md): Kanban's stored records, queries and workflows move into one F# library, Kanban.Data, which both hosts reference, with `KanbanData`, the type C# calls: plain arguments and a CancellationToken in, a Task out, and nothing returned null, an F# option or an F# Result. A read that can miss is a `Found<'T>` and a refusable write an outcome per write, each a struct of C#'s union shape that C# switches over exhaustively. The C# host (minimal APIs, slim builder, source-generated JSON) passes 10 scenarios over HTTP, under the runtime and as a native binary. A card's stage is a union-shaped type too, so no view has a null; System.Text.Json's source generator writes it natively, its case picked by a classifier that reads `kind` (`StageByKind`), the case records plain F# records. Plasm needed no change | `examples/Kanban/features/csharp/csharp-host.feature` |
| 2026-11-18 | Plasm's package carries what a native binary needs (docs/aot.md): `Plasm.targets` in `build/` and `buildTransitive/`, and the field-getter tool in `tools/`, built when Plasm is packed. An application outside the repository that takes Plasm.Postgres from a local feed, and nothing else of Plasm, publishes natively with its union fields readable, printing what the runtime prints; the step costs about 0.1 s of a 16 s publish | `tests/Plasm.Package/run.sh` |
| 2026-11-18 | A function shaped like a deployed one, on this machine (docs/aot.md): Plasm packed into a local feed, a custom-runtime function (`bootstrap`, Amazon.Lambda.RuntimeSupport, linux-arm64) published natively inside Amazon Linux 2023 (27.8 MB, newest glibc symbol 2.34) and invoked through the Lambda runtime interface emulator against PostgreSQL, with no AWS account; every answer checked, with the repository opened in init and in the first invocation. Opened in init, the first invocation's handler takes 0.8 ms instead of 17.5 ms, the whole first invocation 27 ms instead of 31 ms as the emulator times it | `tests/Plasm.Lambda/run.sh` |
| 2026-11-18 | Steady state, a native binary against the runtime (docs/benchmarks.md): Npgsql alone is the same in both; a query translated once is 12 to 21% slower natively, by making rows into records through reflection rather than generated code; a query built per call is translated several times more slowly natively (FSharp.Core's decoding of the builder's quotations and Plasm's translation alike), so an order read with two loads built per call takes 979 µs against 574 µs. Corrected the same day: the first runtime column ran with dynamic code off (`PublishAot` in the project) | `benchmarks/Plasm.Steady/run.sh` |
| 2026-11-18 | GraphQL, slice (i) (docs/hotchocolate.md): a program builds a query at run time from fields declared once in F# (`QueryField`, `QueryCondition`, `BuildQuery`, each the builder's own clause, so the SQL and refusals are the written query's), checked once at start-up by field and operator (`repo.CheckFields`). Plasm.HotChocolate (Hot Chocolate 16.7.6) generates the filter and sort inputs from the declarations and builds a request's where and order onto the application's query; a union-shaped field is a GraphQL union. Kanban's GraphQL host passes 6 scenarios over HTTP, the where and order in its SQL | `features/build-query.feature`, `examples/Kanban/features/graphql/graphql-host.feature` |
| 2026-11-18 | GraphQL, slice (ii), paging (docs/hotchocolate.md): offset paging's skip and take are the SQL's OFFSET and LIMIT (one row more than the page, so hasNextPage needs no count), the total a COUNT under the request's where run only when totalCount is selected; a request without an order is ordered by a declared field the host names, since Plasm reads no page without one. Kanban's GraphQL host passes 11 scenarios | `examples/Kanban/features/graphql/graphql-host.feature` |
| 2026-11-18 | GraphQL, slice (iii), a nested collection (docs/hotchocolate.md): a board's cards with their own where and order are one command for every board, through a batch DataLoader keyed by board and argument set (one query per argument set, the boards' keys as one array parameter); `PlasmRequest.Refinement` reads a request's where and order at once for a batch that runs after the resolver. A per-board take is not pushed down: Plasm plans it only for a `let!` load, and no public entry adds a load from a built query yet. Kanban's GraphQL host passes 13 scenarios | `examples/Kanban/features/graphql/graphql-host.feature` |
| 2026-11-18 | GraphQL, slice (iv), text operators on wrapped scalars (docs/hotchocolate.md): a wrapper's own field, the body of its `[<ReflectedDefinition>]` Value, is read as its column, so `c.Title.Value.Contains` is a LIKE on the title column; an unmarked member of the application's type is refused, saying to mark it (scalar-conventions.feature). `QueryField.text` declares Contains, StartsWith and EndsWith beside equality and order; Kanban's titles are searched by the database. Kanban's GraphQL host passes 14 scenarios | `features/scalar-conventions.feature`, `features/build-query.feature` |
| 2026-11-18 | One way to write a value in a message (docs/aot.md): Reflection.Shown writes every value a message shows, in both modes alike, F# literals for primitives and records, unions, options, tuples and lists through Shape, bounded; printf and %A are gone from Plasm. The native check now compares the native binary with the runtime as shipped (its project no longer sets PublishAot), which found that a refused query's values read `2L` under the runtime and `2` natively; it gains rejections carrying a record, a union with fields, an option and a list, and a stored value the domain does not know | `tests/Plasm.Aot/run.sh`, `tests/Plasm.Tests/Shown.fs` |
| 2026-11-18 | A retried transaction waits between attempts (docs/adr/0001-architecture.md): Kanban's race of two starts at once sometimes answered "unavailable", the loser having spent its five attempts in 13 ms while the winner's commit was still being written. `RetryPolicy` gains `Wait`; `OnSerializationFailure` waits 10 ms after the first attempt, doubling, with jitter, at most 150 ms in all; the wait ends with the transaction's cancellation, and `TransactionError` reports the time waited. Reproduced without load by holding the winner prepared (PREPARE TRANSACTION) | `features/elegance.feature` |
| 2026-11-18 | Kanban's host, slice 1 (docs/kanban-host.md): `examples/Kanban/src/Host`, a C# application over `KanbanData` on the runtime, with cookie sign-in (register, sign in, sign out, who am I; the password's rules the domain's, its hash the host's), a first change over REST (start, made by the person signed in, never a name in the request) answered through one outcome mapping, and the GraphQL reads moved from `Host.GraphQL`, which is removed with its spec project. `KanbanData` gains `RegisterAsync` and `AccountAsync`. 24 scenarios: the 14 GraphQL ones moved, 10 for accounts | `features/host/` |
| 2026-11-19 | Plasm.HotChocolate pages a nested field per parent (docs/hotchocolate.md), nested paging step 2: `PlasmRequest.PageEach(context, key, defaultTake)` is the field's offset paging per value of key, one row more than the page per value, and `PlasmRequest.Where(context, fields)` its where alone, for a total per value; `BatchKey` names skip and take too | `src/Plasm.HotChocolate/Request.fs` |
| 2026-11-19 | Kanban's boards' cards are a page per board (docs/kanban-host.md), nested paging step 3: `boards { cards(where, order, skip, take) { items pageInfo totalCount } }`, every board's page one command (numbered per board) and every board's total one more, grouped by board, only when selected (`KanbanData.CountCardsOfBoardsAsync`, `CardFields.board`). A board's cards were a list; the schema changed for this example's clients. 4 scenarios in graphql.feature, red first, and the board scenarios read the page | `examples/Kanban/features/host/graphql.feature` |
| 2026-11-19 | Kanban as a Lambda function in C# (docs/aot.md, "Kanban's C# function"): `examples/Kanban/src/Lambda`, a custom runtime (`bootstrap`, Amazon.Lambda.RuntimeSupport) referencing Kanban.Data alone. It calls `KanbanData` methods, opened once in init, and switches over their outcomes with no default arm. An event is a plain typed request told apart by `call`, the answer is the outcome's case by name, and all JSON goes through one source-generated context that refuses a missing or null argument before F# is called. It is published natively for linux-arm64 inside Amazon Linux 2023 (29.6 MB, 12.0 MB zipped, newest glibc symbol 2.34, no IL warning naming Plasm) and invoked through the Lambda runtime interface emulator against PostgreSQL, one container and database per scenario. Opening takes 37 ms in init, of which 9 ms is the first connection and about 28 ms is the start-up check, which `OpenAsync` gives a function no way to skip. Later invocations take 1.5 to 2.4 ms. 10 scenarios, red first against a stub that answered `{}` | `features/lambda/lambda.feature`, `tests/Kanban.Lambda.Specs` |
| 2026-11-19 | A host may open Kanban without the start-up check (docs/aot.md, "Kanban's C# function"). `KanbanData.OpenAsync(dataSource, StartupChecks.Skip, ct)` skips the query, table and field checks; `StartupChecks` is a plain enum of Kanban.Data's own, and `Run` stays every other overload's default. The function reads `KANBAN_STARTUP_CHECKS`. The check then runs in the function's own scenarios, which open the same binary with it against the migrations, and a scenario shows that only the check finds a renamed column at start. Measured: init 45.4 ms with the check, 15.6 ms without, but the first invocation then pays 15.5 ms for the connection and the first translation, so the first round trip goes from 47.7 ms to 32.7 ms. 2 scenarios, red first: the function ignored the choice, and the unchecked open failed its init on the renamed column | `features/lambda/lambda.feature`, `examples/Kanban/src/Data/Facade.fs` |
| 2026-11-19 | A page and a total per parent join their batches together (docs/hotchocolate.md): `PlasmRequest.LoadedSegment(context, defaultTake, page, total)` starts a parent's page load, and its total load only when `totalCount` is selected, before it awaits anything. Kanban's `BoardCards` loaded each board's total only after awaiting its page, so the count batch held every board only if every board's page came back before Hot Chocolate dispatched it. Under load one did not, and the late board started a second count: 12 of 20 runs under full CPU load sent one page and two counts. A peer's clean check of 5143014 showed it as one failure in 74. `PlasmRequest.Segment` stays for a field's own page and count. The scenario locks the cards table and requires the count to wait for the lock beside the page, which a count asked for after the page cannot do; red first. Steps that expect one command, or none, now list every command the request sent | `features/host/graphql.feature`, `src/Plasm.HotChocolate/Request.fs` |
| 2026-11-19 | Array columns (array-columns.feature, docs/guide/types-and-records.md), the first of two shapes existing tables need that Plasm could not map (array columns, then composite keys). A list of scalars is one array column by convention from the type, with no attribute. What the list holds tells it apart: a list of stored records is owned or linked, a list of scalars is an array, and anything else is refused with the `[<Json>]` alternative. `[<Json>]` on a list of scalars still chooses one document, as everywhere. The array is never NULL, and it is built only from a closed list of provider types, so a native binary makes no array type at run time. Only `List.contains` and `List.isEmpty` translate over it; equality, ordering, `List.length` and `List.exists` are refused by name. The schema check reads an array column's type through `format_type` of its element. Native: tests/Plasm.Aot stores, reads and filters `string list` and `CustomerId list`, including by a prepared argument, and prints what the runtime prints. 27 scenarios, red first (the model refused a list of scalars), but one: that a nullable array column is reported, which the existing nullability check already did. stored-records.feature refused a list of strings as a model the types cannot describe; that example is now a list of maps, since a list of strings is stored | `features/array-columns.feature` |
| 2026-11-19 | Composite keys, step 1 (composite-keys.feature, docs/guide/types-and-records.md): a record whose Id is a record of scalars has a key of several parts, its parts the table's own columns with no prefix, as the type says with no attribute. The key record is the record's typed id. Reads by key, by a part and by a whole key as a prepared argument (each part bound when the query runs) are supported, as are a stable page ordered by every part, updates and deletes found by every part and the version, and a refused key change. The DDL prints one primary key, and the schema check compares the primary key's columns in order (SchemaIssueKind.PrimaryKey, for keys of several parts only). Refused by name: an optional, list or value part, a key of one part, a generated key, ordering or `<` on a whole key, references to such a key, owned or linked lists on either side, and upserts keyed by it. Two fields mapping to one column are found over every column a field is stored in. Native: tests/Plasm.Aot inserts, reads by a whole key and updates shipments keyed by carrier and number, and prints what the runtime prints. 20 scenarios, red first (the key's parts were prefixed id_ columns) | `features/composite-keys.feature` |
| 2026-11-19 | Nothing null crosses to F# in the C# host (docs/csharp.md, features/csharp/csharp-host.feature). Its minimal API inserted the source-generated context into the host's JSON options, but those options did not respect nullable annotations or required constructor parameters, and the attribute's own flags do not reach a minimal API's binding. So `{"title":null}` and `{}` reached KanbanData, whose create functions happened to answer "is required". The host now sets both flags on `SerializerOptions`, and a body with an argument left out or null is a 400 before any F# is called, under the runtime and natively. The kitchen-sink Host already set both; its scenario now covers a null as well as a left-out field. The Lambda function reads events with the context directly, whose generated options carry the flags: a null title is refused as a JsonException at `$.title`, now a scenario too. Red first: 4 of the C# host's new scenarios failed with KanbanData's own answer | `features/csharp/csharp-host.feature` |
| 2026-11-19 | The browser koans say when their engine cannot load (course-in-the-browser.feature). A page open across a deploy holds the old dotnet.js, whose fingerprinted assemblies the new deploy no longer serves; .NET then failed to start in the worker, and the page showed "Loading… the database ready." for ever, with Run stuck on "Running…". The worker now reports a failed start, and the page says the course could not load, why, and that the site may have changed, with a Reload button, in its status and in a run's result; Run is enabled again. The scenario answers 404 for the site's Plasm assemblies, as across a deploy; red first, the page stayed on "Loading…". Staged twice in a row from the same sources and folder, the assemblies keep their fingerprinted names; a clean build of the same source in another folder differs unless its paths are mapped, which a CI build of the packages now does | `koans/browser/features/course-in-the-browser.feature` |
| 2026-11-19 | Composite keys, step 2 (composite-references.feature): references to and owned lists under a key of several parts, over an issue tracker's tables written by hand, not by Ddl(). A field of a key record refers over every part, in the field's column followed by the key's parts, or as `[<Column("a", "b")>]` names them. ColumnAttribute takes several names rather than a second attribute, and two names on a field of one column, or a count that differs from the key's parts, are refused. A key record inside an Id is part of the key and a reference, `[<NotReference>]` included; parts with the same columns are refused, naming both. An optional reference stores every part or none, and a row stored partly is a read error naming the field. An owned list under such a key is read in one command a level, `(a, b) IN (SELECT * FROM unnest(@p0, @p1))`, with an array per part made from a closed list of provider types; at 3,000 keys the plan is a nested loop of index scans on the composite index (9,000 rows, 6.7 ms). Owned children are found and deleted by every part of the owner's key, and a foreign key's violation is mapped by PostgreSQL's default name over every column. `Ddl()` prints foreign keys and indexes over several columns, an index only where the columns do not lead a key, and `CheckSchema` reports a foreign key over several columns that is missing. Still refused by name: linked lists on either side, an owned list of records keyed by several parts, and upserts keyed by such a key. composite-keys.feature's refusals of a reference and an owned list became those of a linked list and of an owned list of composite-keyed records. Native: shipments own their events through their (carrier, number) key, loaded for every shipment at once, and print what the runtime prints. 16 scenarios, red first (the model refused a reference to a key of several parts); 643 in all | `features/composite-references.feature` |
| 2026-11-19 | Kanban.Data and Kanban's Shared domain check F# nullness (docs/csharp.md): both set `<Nullable>enable</Nullable>`, since the facade is the null boundary, and compile clean against FSharp.Core 8.0.403 (this repository's pin) and 11.0.101 (what a new project gets), and through Fable. A newcomer's facade written from Facade.fs failed 22 times with it on, and Kanban's did not, because nullness in FSharp.Core 9 and later makes `box` return `objnull` and a `string` never null, and the repository pins 8. So the facade's 23 outcome cases are `{ value = card :> obj }`, `Found<'T>` is `'T: not null`, the domain's five create functions take `string | null` and read it with a `null` pattern (an `if isNull` does not narrow), `KanbanUnavailableException`'s inner exception is `exn | null`, and `StageByKind` holds its case as `Type | null` | `examples/Kanban/src/Data/Kanban.Data.fsproj` |
| 2026-11-19 | A C# host over an F# library on Plasm's packages is checked natively (docs/aot.md, "From the packages"): tests/Plasm.Package/csharp, an F# library referencing Plasm.Postgres's package and a C# executable referencing only the F# project, with PublishAot and no Plasm reference or import. The library's package brings Plasm's targets to the C# publish through buildTransitive/. The native binary prints what the runtime prints, with no IL warning naming Plasm, and published without the step it refuses the library's typed id by name. Found on the way: on macOS, under the temporary folder's /var path (a symlink to /private/var), NuGet's restore of the C# project dropped its reference to the F# project and with it the packages, so the script now works in the folder's real path | `tests/Plasm.Package/run.sh` |
| 2026-11-19 | A test of the facade runs in a sandbox (features/csharp/facade-sandbox.feature): `KanbanData.SandboxAsync(body, ct)` gives `body` a facade over Plasm's sandbox repository (one connection, one transaction, always rolled back), sharing the facade's clock and ids, so C# never sees the repository. A board created and a card added inside are read there and gone afterwards; the domain's refusal inside leaves the sandbox usable. Red first, against a SandboxAsync that ran the body without a sandbox: the board was still there afterwards | `features/csharp/facade-sandbox.feature` |
| 2026-11-19 | The koans notebook is removed, at the user's request: the browser koans do not depend on it, and the terminal runner and the browser are the two ways to take the course. Gone: koans/notebook/plasm-koans.ipynb, the --notebook option and its generator, notebook.feature and its steps, the dotnet-repl tool that ran it in a real .NET Interactive kernel, and the Logging.Abstractions copy only the kernel needed. What the browser course is made from moved from Notebook.fs to Course.fs, and `--course` writes the same course.json byte for byte. F# Interactive (docs/start/fsi.md, fsi.feature) shared no code with it | `koans/Plasm.Koans/Course.fs` |
| 2026-11-19 | A program pages each value of a field in one command (build-query.feature, docs/hotchocolate.md), nested paging step 1: `BuildQuery.pageEach key skip take` numbers a query's rows per value of key in its own order (`ROW_NUMBER() OVER (PARTITION BY`, in a derived table named `ranked`, the function a load's take per outer record already used, now shared by it, whole-record reads and projections) and keeps each value's rows from skip, take of them. The where is done first; a page with a take, a skip or another page, or without an order, is refused; a grouped query cannot be paged per value; a list chosen of the page is read for the rows kept alone. Native: tests/Plasm.Aot pages tickets per owner with chosen fields. 6 scenarios, red first | `features/build-query.feature` |
| 2026-11-19 | Selection pushdown, slice (v) step 3 (docs/hotchocolate.md, docs/kanban-host.md): a GraphQL request reads only what it selects. `AddPlasmReadType<Card, CardView>(CardRead.all)` generates the one card type from Kanban.Data's read declarations, each field resolving from a chosen row through its declaration or from a whole CardView (a change's outcome) by the property of its name, checked as the schema is described and refused naming the field; `PlasmRequest.Chosen` reads a field's selection, `BatchKey(context, chosen)` keys a batch by it too. `KanbanData.CardsAsync` and `CardsOfBoardsAsync` take the chosen fields. `{ cards { items { title } } }` reads one column; comments are one more command only when selected. The SDL of CardView is unchanged; the unions no longer carry Plasm.HotChocolate's own doc comment as their description. 6 scenarios in graphql.feature, red first (the whole card read; the check removed) | `features/host/graphql.feature` |
| 2026-11-19 | A program reads the fields its caller chose (build-query.feature, docs/hotchocolate.md), slice (v) step 2: `ReadField.value` and `ReadField.via` declare a field to read (a quotation, and for `via` an F# function run after the read); `BuildQuery.select` reads the chosen fields, and only those, into `Selected<'T>` rows read through the declarations, a field not chosen refused by name; `repo.CheckFields` checks fields to read. A list a record owns or links can now be selected on its own, written or built: its record's key is read beside the row and the list a round trip later, for every row at once, as a whole record's list is. Native: tests/Plasm.Aot reads a ticket's title, stage and notes so, and prints what the runtime prints. 7 scenarios, red first | `features/build-query.feature` |
| 2026-11-19 | A whole union with fields can be selected beside other fields (union-fields.feature): `select (c.Title, c.Stage)` read only the tag column and failed when read (`InvalidCastException`, String to Stage); it now reads every column of the union's cases and makes the union of them as a whole record's field is (a projection's shape gains `Parts`). Found by the selection pushdown report (slice v), whose first step it is. 2 scenarios, red first with that failure | `features/union-fields.feature` |
| 2026-11-19 | Kanban's test databases are dropped (docs/kanban-host.md, "Running it"): the host specs' (tests/Hosting) once a scenario's host has stopped, Kanban.Specs' after every other hook and its shared one when the run ends, each `WITH (FORCE)`. A database is named `kanban[_cs]_<machine>_<pid>_<guid>`, the machine an 8-hex hash of its name, and on a PLASM_TEST_PG server a run's first database drops those of this machine whose process is gone: a killed run's are dropped by the next, another machine's never, and a reused process id only delays a drop | `examples/Kanban/tests/Hosting/Hosting.cs` |
| 2026-11-19 | Native translation, before and after (docs/benchmarks.md): `benchmarks/Plasm.Steady/compare.sh 0d79f23` builds Plasm.Steady from 0d79f23 and from da20d8e with one harness (which gains a select into a record), under the runtime and natively, alternated over two rounds. Natively, Plasm's translation of a query built per call fell from 37 to 4 µs for a read by key, 282 to 30 µs for an order with two loads, and 109 to 6 µs for a select; that order read as a whole fell from 934 to 668 µs and allocates a quarter less. Building the query value (FSharp.Core's quotation decoding: 48 to 348 µs natively) did not move, and is now most of what a query built per call costs. Caching translated plans or templates by shape is therefore not built (docs/hotchocolate.md, docs/aot.md) | `benchmarks/Plasm.Steady/compare.sh` |
| 2026-11-19 | Test databases are dropped: `TestDb` is `IAsyncDisposable`, disposing its data source and then `DROP DATABASE ... WITH (FORCE)`; each scenario's goes after every other hook (one rolls back a prepared transaction), each unit test's with `use!`, and the properties' shared seeded database by an assembly fixture when the run ends. Measured on one server (`PLASM_TEST_PG`), Plasm.Tests then Plasm.Specs, sampled each second from `pg_stat_activity` and `pg_database`: before, 67 test databases were left after the unit run and up to 126 connections were open to them at once, and the next run's dropping of the template took 27 s, past which the spec run's setup timed out and all 565 scenarios failed; after, none is left, at most 16 exist and 4 connections are open at once, and the template drops in under a second. The template itself is recreated at each run's start | `tests/Plasm.TestDomain/Database.fs` |
| 2026-11-19 | `Option.exists` and `Option.forall` over the option a domain's member gives from a union case (`c.Since`, `match c.Stage with Doing(_, since) -> Some since \| _ -> None`) translate: the case test, AND (or, for forall, NOT ... OR) the predicate over the case field's column. Found by Kanban's stale-card job (slice 4), whose query goes back to `c.Since \|> Option.exists (fun since -> since < started)`. 2 scenarios in `union-fields.feature`, red first with the refusal Kanban hit | `features/union-fields.feature` |
| 2026-11-19 | Kanban's host has one clock (docs/kanban-host.md): `KanbanData.OpenAsync` takes a `TimeProvider`, every change reads its time from it, and `SendStaleBackAsync` reads the same one; the overloads without one use the system's (Host.CSharp). The host makes one clock (`Clock.cs`: the system's, or one starting at `KANBAN_CLOCK_START`), registers it and opens the facade with it; the job keeps it only for its timer. Reverses slice 4's two sources of "now". 2 scenarios in `clock.feature`, red first with the clock in the host's services alone | `features/host/clock.feature` |
| 2026-11-19 | Kanban's host, slice 4, the background job (docs/kanban-host.md): `StaleCards`, a BackgroundService, sends stale cards back when the host starts and every `Kanban:StaleCardsEvery`, "now" and the wait from the `TimeProvider` in the host's services. Which cards are stale is the domain's (`Card.staleAfter`, 14 days) and the write is `Workflows.sendStaleBack`, one UPDATE over every board, through `KanbanData.SendStaleBackAsync`; each card's version is incremented, and Kanban has no audit to skip. Found: Plasm refuses `Option.exists` over a member that matches a union (`c.Since`) in a where, so the query matches on `c.Stage`. 4 scenarios in `background.feature`, red first | `features/host/background.feature` |
| 2026-11-19 | Kanban's host, slice 3, GraphQL mutations (docs/kanban-host.md): every change a mutation calling the facade method its REST endpoint calls, answering the facade's outcome as a GraphQL union of its cases, each outcome type registered once; `card(id)` answers `CardResult = CardView \| Missing` from the new `KanbanData.CardAsync`. Mutations need someone signed in (`[Authorize]`, HotChocolate.AspNetCore.Authorization pinned in the example, not in Plasm.HotChocolate). `AddPlasmUnion<'U>(name)` names a union over a generic type, and a case record shared by several unions is one object type. 13 scenarios in `mutations.feature`, red first | `features/host/mutations.feature` |
| 2026-11-18 | Kanban's host, slice 2, REST (docs/kanban-host.md): every change and read over REST, each one `KanbanData` method answered through `Outcomes.cs`; `KanbanData` gains ship, drop, send back, reopen, block, unblock, items, tick, comment, relabel, send all back, my work and a page of finished cards. Nothing null crosses inbound either: the host's JSON respects nullable annotations, so a field left out is 400 before the facade, and the facade is opened without a logger rather than with null. 21 scenarios in `rest.feature`, red first | `features/host/rest.feature` |
| 2026-11-17 | **Reversal**: `including` and `includingOne` are replaced by loads bound with `let!` and `and!`, the row built by `return`, at the user's choice after two spikes in the real library ("I think I like Option 2"; the other, rows generated from `X.Of(...)` by a build step, stays on the unmerged branch spike/row-of). Each load is one including as before; `return` builds a record; a load goes in the field of its name, which replaces the same-type refusal and the one-case-type remedy (2026-11-16); the limit of sixteen and its 64 overloads are gone; `referredBy` gives a `One<'T>`, no longer a query, which a `let!` binds as an option, as it does what is selected of it; `repo.One(referredBy<T> x)` for a record in hand. Koans, examples, benchmarks, README, website and Kanban converted in the same commit | `features/loads.feature` |
| 2026-11-16 | **Start-up checking by default**, at the user's request ("Can we have a startup check available as a default in the library? And the default is \"crash\", but we can add a setting to turn it down to Warning or Ignore?"): `Repo.Start` is the one way to get a repository; the constructor and `repo.CheckQueries` are internal (Plasm's own tests reach them through InternalsVisibleTo), so the namespace argument that read a typo as success is gone; `RepoOptions.Queries` and `RepoOptions.Schema`, each `Crash`, `Warn` or `Ignore`, since a database migrated after start-up is a reason to relax the schema alone; `OnProblem` beside `OnCommand`; no queries found is a problem; functions returning a query are counted in a note. Koans, the browser course, benchmarks, examples, the F# Interactive tour, README and website start their repositories; a repository that is not checked writes Ignore (`RepoOptions.Unchecked`) | `features/query-check.feature` |
| 2026-11-16 | `referring<T> o` and `referredBy<T> o`: the tie derived from the typed ids, at the user's request (asked whether F# could generate nine hand-written relationship functions). Reverses "no inferred tie" from the `including` design: with typed ids it is a proof, not a guess; not exactly one candidate field is refused, never picked. Plain ties in koans, README, website, examples and Kanban moved to it | `features/referring.feature` |
| 2026-11-15 | `[<Json>]`: one jsonb document per field in Plasm's own strict mapping (unknown keys refused, since writes replace the whole document); JSON rendered through `ISqlDialect.Json`, refused at repository creation when absent | `features/json-columns.feature` |
| 2026-11-19 | The suites run on the FSharp.Core a new project gets (docs/status.md, "Limits"). The packages are still built against the floor 8.0.403 (`PlasmFSharpCoreFloor`), so the nuspecs still say >= 8.0.403, which the package check now asserts. Everything else that runs Plasm here runs on 11.0.101: Plasm.Specs, Plasm.Tests, the native check, the koans and their specs, the browser koans, and the package check's consumers. Until now every F# project here had no FSharp.Core reference of its own, since under central package management the F# SDK makes none, and ran on whatever its references pulled in, mostly the floor through Plasm's. Directory.Build.targets now gives each F# project one that lacks it. Kanban runs on 10.1.202, the newest Fable.Remoting.Server 6.1.0 and Fable.Remoting.Json 3.0.0 allow; its C# host and Lambda reference the F# server for its migrations, so they move to 11 in a later change. A pass with the pin at 11 everywhere found Kanban unbuildable (NU1608) and nothing else: suites, native output, IL warnings (2, both FSharp.Core's) and the trim audit as before. It also found six warnings in tests/Plasm.Aot, under both pins, hiding a match that read every stage as "backlog": that project, and Plasm.Aot.Unpatched, now treat warnings as errors but for FSharp.Core's IL2104 and IL3053, and trim-audit.sh, which counts IL warnings, turns that off for its own publish | `Directory.Build.targets` |
| 2026-11-19 | **Changed.** `Tx.update`'s `expectVersion` is gone: a change made from a copy a caller saw (one that came back from a client) reads the record with `Tx.oneAsSeen (query, seen)`, which gives None if there is none and is a `TxFailure.Conflict` if its version is no longer `seen`, before anything is decided on it; the update made from it checks the version it was read at again. `expectVersion` checked only at the write, so a decision was made, and could be refused, on a record the caller had not seen. A record without a `[<Version>]` field cannot be read so (`TxFailure.Misused`, naming `Tx.one`). The examples, the koans (lesson 11), Kanban's cards (whose `StaleCopy` refusal is now the Conflict) and the docs read a caller's copy so. typed-transactions.feature: 5 scenarios in place of 2, red first (a stale copy was decided on, and a record without a version was read); 646 in all | `features/typed-transactions.feature` |
| 2026-11-19 | `repo.Ddl()` quotes a table or column named by a word PostgreSQL reserves, such as `from`, `order`, `user` or `select` (ddl.feature): it quoted only names that are not plain lowercase, so a booking's `From` field printed `from timestamp with time zone`, which PostgreSQL refuses. Queries and the schema check always quoted. Found by examples/Starter's tests. One example added to the outline, red first (a syntax error at "order"); 647 in all | `features/ddl.feature` |
| 2026-11-19 | `examples/Starter`, what a new application starts from: a booking service for shared equipment, an F# library on Plasm.Postgres's package (a record with a union with fields and an owned list, its rules, and `BookingData`, the one type C# calls, with `OpenAsync`, `SandboxAsync` and a static `Ddl`), a C# minimal API on the slim builder with `PublishAot` and source-generated JSON (`RespectNullableAnnotations` and `RespectRequiredConstructorParameters` both in the context's options and in the host's) and a union-shaped view with its classifier, and C# xunit tests, each in a sandbox, with `PublishAot`; its own nuget.config (Plasm from `packages/`, restored into `.nuget/packages`), global.json and solution. The package check builds it from the packed feed in place of tests/Plasm.Package/csharp: the API's native DDL is the runtime's, without Plasm's step it refuses EquipmentId by name, its publish warns only FSharp.Core's two summary lines, and the 7 tests pass under the runtime and as a native binary (xunit.v3.aot, Testcontainers). Its tests found the DDL's unquoted reserved words (ddl.feature) | `examples/Starter` |
| 2026-11-19 | OnCommand reports a transaction's own commands (transaction-events.feature): BEGIN with its isolation level, COMMIT, ROLLBACK, and the savepoints a sandbox works in, in the text PostgreSQL logs, each with `CommandEvent.Kind` (`Statement`, `Begin`, `Commit`, `Rollback`) so that statements are counted by kind, not SQL. A refused or failed transaction reports its ROLLBACK, and so does a sandbox when it ends. Npgsql sends BEGIN and SAVEPOINT with whatever goes next, as measured against PostgreSQL 18, so their events carry the round trip they travelled in (`ProviderCapabilities.DefersBegin`); COMMIT, ROLLBACK and a savepoint's release are round trips of their own, and PGlite sends each at once. A repository with an OnCommand begins through a provider whose transactions report what they send. The spec steps' recorder keeps statements by kind, so no existing step changed; the koans, the browser course, Kanban's specs and the site's SQL page keep statements the same way. 11 scenarios, red first | `features/transaction-events.feature`, `src/Plasm/Transactions.fs` |
| 2026-11-19 | The Starter after a third trial of the Get Started prompt (`examples/Starter`): the equipment's name is a validated value, `EquipmentName`, made by `create: string | null -> Result<EquipmentName, string>` and read back through it. A name it refuses answers 400 with the field; a refusal of the rules answers 422. `BookingData.OpenAsync` takes the application's clock, a `TimeProvider`, and, like Kanban's, an `onSql` log; `LogSql=true` makes the API print each command, its BEGIN and COMMIT included. The README packs the three packages in full into the folder's own `packages/` and says how to create the database and choose the port. docs/aot.md names the four macOS `dsymutil` lines as expected. 11 tests, 4 new: a time the test chose, two names refused with their field, a name read back trimmed | `examples/Starter` |
| 2026-11-19 | A wrapper's `create` that Plasm cannot read a stored value through is refused when the model is built, naming why (scalar-conventions.feature). The convention reads through exactly `create: 'P -> Result<'T, string>`; a `create` that makes the type another way was passed over without a word. Its error not a string, two arguments, an argument other than the stored type, or a return of `'T option`, `'T voption` or `'T` itself were all affected. A private type was then refused as having no create function, and a public one was read past its `create`, unchecked. Still not looked for: a `create` that is not public and static, one in a module not named after the type, and other names. 6 scenarios, red first | `src/Plasm/Model.fs` |
| 2026-11-19 | An id inside a union case is a reference (union-references.feature): a typed id among a case's fields references the record whose key it is, as a record field of that type does. The DDL declares its foreign key, `<table>_<column>_fkey`, and indexes the column. The schema check reports the key missing or misnamed under `<Field>.<case field>`, and a violation is the union field's error. The column is NULL for the cases without that field, which the key allows; cases that share a field of one name share its column and its one reference. `Model.References` lists it as `Status.borrower`. 4 scenarios red first, a fifth (NULL for another case) and a DDL example as guards | `src/Plasm/Model.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-19 | An id inside a value is a reference (union-references.feature): a typed id among the fields of a value (a record with no table, stored in its owner's row) references the record whose key it is, named by its path (`Home.City`, `Work.Near`). Its foreign key is printed, checked and mapped to the value's field, as an id in a union case is; the column is NULL, which the key allows, when the value or the field is optional and absent. A key's own parts are references already. Also pinned: a list of a type stored as a PostgreSQL enum is refused when the model is built (array-columns.feature), its message naming the type as F# writes it; docs/comparisons/enums.md said such a column would come back as raw text. 4 scenarios red first, a fifth as a guard, a DDL example, and the enum row red on its wording | `src/Plasm/Schema.fs`, `src/Plasm/Model.fs` |
| 2026-11-20 | Upserts by a key of several parts (composite-keys.feature): `Bulk.upsert` and `Bulk.insertMissing` take the whole key, `fun i -> i.Id`, as their conflict target, which is every part, `ON CONFLICT ("repo", "number")`; it was refused by name. A tuple of the parts already worked, and so did bulk insert, update and delete, which find rows by any part; a scenario now pins the update and delete by the whole key. 2 scenarios red first, one guard | `src/Plasm/Translate.fs` |
| 2026-11-20 | A linked list joins records keyed by several parts, on either side (composite-links.feature): the join table has a column per part of each side's key, `<record>_<part>` (`thread_repo`, `thread_number`), where a key of one part keeps `<record>_id`; a column name the JoinTable attribute gives for such a side is the prefix. Links are written by unnest over an array per part (`(label_repo, label_name) NOT IN (SELECT * FROM unnest(...))`), loaded in one command a level with the owner's key read back from its columns, and queried through with `List.exists`; a link to a missing record is the field's error by `<join table>_<columns>_fkey`. A linked list of such records is read in the order of the key's parts. The DDL keys the join table by every column and references each side over its columns; the schema check reports a missing column or foreign key. A key of one part on both sides sends the SQL it sent before. 8 scenarios red first, and a DDL example; the refusal it replaces is gone from composite-keys.feature | `src/Plasm/Writes.fs`, `src/Plasm/Plan.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-20 | The koans, from a review of every page: each koan has a hint, the last cell of its row in its lesson's feature, shown after a wrong answer in the terminal and the browser (215 from the review's patch, 17 written once their pages were made sufficient; no hint may contain its koan's answer, and `koans/no-hint-yet.txt` names any koan without one). The 17 koans whose answer was nowhere on the page, and 14 where two answers fit, now name the vocabulary or the form in their comments (FieldErrorKind's cases, TxFailure.Conflict and what it carries, Property as an option, a step's Action, DbFailureKind, a query's full name), without the blank's answer. Two koans counted `sqlSent ()` on a repo they started themselves, which it cannot see (a probe that sent a query still passed); each now gives that repo its own OnCommand. A wrong answer is shown as "You wrote" and "Plasm gave" (they were Expected and Actual, the learner's value as Expected), and the Meditate line, which states the answer in 28 koans, is shown once a koan passes, not before; so is, in the browser, the scenario a koan teaches. A koan title and a lesson goal that stated an answer are reworded (lesson 14's "A left join's missing side is None", lesson 17's "in one command") | `koans/`, `koans/features/path-to-enlightenment.feature`, `koans/browser/features/running-koans.feature` |
| 2026-11-20 | Tx.traverse sends its items' writes together (round-trips.feature, stage 3 of the user's batched writes): its items are independent, so when each is one write (Tx.insert, Tx.update or Tx.delete) their statements travel together, a round trip for each level a write needs. Three Sheets whose keys the application assigns, with their rows, are one round trip; three Orders whose keys the database generates, with their lines, are two, however many. Every item is checked before anything is sent; a failure is the first failed item's, in order, the items before it rolled back as if they had run one after another. An item of more than one step (a `transaction { }`, a read, a bind) runs after the one before, as before, and a provider that cannot send statements together sends them one at a time. A write step is now a value that carries its round trips (`Writes.plan`), which a bind runs at once. 3 scenarios red first, 3 guards | `src/Plasm/Tx.fs`, `src/Plasm/Writes.fs` |
| 2026-11-20 | A transaction can lock the rows it reads (row-locks.feature): `locking`, the last clause of a query of records, takes `RowLock.ForUpdate`, `ForNoKeyUpdate`, `ForShare` or `ForKeyShare`, waiting, or `.NoWait` or `.SkipLocked`. It makes a `Locked<'T>`, which only `Tx.all` and `Tx.one` read, so a lock outside a transaction does not compile. The lock is on the query's own rows (`FOR UPDATE OF "j"`); what they own or link is read by its own command, unlocked. A projection has nothing to lock, a grouped query is refused when it is checked (it must end in select), a joined or per-key-paged locked query is refused by name, and a provider without RowLocking refuses a locked read. A NOWAIT read of a held row fails as `DbFailureKind.LockNotAvailable` (SQLSTATE 55P03). Proved with two transactions: two workers claiming the first queued job with SKIP LOCKED get different jobs; a NOWAIT claim on a held job fails at once; a shared lock is shared and keeps out an update. 5 scenarios red first, 2 guards (the compile check, which the types settle, and the grouped refusal, which already held) | `src/Plasm/Query.fs`, `src/Plasm/Tx.fs`, `src/Plasm/Translate.fs` |
| 2026-11-20 | A transaction can try a step and go on when it fails (tx-attempt.feature): `Tx.attempt step` runs it under a savepoint and gives `Ok` with what it gave, its writes kept (RELEASE SAVEPOINT), or `Error` with its failure, what it wrote undone (ROLLBACK TO SAVEPOINT) and the transaction able to go on; OnCommand reports the savepoint with its Kinds. Any failure of the step is a value, a Conflict and a rejection included, except those that end the transaction: a serialization failure, a deadlock, a lost connection, a closed repository and a misused step stay the transaction's failure, so a retry policy can run it again. Attempts nest, each its own savepoint, and the steps an attempt rolled back leave the list a later failure reports. Also pinned: under a retry policy a step of your own (Tx.run) runs once per attempt, its database work undone and done again. 4 scenarios red first (the uncaught failure proved red against an attempt that catches everything), 3 guards | `src/Plasm/Tx.fs` |
| 2026-11-20 | Bulk updates and deletes can return the rows they wrote (bulk-writes.feature): `Bulk.updateReturning (query, change)` and `Bulk.deleteReturning query` are the same writes as `Bulk.update` and `Bulk.delete`, a `BulkWrite<'T list>` instead of a count, as `Bulk.insert` already is. The update's rows come back as they are after it (UPDATE ... RETURNING), read whole with what they own and link in the same transaction; the delete's as they were. A delete cannot read back what the rows it removes owned or linked, so it returns only records that own or link nothing, and refuses others by name before anything is sent; a provider without RETURNING refuses both. 3 scenarios red first | `src/Plasm/Bulk.fs` |
| 2026-11-20 | A field can be unique within other fields (unique-within.feature): `[<Unique(Within = [| nameof the<Tag>.Tenant |])>] Name` states one uniqueness over the Within fields and then the field, `(tenant, name)`, so its index also serves a tenant's reads. The DDL prints `constraint <table>_tenant_name_key unique (tenant, name)`, the schema check reports it missing or held under another name with the rename (it reads unique indexes over any number of columns, in order), and a violation is the error of the field carrying the attribute. Two fields unique within the same fields are two constraints, named by their own columns (`tenant_tags_tenant_name_key`, `tenant_tags_tenant_slug_key`). A Within field and the field carrying it must be one column and required, each refused by name at model build: PostgreSQL counts absent values as distinct, so with an optional field two rows F# calls equal would both be kept, and refusing keeps one meaning for absent values without NULLS NOT DISTINCT (PostgreSQL 15). Not covered: a field of several columns in Within (a value, a union, a reference to a key of several parts), refused by name. 8 scenarios red first, and a DDL example | `src/Plasm/Model.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-20 | The database describes every statement before any runs (statement-check.feature): Repo.Start translates every query value a module defines, as before, and now asks the database to describe the SQL of each, parsed and planned but not run (Npgsql's SchemaOnly: Parse and Describe): its query values with their loads, its bulk writes, and its hand-written statements (module-level FormattableString values). A statement the database refuses is `StartupProblem.Refused`, named by its value with the database's reason, under RepoOptions.Queries; a bulk write that cannot be translated is a query problem. Every parameter goes as an untyped NULL, so a prepared query is described through its template. Only over tables that match the model: when the schema check finds a difference or does not run, nothing is described and the schema check's report stands. A provider without `Describe` (PGlite) describes none and notes how many (`NotDescribed`), a note that never fails start-up; so is a statement whose parameter the database cannot type from the statement alone (42P18, `@p0 IS NULL` with an untyped NULL), the description's limit and not the statement's fault. Not reached: SQL written in place at a call (repo.Sql, Tx.sql, Tx.executeSql), a query a function builds, a transaction's steps, and a query's let! loads (its own statement is described). Proved that describing runs nothing: a module-level `delete from customers` and a `Bulk.delete` of every customer leave them all. 6 scenarios red first, 2 guards | `src/Plasm/Repo.fs` |
| 2026-11-20 | Each command is an activity a tracer can listen to (activities.feature): Plasm has one ActivitySource, "Plasm", and wherever OnCommand is given an event, the same event is a Client activity while something listens, named by the statement's operation and table (`SELECT customers`) or the transaction's own command (`BEGIN`), lasting the command's duration and tagged by OpenTelemetry's database conventions: db.system.name, db.operation.name, db.collection.name, db.query.text (as sent, values as parameters), db.response.returned_rows, error.type (the SQLSTATE) with the status Error when it failed, and plasm.round_trip. OnCommand stays the one hook, and with neither an OnCommand nor a listener no event is made. A repository always begins its transactions through the reporting provider, which reports only while one of them wants it, so a transaction's BEGIN and COMMIT are activities too. `CommandEvent.Error` carries why a command failed. 3 scenarios red first, a guard that OnCommand still has every event | `src/Plasm/Activities.fs` |
| 2026-11-20 | Kanban's host tests cannot be handed one port twice (tests/Hosting): a host listens on port 0 and its address is read from what Kestrel writes, "Now listening on: ...", where it took a free port, let it go, and passed it on, which a test run beside it could take in between. One did: a check of the koans (tree 6eb9c09) failed one host scenario with "address already in use" while another check ran; its log is kept | `examples/Kanban/tests/Hosting/Hosting.cs` |
| 2026-11-20 | Two queries can be combined, and a query can be joined (set-operations.feature): `Query.union (a, b)`, `Query.unionAll`, `Query.intersect` and `Query.except` combine two queries of records, or two projections of one type (tupled, since a member cannot be overloaded curried). Of records the result is a query like any other, `for c in Query.union (a, b) do` filtering, ordering and paging it, its records read whole with their loads; its source is `FROM (... UNION ...) AS "c"`. Ordering and paging come after the set operation, so an operand that orders or pages itself is refused by name, and a page is a page of the combined rows; a record's version is a column like the others, and a set operation's rows cannot be locked (refused by name, since a union of records is a Query). Of projections the result is the rows, read by position in the left operand's shape, in no order. A join takes a query where it takes a table, `join c in (plasm { ... }) on (...)`, as a subquery named by the join's variable. 11 scenarios red first (two made to tell an unapplied union from an applied one) | `src/Plasm/Translate.fs`, `src/Plasm/Sql.fs` |
| 2026-11-20 | Filtered, boolean, list and text aggregates (aggregates.feature), as Agg functions: `Agg.countWhere cond` and `Agg.sumWhere cond value` (`COUNT(*) FILTER (WHERE ...)`), `Agg.any cond` and `Agg.all cond` (bool_or, bool_and), `Agg.listBy value key` and `Agg.stringConcatBy sep value key` (array_agg, string_agg, `ORDER BY` inside the aggregate), and the Descending forms of the last two. The order inside a list or a text is always stated at the call, a value or a tuple of them, since in a grouped query the query's own order sorts the groups; there is no unordered form. Over no rows each gives what the F# function it is named after gives: 0, 0, false, true, [] and "". A projection reads a computed list (the database's array, each element as its type). The native check reads all eight. Not covered: mixed directions inside one aggregate (Descending applies to every part of a tuple key). 3 scenarios red first | `src/Plasm/Translate.fs`, `src/Plasm/Query.fs` |
| 2026-11-20 | A select can read values over a window of rows (window-functions.feature): `Win.partition key` (a value or a tuple) or `Win.whole`, ordered with `|> Win.orderBy`, `Win.thenBy` and their Descending forms, given last to `Win.rowNumber`, `Win.rank`, `Win.denseRank`, `Win.lag`, `Win.lead` (None at the edge), `Win.runningSum` (`ROWS UNBOUNDED PRECEDING`), and `Win.sum`, `Win.avg`, `Win.count` (over every row of the window, whatever its order). A function that depends on an order, over a window with none, is refused by name, and a window function outside select is refused: the window is over the rows the query's where keeps, before its order and page, so to filter by its value query a query that reads it. The native check reads all of them. 6 scenarios red first | `src/Plasm/Translate.fs`, `src/Plasm/Query.fs`, `src/Plasm/Sql.fs` |
| 2026-11-20 | The koans reordered, as their review proposed: Writes is lesson 10 and Constructors and constraints 11, since a constraint's error is met through a write; lesson 09's five loads (let!, and!, a load's own loads, referredBy, a relationship written once) are lesson 15, Loads, after joins; lessons 15 to 22 are now 16 to 23. Each error case is named where a koan first meets it (TransactionError and TxFailure.InvalidChanges in 04, DbError.Translation in 02, DbError.Materialization in 05, TxFailure.Failed and DbError.Database in 11, RolledBack in 13), and the curriculum says where; lesson 17's use of repo.Sql points to lesson 19. Every cross-reference and prerequisite follows the new numbers | `koans/` |
| 2026-11-20 | In the browser course a koan's hint is a button the learner chooses (the user: "make the hint a button on the page that a user can click to see"): every koan with a hint shows "Show a hint" from the start, a `<button>` with `aria-expanded`; the hint appears below the code when it is clicked, the button then reads "Hide the hint", and a second click hides it. Each koan starts with its hint hidden, and a run, passed or failed, neither shows nor hides it. The hint is no longer shown after a failed run in the browser; the terminal runner still prints it after a wrong answer, and the Meditate line still waits for a pass. Every koan has a hint, so no koan shows no button. 5 scenarios red first | `koans/browser/app/Koans.Browser.Host/wwwroot/app.js`, `koans/browser/features/running-koans.feature` |
| 2026-11-20 | The packages are fit to push to nuget.org: Plasm.Annotations no longer carries what a build writes (its `**/*.fs` for Fable took `obj/Debug` and `obj/Release` with it), and the package check refuses any package with `obj/` or `bin/` in it, since a version on nuget.org cannot be replaced. `tests/Plasm.Package/run.sh` checks a feed packed already when `PLASM_FEED` names it, so a release checks the files it will push; without it, it packs its own as before. The packages carry their own README (`src/PACKAGE.md`), whose links are absolute: the repository's lead nowhere on nuget.org | `src/Plasm.Annotations/Plasm.Annotations.fsproj`, `tests/Plasm.Package/run.sh`, `src/PACKAGE.md` |
| 2026-11-20 | The Starter restores Plasm from nuget.org (`examples/Starter`): Plasm.Postgres 0.1.0-alpha.1, the first release, named in `Directory.Packages.props`; its nuget.config has nuget.org alone, and the README no longer packs the packages into `packages/`. The package check still builds it from its own feed: it copies the feed into the copy's `packages/`, gives the copy a nuget.config that takes Plasm from there, and sets the copy's Plasm.Postgres version to the feed's | `examples/Starter`, `tests/Plasm.Package/run.sh` |
| 2026-11-20 | A `dotnet new` template, Plasm.Templates (`src/Plasm.Templates`): `dotnet new install Plasm.Templates`, then `dotnet new plasm-api -n Lending`, gives the Starter with its names replaced (solution, projects, namespaces, the connection string's name, the README's database) and Plasm.Postgres at the template's own version. Its content is examples/Starter's files, taken when it is packed, with a template.json that replaces the Starter's Plasm.Postgres version with the package's. The package check packs it, fails if its files are not the Starter's tracked files byte for byte, installs it into a hive of its own, makes a project from it outside the repository, and finds no Starter left, Plasm.Postgres at the version and nuget.org as its one source; it then gives that project the packed feed (it was packed at 0.0.1-packagecheck, which nuget.org never has; the template has been on nuget.org since 0.1.0-alpha.2), and its 11 tests pass and its API publishes natively, warning only FSharp.Core's two summary lines | `src/Plasm.Templates`, `tests/Plasm.Package/run.sh` |
| 2026-11-20 | The Starter after the fourth trial of the Get Started prompt (`examples/Starter`), and so the template made from it: `ClockStart=<instant>` starts the API's clock there (it runs on at the system's pace; unset, the clock is the system's), so a rule about time can be shown over HTTP. The README gives PostgreSQL in Docker (postgres:18, the DDL piped into the container), the native run with the clock, the tests' native publish and run, the two IL warnings that are expected and the macOS `dsymutil` lines that concern the `.dSYM` alone; lists the 400 with an empty body for a null or missing property, which ASP.NET's binding answers before BookingData is called; and names every name a reader changes, each of which `dotnet new plasm-api -n` replaces. Walked natively from a copy on nuget.org's 0.1.0-alpha.1: 11 native tests, the clock's instant in a withdrawal's `since`, and the two 400s | `examples/Starter` |
| 2026-11-20 | Streaming reads (streaming.feature): `repo.Stream query` and `repo.Stream projection` are an `IAsyncEnumerable<Result<'T, DbError>>` of the rows, read from the database as the caller asks for the next and never gathered. Nothing is sent before the first row is asked for; from then one connection is held, and it is given back when the rows end, when the caller disposes of the stream early and when the token is cancelled (OperationCanceledException, as every read). A failure is the last element: a database failure (`DbError.Database`, the third row's division by zero after two values) or a value that cannot be read back (`DbError.Materialization`); a query that cannot be translated or streamed is one Error and opens no connection. A stream is one command, so a query of records that own or link a list, or a projection that selects one or binds loads, is refused naming the list (`Order.Lines is read by another command`); reading such records in batches would need a second command on the connection while the first is still being read, which PostgreSQL allows only through a cursor in a transaction. Outside a transaction only: a stream in a Tx would need a step that yields rows, a second style. In a sandbox it reads the sandbox's rows, holding the sandbox's connection until it ends. OnCommand and the activity report it once, when it ends, with the rows read. 12 scenarios; the native run streams records and a projection | `src/Plasm/Execution.fs`, `src/Plasm/Repo.fs`, `features/streaming.feature` |
| 2026-11-20 | `Tx.oneAsSeen` checks the version on the record's own row, before what it owns is read (typed-transactions.feature): a stale copy is a Conflict after one SELECT, where it was one more per level of what the record owns. No change to its signature or its answers | `src/Plasm/Tx.fs`, `src/Plasm/Execution.fs` |
| 2026-11-20 | A conflict names the record that was stale (typed-transactions.feature, concurrency.feature, composite-keys.feature): `TxFailure.Conflict (entity, key)`, the key being the value of the record's key field, boxed (an F# type test reads it, `:? OrderId as id`; C# a pattern, `key is OrderId id`). Of two records of one type read from the same line, through a helper or `Tx.traverse`, the failure says which was stale; so does a stale `Tx.update` or `Tx.delete`, by version or by every column, and a composite key's record. The failure's step is where the stale read is written, now pinned by a scenario, which a mutation of the reported line fails. Its message names the key: `Order OrderId 2L: changed or deleted by another writer since it was read`. Breaking: see Changed. Kanban's facade takes the stale card from the key, where it had been given the card the caller named; the Starter keeps `Conflict _` until a release carries the key, since it builds on Plasm.Postgres from nuget.org | `src/Plasm/Tx.fs`, `src/Plasm/Writes.fs` |
| 2026-11-20 | A write gives back the record as stored, its time to the microsecond (typed-transactions.feature): a time written finer than PostgreSQL's microsecond comes back without what is finer, so the record `Tx.update` or `Tx.insert` gives back is the one to keep, not the copy built. Plasm neither rounds nor truncates; Npgsql drops what is finer toward PostgreSQL's epoch, so 2026-01-01T00:00:00.0000019 is stored as .0000010 and 1999-01-01T00:00:00.0000019 as .0000020. The record given back equals the record read again. A mutation giving back the copy built fails both examples | `features/typed-transactions.feature` |
| 2026-11-20 | The Starter's README after the fifth trial of the Get Started prompt (`examples/Starter`, and so the template): it says it is an example to replace, and what it books as it comes; it names the Booking names (`BookingData` and its file, `BookingUnavailableException`, `BookingTests.cs`) as the domain's, which `dotnet new plasm-api -n` leaves for the reader to rename; the native run gives `--urls` as the `dotnet run` line does; and the Docker line says what to change when port 5432 is taken | `examples/Starter/README.md` |
| 2026-11-20 | Kanban's C# host and Lambda run on the FSharp.Core a new project gets, 11.0.101 (`ApplicationFSharpCoreVersion`, stated once in the root Directory.Packages.props), by a VersionOverride in those two projects; the F# server, client and shared code stay on 10.1.202, which Fable.Remoting.Server 6.1.0 and Fable.Remoting.Json 3.0.0, the newest, hold them to ([10.1.202, 10.2.0)). A scenario in each suite reads what the build resolved: the host's own deps.json, and the FSharp.Core the Lambda's publish in Amazon Linux 2023 restored. Red first, each finding 10.1.202. The override is not private, since a private package is left out of the publish and the native compiler did not find FSharp.Core (IL2007); the host's scenarios reference the host for its build alone (ReferenceOutputAssembly false, ExcludeAssets all), as they also reference the F# server for its migrations, and start it from its own output, on what its build resolved. The host's 17 scenarios pass on the built host and on a native one, and the native Lambda's 14, warning only FSharp.Core's two summary lines | `examples/Kanban/src/Host.CSharp`, `examples/Kanban/src/Lambda` |
| 2026-11-21 | The koans' lesson 13 reads, decides and writes as an application does: its `cancelOrder` takes the version the caller saw and reads with `Tx.oneAsSeen`, and a new koan, "A stale copy is refused before the domain decides", shows a stale copy refused as a Conflict naming the order, the draft unchanged. Red first: the lesson's row named a koan the code did not have | `koans/Plasm.Koans/Lessons/L13_Transactions.fs` |
| 2026-11-21 | Koans lesson 24, About set operations, the first of the course's sixth part, for what Plasm gained after the course was written: `Query.union`, `unionAll`, `intersect` and `except` over the shop's customers, the combined rows ordered and paged after the union is formed, an operand that orders itself refused, two projections combined, and a query as a join's source; seven koans, each with a hint, each naming its set-operations scenario. Red first: the lesson's rows named koans the code did not have | `koans/Plasm.Koans/Lessons/L24_SetOperations.fs` |
| 2026-11-21 | Koans lesson 25, About windows and aggregates over rows: each customer's orders numbered (`Win.rowNumber` over `Win.partition`), a running total and the total before (`Win.runningSum`, `Win.lag`), a whole window's sum and count beside each row, a window over the rows the where keeps, a rank over no order refused, and the filtered, boolean and list aggregates (`Agg.countWhere`, `sumWhere`, `any`, `all`, `listBy`, `listByDescending`) with what each gives over no rows; nine koans, each with a hint, each naming its window-functions or aggregates scenario. Red first | `koans/Plasm.Koans/Lessons/L25_WindowsAndRowAggregates.fs` |
| 2026-11-21 | Koans lesson 26, About locks and tries: a locked read in a transaction (`locking RowLock.ForUpdate`, `Tx.all`), its lock on the order's own rows and not on its lines, and `Tx.attempt` with a duplicate's field error, a success kept, and a stale copy's Conflict, each a value the transaction goes on from; five koans, each with a hint. NOWAIT and SKIP LOCKED are kept out of the course (`koans/kept-out.txt`): what they do needs a second transaction at once, and the browser course's PGlite has one connection. Red first | `koans/Plasm.Koans/Lessons/L26_LocksAndTries.fs` |
| 2026-11-21 | Koans lesson 27, About bulk writes that return their rows: `Bulk.updateReturning` cancelling the drafts and giving them back with their lines, `Bulk.deleteReturning` giving back the failed payment, and a delete of orders, which own their lines, refused by name with nothing sent; three koans, each with a hint. Red first | `koans/Plasm.Koans/Lessons/L27_WritesThatReturnRows.fs` |
| 2026-11-21 | Koans lesson 28, About recursive queries, over a tree of categories the koans' shop now has (`Category`, table categories: Tools, Saws, Hand saws, Drills, Garden, Hoses): everything under Tools and everything above Hand saws, with their depth, in one statement; the query made from it filtered by depth; a cycle given to the data, read once round; and a step that does not join `found` refused; five koans, each with a hint. Lesson 02's model now has eleven record types. recursive-queries.feature and streaming.feature are tagged @implemented, as every implemented feature is, which the koans' check of what a koan teaches requires. Red first | `koans/Plasm.Koans/Lessons/L28_RecursiveQueries.fs` |
| 2026-11-21 | Koans lesson 29, About streaming reads: `repo.Stream` over the customers, asked for one row at a time, each a Result; nothing sent before the first row and the command reported when the stream ends; a projection streamed; a stream of orders, which own their lines, a single Error naming Order.Lines with nothing sent; four koans, each with a hint. Red first | `koans/Plasm.Koans/Lessons/L29_StreamingReads.fs` |
| 2026-11-21 | Koans lesson 30, About keys of several columns and lists in a column, which the review found the course never reached. The koans' shop gains `Stock`, keyed by a `StockKey` record (table stock, primary key (warehouse, sku)), and `Keywords: string list` on Category (a text[] column): a stock row read by its whole key and by one part, an update finding it by every part and its version, ordering by a whole key refused; keywords read whole, filtered with `List.contains` and `List.isEmpty`, and a change writing that column alone; eight koans, each with a hint. Lesson 02's model now has twelve record types. Red first | `koans/Plasm.Koans/Lessons/L30_KeysAndListsInColumns.fs` |
| 2026-11-21 | The packages and docs/status.md say what the pages say since 0.1.0: "Plasm is new: this is version 0.1, and its API may change before 1.0." It is in src/PACKAGE.md (the packages' nuget.org page) and the packages' release notes; the descriptions of Plasm, Plasm.Annotations, Plasm.Postgres and Plasm.HotChocolate no longer end "Experimental."; Plasm.PGlite, which is not packaged, is described as a spike the browser koans use. nuget.org shows the new text from the next release | `src/PACKAGE.md`, `src/Directory.Build.props` |
| 2026-11-21 | The packages, their release notes and docs/status.md say "Plasm is new, and its API may change before 1.0.", without a version, which went stale: the 0.2.0 packages' readme says "this is version 0.1". nuget.org shows the new text from the next release | `src/PACKAGE.md`, `src/Directory.Build.props`, `docs/status.md` |
| 2026-11-21 | The every-column check of a write without a version field compares each column by the dialect's null-safe comparison (`ISqlDialect.NullSafeComparison`), as every other null-safe comparison does, not by PostgreSQL's `IS NOT DISTINCT FROM` written into the core (providers.feature: a dialect that spells it its own way is obeyed in the UPDATE's WHERE). The first of the boundary changes a second provider needs; the SQL sent to PostgreSQL is the same. Red first | `src/Plasm/Writes.fs` |
| 2026-11-21 | A dialect's column types are three things, said apart: the type a column is declared as (`ColumnType`, which Ddl prints), the names the catalogue may give it (`CatalogueTypes`, which the schema check accepts and scaffolding maps back) and the type a JSON scalar is cast to (`CastType`). One list served all three, its first name standing for the declared and cast type (providers.feature: a dialect that accepts "character varying" before "text" for a string still declares text). Breaking for a custom dialect: see Changed. The SQL and DDL for PostgreSQL are the same. Red first | `src/Plasm/Sql.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-21 | A provider says whether it has enum types and array columns (`ProviderCapabilities.EnumTypes`, `ArrayColumns`), and a model a provider cannot store is refused when the repository is made, naming every field, as one with `[<Json>]` fields already is by a provider without JSON documents (providers.feature: a test dialect without either refuses the shipments' Status and PreviousStatus, and the issues' five lists). Before, such a model was made and failed at the first statement, or stored the wrong value. PostgreSQL has both. Breaking for a custom provider: see Changed. Red first | `src/Plasm/Repo.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | Rows skipped with no limit are paged as the dialect pages them: a dialect that needs a LIMIT before an OFFSET names its unbounded limit (`ISqlDialect.UnboundedLimit`; SQLite's is -1), and `skip` alone renders `LIMIT -1 OFFSET @p` there (providers.feature). PostgreSQL's OFFSET stands alone, as before: the SQL it is sent is the same. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Sql.fs` |
| 2026-11-21 | The statement check asks the provider to describe each statement (`IProvider.Describe`), and reports what it says (statement-check.feature: a provider that refuses every statement in its own words has each reported so). Before, the core opened a reader with CommandBehavior.SchemaOnly and read PostgreSQL's SQLSTATE 42P18 itself; that is now the PostgreSQL provider's way of describing, so PostgreSQL's statement check is as it was. A driver that runs a statement when asked for its schema (Microsoft.Data.Sqlite does) cannot be made to by the core. Breaking for a custom provider: see Changed. Red first | `src/Plasm/Repo.fs`, `src/Plasm/Provider.fs`, `src/Plasm.Postgres/Postgres.fs` |
| 2026-11-21 | A value stored as a database type of its own, an enum type or a JSON document, is cast as the dialect casts it (`ISqlDialect.StoreCast`, `TextCast`), and a document's type is the JSON dialect's (`IJsonDialect.StoreType`), not `jsonb` named by the model: what Ddl declares, the schema check compares and every cast, in queries, writes and hand-written SQL, sends (providers.feature: a dialect that stores an enum as text sends its value with no cast, and one whose documents are `json` declares a document column `json`). PostgreSQL's casts and DDL are as they were. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Sql.fs`, `src/Plasm/Model.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-21 | Aggregates and window functions are named by the dialect (`FunctionName`, as other functions are: SQLite will say MAX for BOOL_OR), and an aggregate's empty list is the dialect's empty array (`ISqlDialect.EmptyArray`) (providers.feature). The least and greatest stored name of an ordered union are read back from their position by a CASE (`CASE MIN(...) WHEN 0 THEN 'draft' ... END`), which any SQL has, not PostgreSQL's `(ARRAY['draft', ...])[MIN(...) + 1]`: the one change in what PostgreSQL is sent, the same value. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Sql.fs`, `src/Plasm/Translate.fs` |
| 2026-11-21 | Two things a provider's row locks did are told apart: a query's `locking` needs row locks (`RowLocking`); a write that replaces what a record owns, which reads it first, needs them only where another writer could change it meanwhile. A provider whose writers take turns (`ProviderCapabilities.WritersTakeTurns`, as SQLite's do) deletes a record and what it owns reading those rows without a lock, where before it was refused for want of row locking (row-locks.feature). PostgreSQL locks them as before: the SQL it is sent is the same. Breaking for a custom provider: see Changed. Red first | `src/Plasm/Writes.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | The site's SQL layout (`SqlCapture.format`, which lays out website/sql.js) keeps IS [NOT] DISTINCT FROM on one line: it took the operator's FROM for a clause and broke the line before it, wherever the comparison was not in brackets. A check mode of the examples program, `-- check-format`, lays out fixed statements and fails on any other layout; the full check runs it. Red first. sql.js is unchanged, since no statement it shows has the comparison unbracketed | `examples/Plasm.Examples/SqlCapture.fs` |
| 2026-11-21 | A prepared query's list argument is one parameter, as the dialect sends a list (`ISqlDialect.ListValue`, `InList`), not PostgreSQL's array parameter written into the core: a dialect that sends a list as one JSON text and reads it with json_each is obeyed, the text the same whatever the list's length (providers.feature). Before, `= ANY(@p)` was sent whatever the dialect could take. PostgreSQL is sent `= ANY(@p)` as before. The first of three changes that put every list Plasm sends behind the dialect (loads under keys of several parts and link writes follow). Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Translate.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | A load under a key of several parts matches the keys as the dialect sends lists (`ISqlDialect.RowsIn`): one list a part, each one parameter (providers.feature: a dialect that sends lists as JSON joins json_each for each part). Before, PostgreSQL's unnest was written into the core. PostgreSQL is sent `(a, b) IN (SELECT * FROM unnest(@p0, @p1))` as before. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Plan.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | Links (a many-to-many list's join-table rows) are written as the dialect sends lists (`ISqlDialect.NotInList`, `ListRows`, with `ListValue`), not by PostgreSQL's `<> ALL` and `unnest` written into the core, so a provider without array parameters writes them: an Article's Tags made new through PostgreSQL's provider with `ArrayParameters` off are stored (providers.feature), where before the write was refused. With this, every list Plasm sends goes through the dialect. PostgreSQL is sent the same link SQL as before. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Writes.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | The schema check and scaffolding read the database's catalogue by the dialect's queries (`ISqlDialect.Catalogue`), its lists of names sent as the dialect sends a list; their rules are as they were (providers.feature: a dialect whose column query is its own is the one sent, and the model's tables are found as they are). Before, eight queries of information_schema and pg_catalog were written into the core; they are now the PostgreSQL provider's, word for word. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/SchemaCheck.fs`, `src/Plasm/Scaffold.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | Ddl prints the model's tables in the dialect's DDL (`ISqlDialect.Ddl`): its reserved words, its generated key's clause, a store type's column type, an enum type's statement or, where no enum type holds them, a check on the enum's column, and foreign keys in their tables or added after them (providers.feature: a dialect with its own key clause and foreign keys in their tables is printed so, with no alter table). Before, PostgreSQL's were written into the core. PostgreSQL's DDL is the same, character for character, for each of the specs' models (the test domain, arrays, enums, documents, keys of several parts, references and links of them, row locks: 228 lines compared). With this, every construct a provider renders differently is behind the dialect. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/SchemaCheck.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | The boundary is checked: `tests/Plasm.Boundary/check.sh`, run by the full check, searches src/Plasm (comments left out) for PostgreSQL-only constructs, unnest, = ANY, <> ALL, cardinality, the catalogue's tables, jsonb, ARRAY[, identity, create type, alter table, IS DISTINCT FROM, casts to text, CYCLE, SchemaOnly, 42P18 and the rest, and fails on any that `tests/Plasm.Boundary/allowed.txt` does not allow, by file and count, with its reason. Five are allowed: two guarded by ArrayParameters and ArrayColumns, the DDL's alter table where foreign keys are not declared in their tables, PostgreSQL's advice for a misnamed constraint, and recursive queries' CYCLE, which moves behind the dialect with the SQLite provider. Red: on the tree before the boundary changes it finds 21 | `tests/Plasm.Boundary` |
| 2026-11-21 | A decimal is read by value as the dialect's exact hooks read it (`ISqlDialect.Exact`, an `IExactDialect`): translation marks each comparison and null-safe comparison of decimals, their arithmetic, SUM, AVG, MIN and MAX (over a window too), an order by a decimal, a group or window partition by one, a join on one, a decimal among a list's values, a distinct decimal and one a set operation combines, and each decimal part of a value or union case compared, and a write's every-column check marks a decimal column's; a dialect with exact hooks writes each as it says, and sends each decimal as its `Value` says (providers.feature: a dialect that is PostgreSQL's with hooks of its own, each marked, has each asked, and the queries find what PostgreSQL finds, 2.0 and 2.00 one group). A computed decimal (an aggregate, arithmetic) is read as a decimal, as a decimal column is, whatever the database keeps it as. A dialect without them writes what was marked as it is: PostgreSQL's SQL is the same, and website/sql.js is unchanged. A dialect with exact hooks refuses a model keyed by a decimal when the repository is made, naming each key: its database compares keys as stored, so 2.0 and 2.00 would be two (a difference between the providers). What SQLite needs to store decimals as exact text. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Sql.fs`, `src/Plasm/Translate.fs`, `src/Plasm/Writes.fs`, `src/Plasm/Repo.fs` |
| 2026-11-21 | Plasm.Sqlite, the second provider, in its first slice: SQLite's dialect, catalogue and DDL; decimals as exact text through registered functions and a collation (`plasm_decimal_compare`, `_same`, the arithmetic, `_sum`, `_avg`, and a window's through `json_group_array`, since a registered function cannot be a window function); and a connection over Microsoft.Data.Sqlite's whose commands hear their token. That driver runs each call on the caller's thread, hears no token while a statement runs (measured: a 30 s count cancelled at 300 ms ran to its end) and waits for a busy database the same way. So a cancelled token interrupts the statement (`sqlite3_interrupt`, only while the statement is the caller's), a busy wait (BEGIN IMMEDIATE, a rollback-journal file's reads and COMMIT) is tried a second at a time hearing the token, and `Sqlite.connectionOf` gives the SqliteConnection itself, without those guarantees. The test suite runs on either database (`PLASM_TEST_PROVIDER`): the plain `dotnet test` is PostgreSQL's run, and a scenario tagged for one database fails at once on the other. `tests/Plasm.Providers/check-tags.py` fails a tag that hides a scenario (postgres-only.txt, sqlite-only.txt), and `differences.txt` lists where the two differ. The SQLite run covers core, providers and the new cancellation feature (3 of 52 feature files); `tests/Plasm.Aot.Sqlite` checks the provider natively (0 IL warnings naming Plasm). Not packed. Red first | `src/Plasm.Sqlite`, `tests/Plasm.Providers`, `tests/Plasm.TestDomain/Database.fs`, `features/cancellation.feature` |
| 2026-11-21 | Kanban's test hosts are stopped with one SIGKILL (`Process.Kill()`), not `Kill(entireProcessTree: true)`, which on Unix stops the process first (SIGSTOP), lists its children and only then kills it (the runtime's StopTree, read from System.Diagnostics.Process 11.0.0): a test run killed in that window left its host stopped, deaf to SIGTERM, until something continued it. That is the cause of the stopped host examined on 2026-11-21 (state T, exiting at once on SIGCONT); the two orphans before it, never examined, are consistent with it. The suspected race in the test-only stdin exit is withdrawn. The leftover check samples the C# hosts' states while their scenarios run and fails on a stopped one (red: 9 or 10 stopped in each of three runs with the tree-kill; green: none in three), and prints a left host's state. A host starts no process of its own, so there is no tree to kill. Test harness only: an application's host is not stopped this way | `examples/Kanban/tests/Hosting/Hosting.cs`, `examples/Kanban/tests/Hosting/no-leftover-hosts.sh` |
| 2026-11-21 | A list column (array-columns.feature) is tested and described as the dialect does it: `List.contains` through `ISqlDialect.ListHas` and `List.isEmpty` through `ListIsEmpty` (providers.feature: a dialect whose tests of a list column are its own is asked for both), its value sent as the dialect sends a list (`ListValue`), its type asked of the dialect as an array type, and a check holding it to its form where its type does not (`IDdlDialect.ListCheck`). Before, `= ANY`, `cardinality` and PostgreSQL's `[]` were written into the core. Under native AOT a list column's arrays are read by typed readers, as other columns are. PostgreSQL's SQL and DDL are the same, and website/sql.js is unchanged. Breaking for a custom dialect: see Changed. Red first | `src/Plasm/Sql.fs`, `src/Plasm/Translate.fs`, `src/Plasm/SchemaCheck.fs`, `src/Plasm/Execution.fs` |
| 2026-11-21 | Plasm.Sqlite stores a list of scalars (array-columns.feature) as one TEXT column holding a canonical JSON array, held to that form by a check (`json_valid` and `json_type` 'array'). Each element is in the form a column of its type holds it, as Microsoft.Data.Sqlite binds it (measured on 11.0.0): a Guid in capitals, a time to its tick, a decimal keeping its scale, an int64 exactly (past 2^53 too), a double's shortest text, a bool as 1 or 0. So `List.contains` finds it (`element IN (SELECT value FROM json_each(column))`, a decimal by value) and a list reads back as written. A double that is not a number is refused, JSON having none. What is not such an array (written by another tool past the check) is a materialization error naming the field, the column and the element. The whole feature runs on both databases, with the Ddl and schema checks of a column's type as pairs; the SQLite run covers 4 of 52 feature files. Red first | `src/Plasm.Sqlite/Lists.fs`, `src/Plasm.Sqlite/Connection.fs`, `features/array-columns.feature` |
| 2026-11-21 | Only UTC times are stored, and a value a provider cannot send is a failure (utc-only.feature). A DateTime whose Kind is not Utc and a DateTimeOffset whose offset is not zero are refused by Plasm before anything is sent, with the same words on every database: a write naming the field, a query, stream, bulk write or prepared query's argument naming the value. Measured on PostgreSQL first: Npgsql sent an Unspecified or Local DateTime as a timestamp without a time zone, which a server in Paris stored as 07:00Z for 08:00, and threw an ArgumentException out of Repo.Transact for a DateTimeOffset at +01:00. Every call the core makes into a driver (to execute, read the next row or result, and a Tx.run step's own commands) marks what the driver throws that is neither a DbException nor cancellation, and that mark is `DbError.Database`, kind OtherDatabaseError, on every provider (PGlite's too, with no code of its own) and under native AOT (tests/Plasm.Aot: a Uri sent by a Tx.run step's command). A hand-written SQL value of a type Plasm does not map is `DbError.Translation (UnmappedType)`. A sweep of what Plasm itself threw on a value in a path that returns a Result found and converted: a prepared query's argument refused when it runs, a null prepared argument, a prepared list's element, an ordered union's prepared argument (each a translation failure), and a key read back during a write that does not convert (a materialization failure); and a list's elements are now converted before duplicates go, since 08:00 Utc and 08:00 Unspecified are equal DateTimes and the refused one was dropped as the other's duplicate. Breaking: see Changed. Red first (9 of 10 scenarios failed: stored shifted, read, or thrown) | `src/Plasm/Model.fs`, `src/Plasm/Writes.fs`, `src/Plasm/Translate.fs`, `src/Plasm.Postgres/Postgres.fs` |
| 2026-11-21 | The Starter (and so the template) takes times with any offset at its door and keeps them as the instants they are, in UTC: `BookingData.BookAsync` converts From and Until, and its clock is read in UTC whatever offset it answers with (`ClockStart` written with one included). Before, a booking posted at +01:00 threw out of the write on Plasm.Postgres 0.1.0, which the Starter is pinned to, and would be refused by the UTC check. Tests: a booking at +01:00 reads back as the same instants at offset zero; one at +01:00 overlapping a UTC booking is refused (and a conversion keeping the clock reading but dropping the offset fails both); a clock at +01:00 stores Since as that instant at offset zero. Red first: the two writes threw on 0.1.0 | `examples/Starter/src/Starter.Data/BookingData.fs`, `examples/Starter/src/Starter.Api/Program.cs` |
| 2026-11-21 | A `[<Json>]` document's times are UTC, as a column's are: a DateTime not of Kind Utc or a DateTimeOffset off the offset zero anywhere in a document (a record's field, a list's element, an option's value, nested) is refused before anything is sent, as an invalid change of the field naming its path in the document (`at [0].note.seen in the document`); a query looking for such a time in a document's list is a translation failure. Before, the document writer read an Unspecified DateTime as the client machine's local time (ToUniversalTime) and stored it shifted by that machine's zone, kept a DateTimeOffset's offset, and the query found nothing. A document's DateTime reads back of Kind Utc and its DateTimeOffset at the offset zero, pinned (a reader that kept the parsed Kind fails it). Red first: three writes stored, the query read 0 rows; comparing an element's time inside `List.exists` was already refused by the column path, and is pinned with them | `src/Plasm/Schema.fs`, `src/Plasm/Writes.fs`, `features/json-columns.feature` |
| 2026-11-21 | Plasm.Sqlite stores times as PostgreSQL does (utc-only.feature, now on the SQLite run): a DateTime reads back of Kind Utc and a DateTimeOffset at the offset zero; each is kept to the microsecond as Npgsql sends it (the ticks since 2000-01-01 divided by ten, towards zero, so a time before 2000 loses its tenth of a microsecond upwards: measured on PostgreSQL 18, pinned on both), so a query by the value written finds it; the TEXT is one form (`yyyy-MM-dd HH:mm:ss`, a fraction of up to six digits without trailing zeros, `+00:00` after a DateTimeOffset), which orders as the instants do, held to it by a check in Ddl, and text in another form, written past the check, is a read error naming the column. List elements are the same form. `IDdlDialect.ListCheck` (unreleased, B17) is now `ColumnCheck(column, providerType)`, answering for lists and times. Red first: 10 of 23 failed on SQLite (Kind Unspecified, seven-digit fractions kept, the check absent, other forms read) | `src/Plasm.Sqlite/Times.fs`, `src/Plasm.Sqlite/Connection.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-21 | A value the database cannot hold is refused before it is sent, through the dialect (`ISqlDialect.Refusal`): on SQLite, a double that is not a number (Microsoft.Data.Sqlite cannot store one, measured: "Cannot store 'NaN' values.") and, in a list, which is JSON, infinity too. A write is refused naming the field; a query, and a prepared argument when it is bound, are translation failures. Before, a list holding NaN threw ArgumentException out of Transact (the list is made JSON while the SQL is rendered, outside the driver's calls), and a scalar NaN written or compared was a database failure. PostgreSQL's and PGlite's dialects refuse nothing. A prepared argument is asked when bound, not when rendered, as it must stay one parameter wherever it is written. Red first: three SQLite scenarios (a THROWN ArgumentException, two database failures) | `src/Plasm/Sql.fs`, `src/Plasm/Writes.fs`, `src/Plasm/Execution.fs`, `src/Plasm.Sqlite/Lists.fs`, `features/array-columns.feature` |
| 2026-11-21 | CI's SQLite run was red on Linux only (run 44922569197): core.feature's 100 Orders were written by the specs' own SQL, which goes round Plasm on a raw connection and bound `DateTime.UtcNow` as Microsoft.Data.Sqlite does, to seven digits of a second, which Plasm.Sqlite's reader refuses. Linux's clock has 100 ns ticks; the Mac's gives whole microseconds, so every Mac run passed. Plasm's own writes round at send, and 87's scenarios wrote through them. The specs' raw SQL now stores a time as the provider does (as it already stored a decimal). Pinned without the clock, on both databases: a time with a tenth of a microsecond written by an insert, an update, a bulk insert, a bulk update, a transaction's own command and the specs' own SQL reads back to the microsecond and is found by the value written, in a query and through a prepared query; and in a list. Red: the specs' own SQL, on SQLite only, the path CI hit. A time in another form, sent past Plasm, stays a read error, which now says a command sent past Plasm (on `Sqlite.connectionOf`, or by another tool) must write a time to the microsecond in UTC. tests/Plasm.Providers/run-linux.sh, which reproduced the failure in 62 s, now runs from an exported tree and is part of the full check for any change to Plasm.Sqlite or the SQLite run; sqlite-run.sh strips the colours dotnet test writes on CI, which hid its totals | `tests/Plasm.TestDomain/Database.fs`, `src/Plasm.Sqlite/Times.fs`, `features/utc-only.feature`, `tests/Plasm.Providers/run-linux.sh`, `tests/Plasm.Providers/sqlite-run.sh` |
| 2026-11-21 | A column's check is asked of the dialect once, by what the column holds: `IDdlDialect.ColumnCheck(column, holds)` with `ColumnHolds` (`Scalar` of a provider type, `List` of an element type, `Document`, `Enum` with its labels), in place of `EnumCheck(column, labels)` and `ColumnCheck(column, providerType)`, neither released. An enum's check is printed before the other columns' as before. The DDL is the same: PostgreSQL's and PGlite's dialects answer None as they did, and SQLite's DDL of every model in the specs' steps, printed before and after, is byte for byte the same (23 models, 16 list checks, 11 time checks; 4 refused alike). No SQLite model prints an enum check yet: an enum with a store type is refused on SQLite, and one stored as text has no store type, so the enum check still owed classifies a text enum as `ColumnHolds.Enum` without changing the signature | `src/Plasm/Sql.fs`, `src/Plasm/SchemaCheck.fs` |
| 2026-11-21 | Text is matched as it is written on every database (text-matching.feature, on the SQLite run): `StartsWith`, `EndsWith` and `Contains` keep the text's case and every character as itself. On SQLite, Plasm wrote `LIKE`, which ignores ASCII case there: `StartsWith "ada"` also found "Ada Lovelace", silently. The dialect now writes the match (`ISqlDialect.TextMatch`, `LikeEscape` gone): SQLite's is `GLOB`, which keeps case, its `*`, `?` and `[` written `[*]`, `[?]` and `[[]`; PostgreSQL's is the `LIKE` it was (website/sql.js unchanged). Pinned on both: ASCII and non-ASCII case (É, é), each wildcard of both dialects in the text, a prepared argument, an empty text. Red first: 7 of 15 on SQLite, every one a case match (non-ASCII letters were already told apart, as SQLite's LIKE folds ASCII only); a mutation dropping the GLOB escapes fails the three GLOB wildcards | `src/Plasm/Sql.fs`, `src/Plasm/Translate.fs`, `src/Plasm.Sqlite/Sqlite.fs`, `features/text-matching.feature` |
| 2026-11-21 | JSON documents on SQLite (json-columns.feature, on the SQLite run): a `[<Json>]` field is TEXT holding JSON, held to it by `check (json_valid(column))` in Ddl (`ColumnHolds.Document`), and queried through SQLite's JSON functions (`SqliteJson`): `->` for a member, `->>` for a scalar's text, `json_each` for a list's elements, and a list containing another as every wanted element among the held ones, by type and value. The feature's tables now come from `repo.Ddl()` on both databases, where they were written in PostgreSQL's SQL; its jsonb DDL and schema-check scenarios are PostgreSQL's, with SQLite siblings (TEXT columns with their check; a column declared INTEGER reported). Red first: every scenario on SQLite (the tables), then the DDL sibling (no check). Containment's type comparison is not pinned: a document Plasm writes from its model never holds the value of one type where another is looked for, and SQLite's `json_each` already tells 3 from '3'; it stays for a document written past Plasm, which also fails to read | `src/Plasm.Sqlite/Sqlite.fs`, `features/json-columns.feature`, `tests/Plasm.Specs.Steps/JsonSteps.fs` |
| 2026-11-21 | A window's SUM and AVG of decimals on SQLite are linear: `plasm_decimal_sum` and `plasm_decimal_avg` are SQLite window functions of Plasm.Sqlite's own (Windows.fs), registered by calling `sqlite3_create_window_function` in the e_sqlite3 library the driver loads (Microsoft.Data.Sqlite and SQLitePCLRaw 3.0.5 register none), with blittable arguments and callbacks from `Marshal.GetFunctionPointerForDelegate<T>`; the same functions are the plain aggregates, and the JSON form (`json_group_array` per row, `plasm_decimal_json_sum`/`_avg`) is gone. A running sum over 10,000 rows takes 16 ms where it took 6 s, and 151 ms over 100,000 (measured on this machine, not idle). A callback's failure is its query's error (`DbError.Database`, naming the value) and the connection goes on; each window's total is taken out by xFinal, a failed or cancelled query's too. Pinned on both databases: a running sum and a whole window's sum and average; on SQLite, the functions over a frame rows leave and one that empties, and an unreadable value; natively (tests/Plasm.Aot.Sqlite), the same frames, the failure, and no total left after a cancelled window (a mutation keeping totals leaves 7). Red first: the SQL (json_group_array) and SQLite's "may not be used as a window function" | `src/Plasm.Sqlite/Windows.fs`, `src/Plasm.Sqlite/Sqlite.fs`, `features/providers.feature` |
| 2026-11-21 | Constraint identity as structure (B12): a violation names its constraint by name or by table and columns (`DbFailure.Table`, `Columns`), and is the field's error either way. SQLite names no unique constraint or foreign key: it gives a unique violation's table and columns, and Plasm.Sqlite finds a foreign key's by running the failed statement again under a savepoint with the check deferred, taking the violation `pragma_foreign_key_check` finds after it and not before (one the database already held never takes the failure), and rolling back (ForeignKeys.fs; two scans of the foreign keys, on that failure only). It is not run again in a database with triggers, once the caller's token is cancelled, or to a different end: then the failure stays unnamed. The savepoint is released and defer_foreign_keys set back whatever the re-run meets, which the native check (tests/Plasm.Aot.Sqlite) proves through a seam for a re-run as it was, one failing again, one adding no violation and one whose token is cancelled; it does not hear the token, so no interrupt meets its cleanup. The SQLite catalogue reports any unique index by Plasm's name, since none is misnamed there. constraints.feature joins the SQLite run: its violations on both; its four misnamed-constraint scenarios PostgreSQL's (postgres-only.txt), covered on SQLite by three scenarios of a unique index and a foreign key of other names; a violation held before (an order line written with foreign keys off) takes no new failure's field, on both; a database with triggers keeps the failure unnamed. Red first: 8 of 13 failed on SQLite; the first re-run, which took the file's first violation, put a line's missing product on Lines[0].OrderId. Mutations each fail: no re-run (3 scenarios), matching by name only (5), the old catalogue (1), no trigger guard (1), no RELEASE and no defer reset (the native check, every case). Breaking for code that builds a DbFailure: see Changed | `src/Plasm/Errors.fs`, `src/Plasm/Writes.fs`, `src/Plasm.Sqlite/ForeignKeys.fs`, `features/constraints.feature` |
| 2026-11-21 | A recursive query's cycles are stopped as the dialect stops them (`ISqlDialect.Cycle`, B6): PostgreSQL by its CYCLE clause, as before, its SQL the same; SQLite, which has none, by a path each row carries, the JSON array of the keys above it (`json_array(json_array(id))`, extended by `json_insert`), a step's row whose key the path of the row it follows holds being left out. The CYCLE text leaves the core (tests/Plasm.Boundary/allowed.txt: 2 allowances). recursive-queries.feature joins the SQLite run, its table made by Ddl there and its cycle bounded by a token cancelled after 5 seconds where PostgreSQL's connection has a statement timeout; a recursive query's lock is refused on SQLite as every lock is (a sibling scenario). Red first: 6 of 12 failed on SQLite, near "CYCLE"; a path that never holds a key runs the cycle until the token ends it (mutation). Breaking for a custom dialect: see Changed | `src/Plasm/Sql.fs`, `src/Plasm.Sqlite/Sqlite.fs`, `features/recursive-queries.feature` |
| 2026-11-21 | A column's check is the model's (column-checks.feature), with the enum check SQLite owed: a union stored as its cases' names, as text, is held by Ddl to them (`ColumnHolds.Enum`), so SQLite's Ddl writes `constraint t_c_check check (c in ('open', 'done'))` for it as for an enum type; PostgreSQL's Ddl writes no column check, and its DDL is the same. The schema check reads every named column check Ddl writes back from the catalogue (`ICatalogueDialect.Checks`: SQLite's tables' text) and compares it, without case or spaces outside quotes: an enum's check that leaves out a case's name (writing it would fail) or allows a name no case has (reading it would fail) is reported by label, as an enum type's labels are; any other check that differs is an incompatible type. A check of the same names in another order is not reported. A column without its check is reported, with the check to add, where PostgreSQL's type would hold the value (an enum type, a list, a document, a time), the check being SQLite's only equivalent; a union stored as text, which nothing holds on PostgreSQL either, is not. Red first: 6 of 7 failed on SQLite, and the missing time check, added after, failed until reported; mutations each fail scenarios (no text enums: 5; no comparison: 3; no normalisation: 1). Breaking for a custom catalogue: see Changed | `src/Plasm/SchemaCheck.fs`, `src/Plasm.Sqlite/Sqlite.fs`, `features/column-checks.feature` |
| 2026-11-21 | A transaction at an isolation level the provider cannot run at is refused before anything is sent (`ProviderCapabilities.IsolationLevels`, providers.feature): Chaos on both databases, and Snapshot on SQLite, are `UnsupportedByProvider` naming the level, where each raised (Npgsql's NotSupportedException; SQLite's InvalidOperationException). PostgreSQL's levels are Npgsql's, its Snapshot REPEATABLE READ as before. Red: without the refusal both scenarios raise on SQLite and the Chaos one on PostgreSQL. Breaking for a custom provider: see Changed | `src/Plasm/Repo.fs`, `src/Plasm/Sql.fs` |
| 2026-11-21 | Ten more feature files on the SQLite run, 20 of 55 (sqlite-features.txt): distinct, reading, tx-steps and window-functions as they were; joins, bulk-writes and query-semantics with their SQL asserted as the dialect writes a decimal's comparison and sum (`plasm_decimal_compare`, `plasm_decimal_add`), the negated comparison's sibling saying no index serves it there (differences.txt); referring with its table made by Ddl; set-operations' locked union refused on SQLite as every lock is (a sibling); error-model's row changed by a trigger PostgreSQL's (postgres-only.txt: SQLite's RETURNING gives the row as written). run-linux.sh builds in one MSBuild node, none kept, where under x64 emulation its worker nodes hung the build for two hours | `tests/Plasm.Providers/sqlite-features.txt`, `tests/Plasm.Providers/run-linux.sh` |
| 2026-11-21 | Kanban's `tests/Hosting/no-leftover-hosts.sh` kills a spec run only once one of its hosts is up, and a run that starts no host within the wait (180 s, the run built beforehand) is its own failure, "no host started within N s", shown with the run's own output, where before it was a fail of the leak condition with the output thrown away. One full check had found no host in 60 s; the run's output, now kept, shows a run still starting its PostgreSQL containers. A host left behind is still a failure, named. Proved both ways with test-only plants (`KANBAN_LEFTOVER_PLANT=leak`, `=never`). The orphaned C# host seen twice (cause unproven; suspected race in the test-only stdin exit) is a separate item | `examples/Kanban/tests/Hosting/no-leftover-hosts.sh` |
| 2026-11-21 | The Starter, and so the template, on Plasm.Postgres 0.1.0 from nuget.org, the first release that is not a pre-release: a version bump from 0.1.0-rc.1, nothing else. Restored from nuget.org alone: 11 tests at runtime, the API published natively warning only IL2104 and IL3053, and the tests published and run natively, 11 passing | `examples/Starter/Directory.Packages.props` |
| 2026-11-21 | Kanban's `tests/Hosting/no-leftover-hosts.sh` sees the C# host again. Since the C# host's scenarios start it from its own output (src/Host.CSharp/bin), the script, which looked for hosts in the scenarios' own output, counted none: a leftover C# host would have gone unseen, and the case of a run killed mid-scenario failed, finding no host up to kill. The host's output is now one MSBuild property, `KanbanHostOutput`, which the scenarios read through their assembly and the script asks MSBuild for. Red: the script as it was exits 1, "0 host(s) up when killed". Green: 1 up when killed, 0 left. A mutation in which hosts no longer exit when their standard input closes leaves 1 after a killed run, and the script exits 1. It was not in the repository's full check, which is why its failure went unseen; it is now | `examples/Kanban/tests/Hosting/no-leftover-hosts.sh` |
| 2026-11-21 | The Starter, and so the template, on Plasm.Postgres 0.1.0-rc.1 from nuget.org, the first release whose `TxFailure.Conflict` carries the stale record's key: its 409 names the equipment from the key the failure carries (`Conflict(_, (:? EquipmentId as EquipmentId stale))`), not from the id the caller sent, so `BookingData`'s change no longer takes the id. Restored from nuget.org alone: 11 tests at runtime, the API published natively warning only IL2104 and IL3053, and the tests published and run natively, 11 passing | `examples/Starter` |
| 2026-11-20 | Recursive queries (recursive-queries.feature): `Query.recursive (start, fun found -> plasm { for c in tableOf<Category> do; join p in found on (c.Parent = Some p.Id); select c })` is a query of the rows `start` finds and, again and again, of the rows the step reaches from the rows found so far. One statement: WITH RECURSIVE ... UNION ALL, with CYCLE on the record's key, read as a source like a set operation, so the query made from it filters, orders and pages the whole. `Query.depth c` is how many steps found c, 0 for the start's rows, and the select and the filter read it. Everything under a row and everything above it are the same form, the step joining `found` from either side. A cycle in the data ends: a row already on its path is not followed again, so a root given a parent under it is read once round. Each path to a row is a row (UNION ALL): in a tree there is one. Refused when translated, each with what to do instead: a start or a step that orders or pages, a step that does not join `found` once, groups, takes distinct rows, locks or binds loads, a lock on a recursive query, `Query.depth` on a source that is not recursive; the start-up check names a refused recursive query in its assembly. CYCLE needs PostgreSQL 14 or later: Plasm is checked on PostgreSQL 18 and states no older version it supports, and on PostgreSQL 13 a recursive query is the database's own syntax error, `DbError.Database` (OtherDatabaseError, 42601), never a statement that does not end. The cycle scenario reads on a connection whose statements time out after 5 seconds and checks that the statement stops a cycle by the key; with CYCLE taken out of the rendering it fails in 5.6 s on the timeout. 12 scenarios, whose red run (on stubs of Query.recursive and Query.depth) was made after the implementation; the native run reads a tree with depth | `src/Plasm/Query.fs`, `src/Plasm/Translate.fs`, `src/Plasm/Sql.fs` |

## Suggested next steps

1. Publish pre-release packages (the CI already builds them); the licence is
   MIT and the repository is public.
2. `Query.prepare3` for projections.
3. A second provider, to prove the `IProvider` / `ISqlDialect` boundary
   (SQLite, under way): every construct a provider renders differently is
   behind the dialect now. `src/Plasm.Sqlite` is in the repository and is
   not packed or released (the package check fails if it is). The SQLite run
   covers 4 of 52 feature files (`tests/Plasm.Providers/sqlite-features.txt`);
   "the SQLite run passed" means those, not every feature.
4. Open question: hosting. `Repo.Start` is asynchronous, and ASP.NET Core's
   service registration is synchronous, so Kanban's Host.fs starts the
   repository after the migrations and hands it to the services through a
   `TaskCompletionSource`. Whether Plasm should offer something for that (a
   hosted service, or a registration helper in a `Plasm.AspNetCore` package)
   is undecided; nothing is built.
5. Bring the browser koans' first runnable koan back under 20 s on a 25 Mbit/s
   connection. Measured 2026-11-15 at 19.2 to 20.1 s with 24.1 MB transferred
   (about 7.7 s of transfer); the budget in `course-in-the-browser.feature` is
   25 s until then.
6. Close the gaps found by the comparison with the F# data-access libraries
   ([comparison.md](comparison.md)): typed parameters (such as `jsonb`) and
   several result sets in raw SQL; then the larger EF Core gaps listed there.
