# Status: verified behaviour, measurements and gaps

*As of 2026-11-12: first delivery, joins, batch 2a (aggregates, bulk writes, hand-written SQL, prepared queries) and batch 2b (many-to-many, schema check, test sandbox).*

**Plasm is not production-ready.** The core is designed and verified
end-to-end against PostgreSQL:

- typed immutable queries, joins, aggregates and grouping
- nested batched relationship loading
- changesets
- transactional writes, set-based bulk writes and upserts
- typed escape hatches to hand-written SQL
- prepared queries that are built and planned once
- many-to-many relationships, a start-up schema check and rolled-back test
  sandboxes

Its scope is deliberately narrow, its performance has had one tuning pass, and
it has had no production use. Its design comes from Ecto, but Ecto's
operational maturity does not transfer. That has to be earned.

## Verification

| Suite | What it proves | Result |
|---|---|---|
| `tests/Plasm.Specs` (Reqnroll, real PostgreSQL 18) | All 65 supplied Gherkin scenarios (64 as specified, 1 with a documented deliberate change), 13 join scenarios, 37 batch-2a scenarios (aggregates 10, bulk writes 12, hand-written SQL 8, prepared queries 7) 20 batch-2b scenarios (many-to-many 9, schema check 6, sandbox 5) 16 F# Interactive scenarios (run in a real hosted FSI session) and 78 scenarios from the two architecture-audit follow-ups (query semantics, reading one row, transaction steps, pipeline forms, error model, has-one, providers, and new rules for prepared queries, sandboxes and FSI), each feature written and run red before it was built. See `docs/acceptance.md`. | 229 / 229 pass; repeated reruns passed (timing-sensitive scenarios: snapshot interleaving, cancellation, serialisation retry) |
| `tests/Plasm.Tests`: `Translation.fs` | SQL-shape contracts, unsupported-expression errors, model-configuration errors | 51 pass |
| `tests/Plasm.Tests`: `CompileTime.fs` | Typed API acceptance and rejection, checked with FSharp.Compiler.Service against the real assemblies; each rejection pins its error code and has a passing control snippet | 17 pass |
| `tests/Plasm.Tests`: `Properties.fs` (FsCheck, real database) | Predicate-order equivalence matching F# semantics; extending a query never alters it; `edit` diffs exactly; equivalence across batch sizes with key-proportional command counts; child ordering per parent | 5 properties pass (mutation-checked by the test author: deliberately broken checks fail) |
| `tests/Plasm.Tests`: `EctoCompat.fs` (real database) | About 40 adapted Ecto cases (preload, repo, changeset, multi), including 3 asserting deliberate differences. See `docs/ecto-compatibility.md`. | 41 pass |
| `tests/Plasm.Tests`: `Joins.fs` | Join SQL shape: ON conditions, composite keys, filters before and after joins, left-join option semantics, ordering, projections, misuse errors | 13 pass |
| `tests/Plasm.Tests`: `Behaviour.fs` (real database) | Edge paths: projections, materialisation errors, snapshot, `forKey`, child-write ownership and rollback, replacement policies, transaction naming, cancellation | 20 pass |
| `tests/Plasm.Tests`: `Batch2.fs` (SQL shape and real database) | Aggregate SQL and domain-typed results, grouping errors, count/exists over joins, pages and groups; bulk misuse errors, foreign-key failures, a 40,000-row insert split into two commands that roll back together, composite conflict targets; raw SQL with list, option and domain parameters; prepared patterns, three arguments, transactions, negative counts; many-to-many configuration, duplicate and cleared links, relationship-only updates; sandbox transaction failures; join-table type checks | 26 pass |

Run everything with `dotnet test` from the repository root (Docker is
required, or set `PLASM_TEST_PG`). 405 tests (229 scenarios, 176 unit) take
about 30 s together on the development machine.

## Supported behaviour

**Mapping:**

