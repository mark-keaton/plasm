# ADR 0001: Ecto's architecture, expressed in F#

- Status: accepted (first delivery)
- Date: 2026-11-12

## Context

We want an F# persistence library that can eventually replace EF Core in F#
applications. EF Core's model is change tracking over mutable entity graphs,
which works against immutable records, `option`, single-case unions and explicit
effects. Ecto (Elixir) has already worked out a functional design that keeps
the separation that matters:

- schemas
- query values
- changesets
- explicit preloads
- a repository effect boundary
- composable transactions (`Ecto.Multi`)

We adopt Ecto's responsibilities and semantics. We do not adopt its macro
syntax.

## What we investigated, and what we reuse

| Candidate | What it is (Nov 2026) | Licence | Decision |
|---|---|---|---|
| **Ecto** | Elixir data mapping and query library; behavioural reference | Apache-2.0 | **Reuse the design and test cases.** About 40 behavioural cases are adapted in `tests/Plasm.Tests/EctoCompat.fs`, with attribution in `NOTICE`. No Ecto code is copied. |
| **Npgsql** | PostgreSQL ADO.NET driver | PostgreSQL | **Reuse.** Connections, pooling (`NpgsqlDataSource`), typed reads, array parameters, cancellation and SQLSTATE classification. |
| **FSharp.Core** quotations and reflection | Compiler-checked expression trees; `PreComputeRecordConstructorInfo`, `LeafExpressionConverter` | MIT | **Reuse.** The whole query surface is quotations. Closed subexpressions are evaluated with the FSharp.Core evaluator. |
| **EF Core** as an execution backend | Mature ORM with LINQ translation and split queries | MIT | **Rejected as a backend; kept as a benchmark baseline.** See below. |
| **SqlHydra** | Generates types from a database schema, plus a LINQ-`Expression`-based query CE | MIT | **Not reused.** Its CE showed that custom operations can take typed predicates, which helped this design. But it is schema-first, has no relationship loading, and uses LINQ expression trees, which handle F# idioms poorly. |
| **Dapper.FSharp** | Lightweight CRUD CE over Dapper, with LINQ-expression predicates | MIT | **Not reused.** No graph loading or changesets, and Dapper's mapper needs settable members or matching constructors with its own conventions. |
| **SQLProvider** | Erased type provider over a live schema | Apache-2.0 | **Not reused.** Needs a database at compile time, uses erased types, and navigates relationships lazily, which is the hidden database access we explicitly avoid. |
| **Facil** | Generates F# from SQL scripts and stored procedures (SQL Server) | MIT | **Not reused.** SQL-first and SQL Server only. A good complement for hand-written SQL, not a fit for composable query values. |
| **Rezoom.SQL** | Type provider over its own SQL dialect, with migrations | MIT | **Not reused.** SQL-dialect-first, and its composition model is different. |
| **EFCore.FSharp** | Design-time and value-converter support for EF Core in F# | MIT | **Not reused.** It makes EF more usable from F# but keeps EF's tracked, mutable model. |

**Why not EF Core underneath?** EF Core can bind records through their
constructors. But the library would then inherit three things it is meant to
remove:

- Navigations must be settable collections or references. They cannot be an
  explicit unloaded/loaded type.
- Translation goes through LINQ trees converted from F# quotations, so F#
  idioms (`option`, pattern matching, unions) translate poorly or fall back to
  runtime errors.
- Split queries restrict children by re-running the parent query (with its
  ordering and `LIMIT`) as a join in every child query.

`docs/benchmarks.md` shows the generated SQL. Going around EF's tracking, model
building and query pipeline would leave little of EF in use. The PostgreSQL
provider instead uses ADO.NET directly, and EF Core stays as the benchmark
baseline.

## Decisions

### Queries: immutable values written with quotations

- `Query<'T>` is an immutable value: filters, orderings, limit/offset and
  relationship loads, each kept as an F# quotation. `Projection<'R>` is a query
  ending in `select`.
- The `plasm { for o in Db.orders do ... }` computation expression gives every
  custom operation its argument as a **quotation**, via
  `[<ProjectionParameter; ReflectedDefinition>]`. The compiler type-checks it
  against the record: `o.Total > "x"`, `preload o.Total` and
  `preload o.Product` are compile errors.
- Composition is ordinary F#: `let forCustomer c q = plasm { for o in q do where (o.CustomerId = Some c) }`.
  Pipeline helpers (`Query.where`, ...) exist too.
