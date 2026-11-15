# Status: verified behaviour, measurements and gaps

*As of 2026-11-14: the one style.*

**Plasm is not production-ready.** It is designed and verified end-to-end
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
| `tests/Plasm.Specs` (Reqnroll, real PostgreSQL 18) | Every scenario of `features/*.feature`: the user's two specifications (`core.feature`, `elegance.feature`, with scenarios removed or rewritten for the one style, listed below) and every feature added since, each written and run red before it was built. See `docs/acceptance.md`. | 358 / 358 pass (outline examples counted separately; 322 titles) |
| `tests/Plasm.Tests`: `Translation.fs` | SQL-shape contracts (options, membership, `exists`, `List.exists`/`forall` over owned lists, pagination), unsupported-expression errors, model-configuration errors | 42 pass |
| `tests/Plasm.Tests`: `CompileTime.fs` | Typed API acceptance and rejection, checked with FSharp.Compiler.Service against the real assemblies; each rejection pins its error code and has a passing control snippet | 15 pass |
| `tests/Plasm.Tests`: `Properties.fs` (FsCheck, real database) | Predicate-order equivalence matching F# semantics; extending a query never alters it; an update records exactly the differing fields; the same aggregates for every batch size, with key-proportional command counts; owned lines keep their declared order per owner | 5 properties pass |
| `tests/Plasm.Tests`: `EctoCompat.fs` (real database) | Adapted Ecto repo and multi cases, including one deliberate difference. See `docs/ecto-compatibility.md`. | 14 pass |
| `tests/Plasm.Tests`: `Joins.fs` | Join SQL shape: ON conditions, composite keys, filters before and after joins, left-join option semantics, ordering, projections, misuse errors | 12 pass |
| `tests/Plasm.Tests`: `Behaviour.fs` (real database) | Edge paths: `One`, projections, materialisation errors, snapshot reads, key and version changes refused, owned items of another owner refused and rolled back, `Tx.traverse`, cancellation | 11 pass |
| `tests/Plasm.Tests`: `Batch2.fs` (SQL shape and real database) | Aggregates and grouping errors; bulk misuse errors, foreign-key failures, a 40,000-row insert split into two commands that roll back together, composite conflict targets; raw SQL with list, option and domain parameters; prepared patterns; join-table type checks | 26 pass |
| `tests/Plasm.Tests`: `ConventionCost.fs`, `Smoke.fs`, `WriteSmoke.fs` | A value read by convention allocates no more than a registered mapping (three types); a page read and a write path end to end | 5 pass |
| `koans/Plasm.Koans.Specs` | Every koan (22 lessons, 224 koans) passes with its reference answer and stops at its blank as shipped; the notebook runs in a hosted session and a real kernel | 249 checks pass |
| `koans/browser` | The browser koans (PGlite) | 119 scenarios pass |

Run the library's tests with `dotnet test` from the repository root (Docker is
required, or set `PLASM_TEST_PG`): 358 scenarios and 128 unit tests.

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

**Writes** (only through `repo.Transact`):

- `Tx.insert record` (gives it back as stored, with its generated key and what
  it owns), `Tx.update (original, changed, ?expectVersion)` (only the columns
  that differ), `Tx.delete record`.
- Concurrency: a record with a `version` field is checked by its version
  (`expectVersion` for a copy that came back from a client); a record without
  one is checked against every column of `original` (`IS NOT DISTINCT FROM`)
  in the same command. A failed check is `TxFailure.Conflict`.
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
  (columns in order) or a scalar.
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

- `repo.CheckQueries(assembly, ?inNamespace)` translates every module value of
  type `Query<_>` or `Projection<_>` and every prepared query an assembly
  defines, without running them, and reports each failure by its name.
- `repo.CheckSchema()` reports every `SchemaIssue`: missing tables and columns
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
  `InvalidChanges (entity, errors)`, `Conflict entity`, `Failed of DbError`, or
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
  (`ArrayParameters`, `Returning`, `Upserts`, `RowLocking`, `SnapshotReads`).
  A missing capability is refused before execution.
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
  is an experimental spike and is not packaged).
- Migrations: Plasm prints the DDL the model expects and checks the database
  against the model, but generates no migration. Keep your migration tool.
- Right and full outer joins, joins on non-equality conditions, joins to
  subqueries.
- `distinct`, subqueries in `select`, unions, CTEs and window functions (use
  `Raw.fragment` or `repo.Sql` / `Tx.sql`).
- The latest few rows per parent (top-N per group): dropped on purpose with
  `limitPerParent`; write it as hand-written SQL.
- Join tables with extra columns (model the link as its own record), composite
  keys, JSON columns.
- Bulk writes over joins (`UPDATE ... FROM`), `RETURNING` from bulk updates,
  streaming (`IAsyncEnumerable`) results.
- `Query.prepare3` for projections.

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
- What a record owns or links is always read with it: every query of the owner
  pays one more command per list. A relationship read only sometimes is
  another aggregate, reached by a query.
- Violations are mapped to fields by constraint name. A constraint under
  another name stays a database failure (`TxFailure.Failed` with
  `DbFailureKind.UniqueViolation` / `ForeignKeyViolation` / `CheckViolation`);
  `CheckSchema` reports a misnamed unique constraint or foreign key, but check
  constraints are not stated in the model and are not checked.
- Only queries defined as module values are found by `repo.CheckQueries`; a
  query built inside a function is checked when it is first used.
- Plasm depends on FSharp.Core 8.0 or later (`FSharp.Core >= 8.0.403`), so it
  loads into hosts with an older FSharp.Core than the SDK's, such as .NET
  Interactive notebook kernels.
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

### Two capabilities dropped on purpose

- **Top-N children per parent** (`limitPerParent`, a window function per
  parent). The latest few rows per parent is a report, not part of an
  aggregate: write it as hand-written SQL (`repo.Sql` / `Tx.sql`).
- **Choosing per query which relationships to load** (`preload`,
  `replacePreload`, `repo.Preload`). An aggregate always loads whole; another
  aggregate is reached by a join, `exists`, or a second query (for a page, a
  prepared query with `List.contains` over the page's ids).

### Still unproven

- Nothing has run in production.
- `examples/Kanban`, the reference application, is untracked in git until the
  user decides to commit it.

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

## Suggested next steps

1. Decide whether to commit `examples/Kanban`, the reference application.
2. Choose a license, add the repository URL to the package metadata, and
   publish pre-release packages (the CI already builds them).
3. `Query.prepare3` for projections.
4. A second provider, to prove the `IProvider` / `ISqlDialect` boundary
   (SQLite or SQL Server); move the catalogue queries and the link SQL behind
   it.
5. Diagnostics as OpenTelemetry activities.
6. Close the gaps found by the comparison with the F# data-access libraries
   ([comparison.md](comparison.md)): typed parameters (such as `jsonb`) and
   several result sets in raw SQL; then the larger EF Core gaps listed there.