- F# records only.
- Built-in scalars: `int16`, `int`, `int64`, `string`, `bool`, `decimal`,
  `float`, `float32`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`,
  `TimeOnly`, `TimeSpan` and `byte[]`, each optionally as `option`.
- Explicit domain scalars: `Scalar.wrap` (single-case wrappers),
  `Scalar.tryWrap` (validating constructors) and `Scalar.enum` (fieldless
  unions as text). Unions with fields, nested records, collections as columns
  and nested options are configuration errors.
- Single-column primary keys, assigned or database-generated.
- Database-generated columns (`generated`) and an integer version for
  optimistic concurrency.
- Default snake_case column names, overridable per field.

**Relationships:**

- `hasMany` and `belongsTo` (required or optional) are verified for reading,
  filtering and loading.
- `manyToMany (fun a -> a.Tags) "article_tags" "article_id" "tag_id"`: loaded
  with one command per batch of owner keys through the join table (filters,
  ordering, `limitPerParent` and nested loads apply), and filtered with
  `Rel.any` / `Rel.all` (`EXISTS` through the join table). A target linked to
  several owners is materialised once. Links are written with
  `Changeset.putLinks` (the complete set; the result has it loaded),
  `addLinks` and `removeLinks` (the result is `NotLoaded`). Only join-table rows
  are written, in the write's transaction; targets must already exist (a
  missing one is a classified foreign-key failure). The link SQL uses
  PostgreSQL's `unnest`.
- `hasOne` (`Assoc<'T option>`, key on the related record) loads in one
  batched command; more than one related row is a materialisation error naming
  the owner's key. `Rel.exists` filters through an optional relationship.
- Child writes go through `hasMany` only.

**Queries:**

- Operations: `where`, `orderBy` / `orderByDescending` / `thenBy` /
  `thenByDescending`, `take` and `skip` (ordering required; the primary key is
  appended as a tiebreaker), and `select` projections (tuples, records,
  anonymous records, and arithmetic over columns).
- Predicates:
  - comparisons, `&&`, `||`, `not` and arithmetic
  - equality with `None` (`IS NULL`), and option-to-option equality
    (`IS NOT DISTINCT FROM`)
  - `.IsSome`, `.IsNone`, `.Value` and `Option.get`/`isSome`/`isNone`
  - `List`/`Seq`/`Array.contains` and instance `Contains` over captured
    collections (`= ANY`, or `FALSE` when empty)
  - `StartsWith`, `EndsWith` and `Contains` (escaped `LIKE`)
  - `ToLower`, `ToUpper`, `Trim` and `Length` on strings
  - `Rel.any`, `Rel.all` and `Rel.exists`, as correlated `EXISTS` /
    `NOT EXISTS`, verified against PostgreSQL
- Everything else that depends on a row is rejected with
  `UnsupportedExpression`.

**Joins:**

- `join c in Db.customers on (o.CustomerId = Some c.Id)` (inner) and `leftJoin`
  (the joined record is `'T option`), chained into flat tuples
  (`Order * OrderLine * Product`). Composite keys: `on ((a.X, a.Y) = (b.X, b.Y))`.
- Keys compare with SQL `=`, so NULL keys never match.
- Filters and orderings over every source; filters written before a join take
  the root record. Left-joined sources use option semantics: `c.IsNone`,
  `c = None`, `Option.exists`, `Option.forall`, and `Option.map` / `Option.bind`
  to a field (a nullable column).
- `take` / `skip` require an ordering; the key of every source is appended as a
  tiebreaker.
- Results are tuples of whole records (relationships `NotLoaded`), or
  projections that read only the named columns. One command, no preloads:
  `preload` on a joined query is a planning error pointing to `repo.Preload`.
- `repo.All`, `repo.Explain` and `Tx.all` accept joined queries.

**Counts and aggregates:**

- `repo.Count` (one row transferred; a page counts the page, a grouped query its
  groups) and `repo.Exists` (reads at most one row).