- Translation to a small SQL AST (`Sql.fs`) happens only when a query is
  planned (`repo.Explain`) or executed. The rules:
  - A subexpression that does not mention a row variable is *closed*. It is
    evaluated once and sent as a parameter; captured values are always data.
  - A row-dependent subexpression must translate to SQL. Otherwise translation
    fails with `UnsupportedExpression`, naming the expression, and nothing runs
    client-side.
  - `x = None` means `IS NULL`. Option-to-option equality is
    `IS NOT DISTINCT FROM` (F# structural equality).
  - Ordering comparisons on options are rejected, because F#'s `None < Some`
    differs from SQL's NULL.
  - An empty `List.contains` gives `FALSE`.
  - `Rel.any` and `Rel.all` give correlated `EXISTS`, so a parent never
    duplicates.
- `take` or `skip` without an ordering is rejected. With an ordering, the
  primary key is appended as a documented tiebreaker.
- Plans are cached per repo, keyed by the query value (by reference). A plan
  is reused only when every closed expression it evaluated is stable: a
  literal, an immutable captured value, or an F# module value whose current
  value is immutable. `DateTime.UtcNow`, captured or module-level arrays,
  collections and reference cells, and any other computed property make the
  plan volatile, so it is translated again on each run.

### Joins

- `join c in Db.customers on (o.CustomerId = Some c.Id)` and `leftJoin` are
  custom operations whose keys arrive as quotations. After a join the variable
  space is a flat tuple of sources; later operations bind each element to its
  source, and `Some x` in a key or comparison translates to `x`.
- A left-joined source is typed `'T option`, so F# code must handle absence.
  The translator gives that F# meaning in SQL: `c.IsNone` / `c = None` test the
  source's key for NULL, `Option.exists` / `Option.forall` become
  `key IS NOT NULL AND …` / `key IS NULL OR …`, and `Option.map` / `Option.bind`
  to a field read a nullable column.
- Keys compare with SQL `=`: NULL keys never match (relational, not F#
  structural, equality); this is documented.
- A joined query is read as a projection: one command, rows are tuples of whole
  records (relationships `NotLoaded`) or the projected values. Pagination
  appends every source's key. Preloading through a join is not offered:
  combining row multiplication with graph assembly would blur two models, so
  `preload` on a joined query is a planning error that points to
  `repo.Preload`.
- Joins return one row per matching pair. Relationship predicates (`Rel.any`)
  remain the way to filter parents without duplication.

### Materialisation of immutable records

- Records are built through their compiler-generated constructor (a compiled
  expression tree). No setters, parameterless constructors or base classes are
  needed.
- Values are read with typed `GetFieldValue<T>`, using the provider type of the
  field's mapping, and then converted:
  - `option` fields map to nullable columns. NULL in a non-option field is a
    materialisation error.
  - Wrapper types need an explicit `Scalar.wrap` or `Scalar.tryWrap`.
  - Fieldless unions need an explicit `Scalar.enum` table.
- `Scalar.tryWrap` runs the domain's validating constructor. A stored value it
  rejects produces a structured `MaterializationError` (model, property, value,
  message). The constructor is never bypassed.
- `Model.build` validates everything up front. Unmapped types, undeclared
  relationship fields, foreign-key type mismatches and nested options are
  reported together, with model and property. No implicit or lossy conversion
  is chosen (for example, `uint64` is rejected).

### Relationships and the unloaded state

- `Assoc<'T> = NotLoaded | Loaded of 'T`. The payload encodes cardinality:
  `Assoc<X list>` (has-many), `Assoc<X option>` (optional belongs-to/has-one)
  and `Assoc<X>` (required belongs-to).
- Reading an `Assoc` never touches the database. `Assoc.get` on `NotLoaded`
  throws instead of loading.

### Planning, batching and assembling nested loads

- Each `preload` names a relationship by a typed path, and may give a child
  `Query` with filters, ordering, `limitPerParent` and nested preloads.
- At plan time, loads are grouped per relationship:
  - Identical requests merge: their canonical SQL and parameters are equal.
  - Nested loads are united.
  - Different customisations are a `LoadConflict`, reported before any command
    runs. `replacePreload` resolves it explicitly.
- Each load is one command per batch of distinct parent keys:
  `key = ANY(@keys)`, with one array parameter. The batch size is a provider
  capability (`MaxKeysPerCommand`), so the command count depends on keys
  divided by the batch size, never on the number of parents. Sibling
  collections are separate commands, so there is no cartesian product.
- `take` inside a load query is ambiguous and rejected. `limitPerParent n`
  means per parent and is implemented with
  `ROW_NUMBER() OVER (PARTITION BY fk ORDER BY ...)`. A provider without that
  capability rejects it instead of substituting a global limit.
- Children are ordered by the load query's ordering, then by primary key.
- Assembly is bottom-up. Rows are read into field arrays, nested loads fill the
  children's arrays, and each record is constructed exactly once. A shared
  belongs-to target (the same Product) is one shared immutable value.
  `repo.Preload(spec, values)` decomposes existing records and returns new ones;
  the originals are untouched.
- `ReadConsistency.Snapshot` runs all of a graph's commands in one
  `REPEATABLE READ READ ONLY` transaction. A provider that cannot do this
  rejects the request.

### Changesets: unchanged, assigned and cleared

- `Changeset<'T>` is immutable and holds:
  - the original value (or only a key)
  - a map of changes by field
  - structured `FieldError`s
  - declared constraints
  - child writes
- A change equal to the original value is not a change (Ecto `put_change`
  semantics).
- `FieldChange` is `Assigned value | Cleared`. A field that is not mentioned is
  absent from the map; that is the third state.
- `Changeset.edit (fun o -> { o with ... })` records exactly the fields the
  function alters.
- `Changeset.forKey` builds an update from partial data (a projection): only
  the fields set are written, never defaults.
- `Changeset.cast` accepts external input field by field, so identifiers and
  versions cannot be changed unless they are cast explicitly.
- Validations are pure functions. As in Ecto, they check changed values on
  update and proposed values on insert. `validateRequired` checks the resulting
  value.
- `Changeset.check model` adds model-aware checks without database access:
  primary-key or version changes, and duplicate child identities.
- Declared unique, foreign-key and check constraints turn matching violations
  into field errors. Undeclared violations stay `WriteError.Failed` with a
  classified `DbFailureKind`.

### Association writes

- `putChildren` inserts new children and updates existing ones by identity.
  Every update and delete is verified to belong to the parent (`NotOwned`
  otherwise).
- `replaceChildren` requires a `ReplaceScope`: `allChildren`, or `matching q`
  for filtered collections. Children in scope that are omitted are handled by
  the relationship's `OnReplace` policy. The default `Reject` rejects the write;
  `Delete` and `Nilify` must be declared in the schema.
- Children not in scope, and relationships not mentioned (including
  `NotLoaded` ones), are never touched.
- A parent write with children runs in one transaction. Any child failure,
  whether a validation error or a database error, rolls back everything.

### Transactions and concurrency

- `Tx<'T>` is a description. Writing it with `transaction { let! x = Tx.insert "name" cs ... }`
  threads typed results between named steps; Ecto's untyped `changes` map is
  replaced by `let!`.
- `repo.Transact` interprets it in one database transaction.
  - A failure reports the failing step, the steps that were rolled back, and
    the attempt count.
  - An empty description succeeds without opening a connection.
  - Duplicate step names are rejected.
- Only database work can be described. `Tx.run` exposes the transaction's
  connection as an explicit escape hatch. External effects use an outbox
  (an insert step) or run after `Transact` returns `Ok`.
- Retries are opt-in (`RetryPolicy.OnSerializationFailure n`). A retry
  re-interprets the whole description in a fresh transaction, so reads are
  repeated. By default nothing is re-run.
- Optimistic concurrency: a schema's `version` field is required to match on
  update and delete, and is incremented. A mismatch is
  `WriteError.Stale` / `StepFailure.ConcurrencyConflict`.
- Cancellation tokens reach Npgsql, which cancels the statement on the server.
- Failures are classified (`DbFailureKind`), so callers never parse messages.

### Aggregates and grouping

Aggregates are functions in the query (`Agg.count ()`, `Agg.sum o.Total`), not
separate repository calls, so they combine with filters, joins, `groupBy`,
`having` and ordering. Their types carry the F# meaning: the sum of no rows is
zero (as `List.sum []`, via `COALESCE`), and `min`, `max` and `avg` return
options. `sum` and `avg` use the same statically resolved constraints as
`List.sum` and `List.average`, so summing text or averaging a key does not
compile. Whether a selected column is grouped cannot be expressed in the F#
types without a much heavier API, so it is checked when the query is planned,
before any command is sent. `repo.Count` and `repo.Exists` are shortcuts that
transfer at most one row.

### Bulk writes

Set-based writes are `BulkWrite<'R>` values, like queries. They are inspected
with `repo.Explain` and run by `repo.Execute` or as a transaction step
(`Tx.execute`). An update is a copy-and-update lambda
(`fun o -> { o with Status = Cancelled }`). Fields read unchanged from the row
are not written, and assigned values may read the row (`o.Total + 10m`).
Upserts use the same form over the stored and incoming rows. Following Ecto's
`update_all` and `insert_all`, bulk writes bypass changesets; database
constraints still apply. Unlike Ecto, a bulk update increments the version
column, so changesets prepared before it are detected as stale. Inserts above
the parameter limit are split into several commands inside one transaction.

### Hand-written SQL

The escape hatches keep the guarantee that values never become SQL text.
`repo.Sql<'R>` takes a `FormattableString`, so every interpolated value is a
command parameter and a plain string does not type-check. Results go through
the same model as queries: mapped records by column name, other records by
field name, tuples by position. `Raw.fragment` puts literal SQL inside a typed
query. A quotation cannot tell a literal string from a captured one, so the
fragment's SQL is typed as an F# format string, which the compiler only
accepts as a literal.

### Prepared queries

`Query.prepare (fun (c: Arg<CustomerId>) -> plasm { ... })` calls the builder
once with placeholder `Arg` values. Inside quotations `c.Value` is a captured
`Arg`, which the translator turns into a parameter bound from each call's
arguments, so the query is built and planned once and every call sends the
same SQL text. Reading `c.Value` outside a quotation throws, so an argument
cannot silently influence the query's shape; ordering by an argument is
rejected for the same reason. `take` and `skip` quote their count, so that
`take size.Value` is recognised without overloading the operation. An
overload broke type inference for ordinary computed counts.

### Many-to-many relationships

`manyToMany` names the join table and its two key columns. Loading goes through
the join table with one command per batch of owner keys, and the owner key
comes back as an extra column. A target linked to several owners is
materialised once, as Products are under order lines. Changing links is
separate from changing records. `Changeset.putLinks` states the complete set;
`addLinks` and `removeLinks` change only what they name. All of them write
join-table rows only, so a link can never create or modify the target, unlike
Ecto's `put_assoc`, which may insert new targets. After `addLinks` or
`removeLinks` the relationship is `NotLoaded`, because the full set was not
read. A join table with its own data is a record with two `belongsTo`
relationships, not a many-to-many.

### Schema check instead of migrations

Plasm does not generate or run migrations. F# teams already use migration tools
(DbUp, Flyway, Grate, EF migrations, hand-written SQL), and a second schema
source would compete with them. What a mapping layer can add is a check that
the model and the database agree. `repo.CheckSchema` reads the catalogue and
reports only what would make a mapped read or write fail: missing tables and
columns, incompatible types (as judged by the dialect), non-optional fields
over nullable columns, and required columns the model cannot write. Extra
nullable or defaulted columns are not reported.

### Test sandbox

`repo.Sandbox` follows Ecto's SQL sandbox. The body gets a repository on one
connection, inside one transaction that is always rolled back. Transactions
inside it become savepoints, so code under test keeps its transactional
meaning. Each operation also runs in a savepoint, because PostgreSQL aborts a
transaction after any error, and without one a test that expects a constraint
violation could not continue. Operations share one connection, so they run one
at a time.

### F# Interactive

Plasm values display themselves through `StructuredFormatDisplay`, which F#
Interactive honours without any printer registration, so referencing the
package is the only setup. A query's display needs a model and a dialect, which
a query value does not carry; each `Repo` therefore registers itself, weakly,
and a query is described by the most recent live repository that maps its type.
Describing plans and renders SQL but executes nothing.

F# Interactive compiles each submission into its own `FSI_nnnn` module, so
re-evaluating a record creates an unrelated type with the same name. A query
and a model built on different submissions' types are recognised by that
module name and fail with `RedefinedType`, saying which side to re-evaluate.
Display prefers a repository that knows a redefinition of the type, so a stale
query explains itself instead of showing SQL from a shadowed repository.

`repo.Playground()` reuses the sandbox provider, but opens its connection and
transaction on first use and keeps them until `Reset` or `Close`, because a
REPL session has no single function body to wrap. `repo.Scaffold` reads only
`information_schema` and maps column types back through
`ISqlDialect.ColumnTypes`, so it agrees with the schema check by construction.

### After the architecture audit

An independent audit found two silent wrong-result bugs and several soft
spots; each fix began as a failing scenario.

- **Derived queries are planned from themselves.** A prepared query carries its
  template so each call reuses one plan. Any operation that derives a new query
  now drops the template, so an extension is never ignored.
- **`not` is two-valued.** SQL's `NOT` keeps an unknown (NULL) comparison
  unknown, which drops the row; F# treats the comparison as false. `not p`
  renders as `(p) IS NOT TRUE`, matching F# and `<>`; `NOT EXISTS` is kept.
- **Plan caching needs immutable captures.** A captured array, collection or
  reference cell can change after the query is built, so it marks the plan
  volatile. Primitives, strings and immutable F# data still allow caching.
- **Failures are values with qualified names.** `DbError` and
  `TranslationError` require qualification. A closed sandbox or playground,
  an unreadable written row and an unsupported write return values; only
  programming errors found before database work, and cancellation, throw.
- **Steps cannot end their transaction.** `TxContext` gives commands enlisted
  in the transaction instead of the transaction itself; write steps check the
  changeset's action when built.
- **Second audit.** `not` is two-valued only where an operand can be NULL
  (an option field, a left-joined source, an aggregate or a fragment), so
  ordinary negations keep their indexes. Pipeline joins nest their rows
  (`((o, l), p)`) and the translator follows the lambda's tuple shape.
  `repo.One` keeps the query's own limit. A bound prepared query used as a
  load query binds its own arguments. Every public union is qualified.
  Transaction failures are typed (`StepFailure.Failed of DbError`,
  `Rejected of Rejection`), and a `Tx.run` command cannot control the
  transaction. A query's display hides parameter values unless the repository
  includes them or the types come from F# Interactive.
- **The provider boundary is explicit.** The dialect renders null-safe
  comparison and function names; RETURNING, upserts, row locking and array
  parameters are capabilities checked before execution. The catalogue queries
  and link SQL are still PostgreSQL-shaped (see the status document).

## Following Ecto, and differing from it on purpose

**Same as Ecto:**

- Schemas are separate from queries, changesets and the repository.
- Preloads are explicit, use separate queries and nest; preloads into existing
  values are supported.
- Changes equal to the data are dropped.
- Validations apply to changes.
- Named constraints map to field errors; undeclared constraints are not
  changeset errors.
- `on_replace` defaults to raising (here: rejecting).
- Optimistic locking.
- Transaction failures identify the failed operation.
- An empty Multi succeeds.

**Deliberately different:**

| Topic | Ecto | Plasm | Why |
|---|---|---|---|
| Query syntax | Macros | Quotations in a CE | F# has no macros; quotations give compiler checking |
| `limit` in a preload query | Applies globally across all parents (documented pitfall) | Rejected; `limitPerParent` | Never substitute one meaning for another |
| Comparing with nil | Raises; use `is_nil` | `= None` means `IS NULL` | Matches F# semantics |
| Multi results | Untyped map of names to values | Typed `let!` | Static types |
| `Repo.preload` on loaded data | Skips already-loaded relationships unless `force: true` | Always reloads what was asked for | An explicit request means an explicit load; no stale data |
| Replacing children | Compared against the preloaded data on the struct | The database's current children, read `FOR UPDATE` in the write's transaction, within an explicit `ReplaceScope` | Filtered or stale loaded data cannot cause deletions |
| Conflicting preload customisations | Raise at query build | Total combination; `LoadConflict` at plan time, before any command | Combining immutable values is total and pure |
| Duplicate child identities in `cast_assoc` | Last one wins | Rejected (`DuplicateIdentity`) | No choice based on incidental ordering |

## Consequences

- PostgreSQL is the only provider. Everything provider-specific sits behind
  `IProvider` / `ISqlDialect` (rendering hooks, capabilities, error
  classification, snapshot support).
- Things that are not built have no API: right/full joins, aggregates and
  group-by, many-to-many, has-through, embedded types, writing
  belongs-to or has-one through a parent, bulk `update_all`/`insert_all`,
  upserts, migrations, and unions with fields. See `docs/status.md`.