- `Agg.count`, `countDistinct`, `sum`, `min`, `max` and `avg` in `select`,
  `having` and orderings, translated to SQL. `Agg.sum` of no rows is 0, as
  `List.sum []`; `min`, `max` and `avg` return options. `sum` needs `+` and
  `Zero`, `avg` needs `DivideByInt` (the `List.average` constraint), so summing
  text or averaging an integer key does not compile. Domain types come back as
  domain types (`Agg.max o.Id : OrderId option`).
- `groupBy key` (a tuple for several keys), `having`, grouping across joins,
  ordering by aggregates. Paged groups get the grouping keys as a tiebreaker.
- `repo.One(projection)` for a single aggregate row.
- Checked when planned, not at compile time: selecting or ordering by a column
  that is neither grouped nor aggregated, aggregates in `where`, nested
  aggregates, and grouped queries without `select`.

**Loads:**

- `preload` / `replacePreload`, with child queries that can filter, order, use
  `limitPerParent` (window function) and nest preloads.
- Identical requests merge; conflicting ones are a `LoadConflict`.
- Batched `= ANY(@keys)` commands, with a configurable `MaxKeysPerCommand`.
- `repo.Preload` onto existing values.
- `ReadConsistency.Snapshot`.

**Writes:**

- `Changeset.insert` / `update` / `edit` / `forKey` / `delete`
- `set`, `clear`, `cast` and `castWith`
- Validations: `validateRequired`, `validateLength`, `validateOptionalLength`,
  `validateRange`, `validateInclusion`, `validateFormat`, `validate` and
  `addError`
- Constraint mapping: unique, foreign key and check
- `Changeset.check model`
- `putChildren` and `replaceChildren`, with `ReplaceScope` and `OnReplace`

**Bulk writes** (`BulkWrite<'R>` values, run with `repo.Execute` or
`Tx.execute`, inspected with `repo.Explain`):

- `Bulk.update(query, fun o -> { o with ... })`: one `UPDATE`; assigned values
  may be computed from the row. Fields not assigned are not written; the version
  is incremented. Returns the row count.
- `Bulk.delete query`: one `DELETE`; returns the row count.
- `Bulk.insert records`: multi-row `INSERT ... RETURNING`, split into several
  commands in one transaction above 65,000 parameters; an empty list sends
  nothing.
- `Bulk.upsert(records, on, fun current incoming -> { current with ... })`
  (`ON CONFLICT ... DO UPDATE`) and `Bulk.insertMissing(records, on)`
  (`DO NOTHING`, returning only inserted rows). Conflict targets can be tuples.
- No changeset validation runs. Database constraints apply and failures are
  classified; each statement is atomic.
- Rejected before execution: joins, ordering, paging, loads or grouping in the
  query; assigning the key, version, generated fields or relationships; changes
  that assign nothing.

**Hand-written SQL:**

- `repo.Sql<'R>($"...")` / `Tx.sql`: rows as a mapped record (columns by name,
  relationships `NotLoaded`), an unmapped record (fields by name or snake_case
  name), a tuple (columns in order) or a scalar. Numeric results convert to the
  requested type.
- `repo.ExecuteSql($"...")` / `Tx.executeSql`: affected row count.
- The SQL is a `FormattableString`, and every interpolated value is a
  parameter, including options, domain scalars and lists (one array parameter
  for `= ANY(...)`). A plain string does not type-check.
- `Raw.fragment<'T> "{0} ILIKE {1}" (c.Name, pattern)` inside typed queries. The
  SQL is typed as an F# format string, so only a literal compiles; write `%%`
  for `%`.
- Missing columns and wrong tuple arity are materialisation errors that name
  the field.

**Prepared queries:**

- `Query.prepare` / `prepare2` / `prepare3 (fun (c: Arg<CustomerId>) -> plasm {...})`
  returns a function to `Query<'T>`. The query is built once and planned once
  per repo; a call only binds arguments, and every call sends the same SQL
  text.
- Arguments (`c.Value`) work in filters, comparisons, `List.contains`, string
  patterns, child load queries, and `take` / `skip` (`take size.Value`).
  Optional arguments compare with `IS NOT DISTINCT FROM`. Bound queries work
  with `All`, `One`, `Count`, `Exists`, `Explain`, `Tx.all` and bulk writes.
- Explicit errors: reading `.Value` while the query is built, ordering by an
  argument, arithmetic on an argument in `take` / `skip`, negative counts, and
  selecting an argument.
- Not yet: prepared projections (`select`), which are still built per call.

**Schema check:**

- `repo.CheckSchema()` reads `information_schema.columns` and returns every
  `SchemaIssue`: missing tables and columns (join tables included), column
  types the dialect says cannot hold the field (`ISqlDialect.ColumnTypes`),
  non-optional fields over nullable columns, and NOT NULL columns without a
  default that the model does not map. It creates and migrates nothing; keep
  your migration tool.

**Test sandbox:**

- `repo.Sandbox(fun repo -> task { ... })` gives the body a repository on one
  connection inside one transaction, always rolled back. Transactions inside it
  become savepoints. Each operation runs in its own savepoint, so a database
  error does not end the sandbox. Operations run one at a time. Concurrent
  sandboxes are isolated by ordinary transaction isolation.
- Inside a sandbox, isolation levels and snapshot reads see the sandbox's
  transaction (serialisation retries cannot be tested there). A repo call made
  from inside a `Tx.run` step waits for the step and fails after 30 s with an
  explanation; use the step's context.

**F# Interactive:**

- Queries display the SQL they would run, with parameter values and one command
  per relationship load, or why they cannot be translated
  (`StructuredFormatDisplay`; no setup call). The SQL comes from the most
  recently created repository that maps the query's type, and nothing is
  executed.
- `Plasm.Interactive.wait` awaits a repository call so the result is displayed.
- A query or model that uses a type from an earlier submission than the other
  (the type was redefined) fails with `TranslationError.RedefinedType`, which
  says which to re-evaluate; the query's display says the same.
- `repo.Playground()` is a repository whose changes last across submissions on
  one connection and transaction and are never committed. `Reset()` rolls them
  back and keeps it open; `Close()` (or `Dispose`) ends it, after which using it
  fails with an explanation. It behaves like a sandbox otherwise.
- `repo.Scaffold table` returns F# source for a record and schema generated
  from the catalogue: dialect column types become F# types, nullable columns
  options, the primary key `key` or `identityKey`; columns with no F# type are
  left out with a comment, and defaults are pointed out. Relationships are not
  inferred.

**Reading one row:** `repo.One` keeps the query's own ordering and `take`,
fetches at most two rows to detect a second match, returns
`DbError.MoreThanOneRow` when there is one, and reuses its plan for the same
query value.

**Transactions:**

- `transaction { }` with `Tx.insert` / `update` / `delete` / `all` / `run` /
  `fail` / `traverse`, isolation levels, and an opt-in `RetryPolicy`.
- `Tx.insert` / `update` / `delete` check their changeset's action when the
  step is built, as `repo.Insert` / `Update` / `Delete` do.
- A `Tx.run` step gets `TxContext.CreateCommand()` (enlisted in the
  transaction) and the cancellation token. The connection and transaction are
  not exposed, so a step cannot commit or roll back.

**Failures:**

- `DbError` and `TranslationError` cases are qualified (`DbError.Database`), so
  domain unions keep their own case names.
- Repository calls return expected failures as values: a repository used after
  its sandbox ended or its playground was closed returns
  `DbFailureKind.RepositoryClosed`; a written row that cannot be read back
  returns `WriteError.Materialization`; `repo.One` over several rows returns
  `DbError.MoreThanOneRow`. Exceptions remain for programming errors found
  before any database work, and for cancellation.
- An operation the provider cannot perform is always
  `TranslationError.UnsupportedByProvider`, naming every missing capability:
  inside `WriteError.Translation`, `DbError.Translation` or
  `StepFailure.Failed (DbError.Translation ...)`.
- Every public union is qualified (`ChangeAction.Insert`, `SortDirection`,
  `FieldChange`, `AssocKind`, `LoadStrategy`, `StepFailure`, `WriteError`,
  `DbError`, `TranslationError`).
- Transaction failures are typed: `StepFailure.InvalidChanges (entity,
  errors)`, `ConcurrencyConflict`, `Failed of DbError`, and `Rejected of
  Rejection`, whose reason is read back with `rejection.As<'R>()`.

**Providers:**

- Database-specific SQL comes from `ISqlDialect` (`NullSafeComparison`,
  `FunctionName`, quoting, parameters) or is guarded by a capability
  (`ArrayParameters`, `Returning`, `Upserts`, `RowLocking`, `PerParentLimit`,
  `SnapshotReads`). A missing capability is refused before execution, naming
  every capability the write lacks.

**Execution:**

- `Repo.All` / `One` / `Preload` / `Explain` / `Insert` / `Update` / `Delete`
  / `Transact`
- Diagnostics callback (SQL, duration, rows; parameter values only when
  enabled)
- Cancellation through to the server
- Classified failures (`DbFailureKind`)

## Measured performance

Full method and limitations are in `docs/benchmarks.md`. The setup was an Apple
M4 Max, PostgreSQL 18.4 in a local container, 10,000 orders × 10 lines × 10
payments, EF Core 11 (rc.3 provider) with `AsNoTracking`.

- **Command count is constant in the number of parents.** A page with
  Lines→Product and filtered Payments takes 4 commands at 20, 100 and 1,000
  parents.
- **No cartesian product.** One order with 10 lines and 10 payments transfers
  21 rows; EF's single query transfers 100.
- **Larger graphs.** At 1,000 parents Plasm is 2.1× faster than EF split query
  and 4.6× faster than EF single query, and allocates 52–67% less. Each
  distinct Product is fetched and materialised once and shared.
- **Small graphs.** Built and planned per call, a one-order query is slower
  than EF and allocates more: about 80 µs and 63 KB to build the
  quotation-based query value, plus about 60 µs and 38 KB to plan it. Prepared
  with `Query.prepare`, the same query is faster than both EF modes. It
  allocates less than EF single query, but more than EF split query:

  | One order with lines and payments, per call | Mean | Allocated |
  |---|---:|---:|
  | Plasm, query rebuilt per call | 474 µs | 207 KB |
  | Plasm, prepared | 392 µs | 108 KB |
  | EF Core single query | 415 µs | 128 KB |
  | EF Core split query | 453 µs | 91 KB |

  These are BenchmarkDotNet means from one run on the development machine; the
  rebuilt case measured 435 µs in an earlier run.

## Gaps and limitations

**Not built, and with no API.** Absent features are not partly supported:

- Joins: right and full outer joins, joins on non-equality conditions, joins
  to subqueries, and `preload` combined with joins.
- `distinct`, subqueries in `select`, unions, CTEs, and window functions other
  than the internal per-parent limit (use `Raw.fragment` or `repo.Sql`).
- Has-through relationships, many-to-many join tables with extra columns
  (model those as records with two belongsTo), composite keys, embedded or
  owned types, JSON columns.
- Writing belongs-to or has-one relationships through the parent; nested
  inserts below `hasMany` children have not been exercised.
- Bulk writes over joins (`UPDATE ... FROM`), `RETURNING` from bulk updates,
  streaming (`IAsyncEnumerable`) results.
- Prepared projections.
- Migrations and DDL (deliberately: `repo.CheckSchema` verifies the result of
  your migration tool instead). Tests create their schema with SQL.
- Providers other than PostgreSQL.

**Known limitations:**

- Plasm depends on FSharp.Core 8.0 or later (the packages declare
  `FSharp.Core >= 8.0.403`), so it loads into hosts with an older FSharp.Core
  than the SDK's, such as .NET Interactive notebook kernels. The koans
  notebook is run in a real kernel (`dotnet-repl`) by the koans checks.

- `DateTime` fields mapped to `timestamptz` must be UTC; Npgsql enforces this.
- The PostgreSQL error-detail text is redacted by Npgsql unless
  `Include Error Detail` is set. Plasm reports SQLSTATE, constraint name and
  message.
- With `<WarnOn>1182</WarnOn>` (unused variables) enabled, F# reports the row
  variable of `for o in ... do` as unused, because its only uses are inside
  quotations. This is a compiler-analysis limitation; the warning is off by
  default.
- A child query given to `preload` for a required `belongsTo` must not filter
  the target away. If it does, materialisation fails with an explicit error;
  no value is invented.
- `Changeset.edit` cannot record a change that equals the original. As in
  Ecto, such a change is dropped.
- Validations run when they are applied, against the values at that point.
- A `join` cannot be the last clause of a `plasm { }` expression; follow it with
  `where`, `orderBy` or `select` (an F# rule for join-like operations, FS3098).
- A key type mismatch in `join` is reported as FS0041 ("No overloads match for
  method 'Join'") because `join` is overloaded; the message names both key types.
- An unmapped constraint violation becomes `WriteError.Failed` with
  `DbFailureKind.UniqueViolation` / `ForeignKeyViolation` / `CheckViolation`,
  never a field error.
- `Tx.run` steps can perform arbitrary effects. Only database work through the
  given context is rolled back.
- A single-row write without children or links runs without a transaction, so
  if its returned row cannot be read back (`WriteError.Materialization`) the
  row stays written.
- Displays of query values use the most recently created live repository that
  maps the query's type. Displaying never fills a plan cache, but the text
  depends on which repositories exist.
- The schema check and scaffolding read `information_schema` with
  `current_schema()`, and many-to-many link writes use `unnest`; a second
  provider would need these behind the provider too.
- A transaction has no typed error channel (`Tx<'T, 'E>` would hit F#'s value
  restriction on every transaction value); `Tx.fail` reasons are read back by
  type with `Rejection.As<'R>()`.
- `Tx.run` commands refuse statements that start with COMMIT, ROLLBACK, END,
  BEGIN, START, ABORT, SAVEPOINT or RELEASE (after comments are removed). A
  semicolon inside a string literal can make the check refuse a harmless
  command; it never lets a control statement through.
- Query displays show parameter values only when the describing repository
  includes them in diagnostics (`IncludeParameterValues`) or the query's types
  were defined in F# Interactive. In F# Interactive with a compiled model
  (`#r` of your assembly), set `IncludeParameterValues` to see values.
- Rows from a multi-row `INSERT ... RETURNING` are returned in insertion order.
  PostgreSQL does this in practice, but does not formally guarantee it.
- `take` and `skip` quote their argument, so a count that is not a literal is
  evaluated with the F# quotation evaluator (a few microseconds).
- Grouping and fragment errors are found when the query is planned, before any
  command is sent, rather than at compile time.

## Decision record

| Date | Decision | Where |
|---|---|---|
| 2026-11-12 | Adopt Ecto's responsibilities; express queries as quotations in a computation expression; ADO.NET with Npgsql directly; EF Core only as a benchmark baseline | ADR 0001 |
| 2026-11-12 | `Assoc<'T> = NotLoaded \| Loaded of 'T`, with the payload encoding cardinality | ADR 0001 |
| 2026-11-12 | `take` in a load query is rejected; `limitPerParent` uses a window function; unsupported providers reject it | ADR 0001 |
| 2026-11-12 | Load conflicts are reported at plan time (total combination); `replacePreload` resolves them | ADR 0001; `@changed` scenario |
| 2026-11-12 | Child replacement reads the database's children in scope, `FOR UPDATE`; the default `OnReplace.Reject` | ADR 0001 |
| 2026-11-12 | BDD with Reqnroll: a C# host generates the tests from `features/*.feature`, and every step is in F# | `tests/Plasm.Specs*` |
| 2026-11-12 | Union cases with common names are qualified (`WriteError`, `DbFailureKind`, `FieldErrorKind`, `ReadConsistency`) after domain unions shadowed them in tests | API review |
| 2026-11-12 | No overloads on `Changeset` statics (`validateOptionalLength` is separate), so lambda record types infer from the piped changeset | API review |
| 2026-11-12 | Joins: `join` / `leftJoin` custom operations with quotation keys; left-joined sources are `'T option` with option semantics in SQL; joined queries run as one command and do not preload | `features/joins.feature`, ADR 0001 |
| 2026-11-12 | Plans cached per repo per query value unless translation evaluated a volatile expression; compiled delegates for record construction | `docs/benchmarks.md` |
| 2026-11-12 | Aggregates are typed functions (`Agg.*`) inside the query; sum of nothing is 0, min/max/avg are options; grouping mistakes are planning errors | `features/aggregates.feature`, ADR 0001 |
| 2026-11-12 | Bulk writes are `BulkWrite<'R>` values; updates are copy-and-update lambdas, bypass changesets, and increment versions | `features/bulk-writes.feature`, ADR 0001 |
| 2026-11-12 | Hand-written SQL takes `FormattableString` (holes are parameters); fragment SQL is a format string (literal only) | `features/raw-sql.feature`, ADR 0001 |
| 2026-11-12 | Many-to-many: declared with its join table; links written only by putLinks/addLinks/removeLinks, which never write targets | `features/many-to-many.feature`, ADR 0001 |
| 2026-11-12 | No migrations: `repo.CheckSchema` reports drift against the live catalogue | `features/schema-check.feature`, ADR 0001 |
| 2026-11-12 | Test sandbox: one rolled-back transaction, a savepoint per operation and per inner transaction | `features/sandbox.feature`, ADR 0001 |
| 2026-11-12 | Prepared queries: probe `Arg<'T>` values, built once, parameters bound per call; take/skip quote their count instead of overloading (which broke inference) | `features/prepared-queries.feature`, ADR 0001 |
| 2026-11-13 | Architecture audit follow-up: extended prepared queries plan their own description; `not` is two-valued (`IS NOT TRUE`); only immutable captured values allow plan caching; sandboxes nest; qualified error cases and closed/unreadable/unsupported failures as values; `TxContext` without the transaction; complete pipeline module; `hasOne` tested with `Rel.exists`; dialect hooks and capability checks | the six audit features, ADR 0001 |
| 2026-11-13 | Plasm built against FSharp.Core 8.0 (not the SDK's 11.0): a notebook kernel with FSharp.Core 10 could not load it; the koans notebook now runs in a real kernel in CI | `koans/features/notebook.feature` |
| 2026-11-13 | Second audit follow-up: only immutable module values and captures allow plan reuse; `not` is two-valued only over nullable operands; pipeline joins nest and translate; `repo.One` keeps `take` and reports `MoreThanOneRow`; displays hide values unless enabled; prepared load queries bind their own arguments; all public unions qualified; typed `StepFailure`/`Rejection`; one shape for unsupported operations; `Tx.run` commands refuse transaction control | `reading.feature` and new rules in six features, ADR 0001 |

## Suggested next steps


1. Choose a license, add the repository URL to the package metadata, and
   publish pre-release packages (the CI already builds them).
2. Prepared projections; `limitPerParent` from a prepared argument.
3. Belongs-to and has-one writes; deeper nested child writes.
4. A second provider, to prove the `IProvider` / `ISqlDialect` boundary
   (SQLite or SQL Server); move the catalogue queries and the link SQL behind
   it.
5. Diagnostics as OpenTelemetry activities.
6. Compare Plasm with [SqlHydra](https://github.com/JordanMarr/SqlHydra),
   especially enums: how each maps .NET enums and discriminated unions to
   PostgreSQL enum types, and what the recent Npgsql enum mapping changes
   (data-source-level `MapEnum`) mean for Plasm's providers.
