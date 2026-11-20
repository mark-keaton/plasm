# SqlHydra compared with Plasm

*Researched 2026-11-13. SqlHydra state: **v5.1.1** (SqlHydra.Query and SqlHydra.Cli, published to NuGet 2026-10-19), repo HEAD `551e1b5` (2026-10-19).
Sources: GitHub repo https://github.com/JordanMarr/SqlHydra (README, `src/`, `docs/writing-extensions.md`, git history, issues via the GitHub REST API), NuGet https://www.nuget.org/packages/SqlHydra.Query and https://www.nuget.org/packages/SqlHydra.Cli.
Plasm state: `README.md` and `docs/status.md` as of 2026-11-12 (0.1.0-alpha, unpublished); Plasm-side statements corrected 2026-11-14 for the one style (relationships, preloads, public changesets, validation and repository writes were removed). The findings about SqlHydra are unchanged. Corrected again 2026-11-20 against `features/`: PostgreSQL enum types, `selectDistinct`, `exists`, the start-up schema check and the licence.
"Not verified" means I read the code or docs but did not run it.*

## What SqlHydra is

Two packages:

- **SqlHydra.Cli** (a dotnet tool). It reads the database catalog and generates an F# file. The file holds one record per table or view (nullable columns become `option`), table declarations, a `HydraReader`, and a database-specific `QueryContextFactory`.
- **SqlHydra.Query**. It provides typed computation expressions (`selectTask`, `insertTask`, `updateTask`, `deleteTask`, plus `Async` twins) that compile to SQL.

The author calls it "a strongly typed query library rather than an ORM" and deliberately has no ORM features ([#80](https://github.com/JordanMarr/SqlHydra/issues/80), JordanMarr, 2024-01-10). That framing sets up most of the comparison:

- **SqlHydra:** database-first, generated types, SQL-shaped queries.
- **Plasm:** model-first, the application's own domain records, aggregates that own and link records and are loaded and written whole, typed transactions and Result-based errors.

In v4.0 (April 2026) SqlHydra replaced its SqlKata dependency with its own IR and SQL emitters (commits `c890a56`/`c43e199`, 2026-04-04). Its only runtime dependency is now FastExpressionCompiler (`src/SqlHydra.Query/SqlHydra.Query.fsproj`). It targets netstandard2.0 and net8/9/10.

---

## 1. Enums and discriminated unions

**PostgreSQL enum types → CLR enums (generated).**

- The CLI reads `pg_enum` joined to `pg_type` (`src/SqlHydra.Cli/Npgsql/NpgsqlSchemaProvider.fs` ~L40–70).
- It emits a **.NET enum**, not an F# union, with each label's sort order as its value (`mkEnum`, `src/SqlHydra.Cli/SchemaTemplate.fs` L15–29).
- Enum columns are typed with that enum, matched schema-qualified first, then unqualified (`NpgsqlSchemaProvider.fs` L204–234).

```fsharp
// generated
type mood =
    | sad = 0
    | ok = 1
    | happy = 2
```

**Npgsql 7 → 8+ (global mapper deprecated, `NpgsqlDataSourceBuilder.MapEnum`).**

- [#110](https://github.com/JordanMarr/SqlHydra/issues/110), 2025-03: "Npgsql v8.0.0 fails when inserting an enum". The maintainer diagnosed it as the v7 global mapper having moved to the per-data-source builder. The fix at the time was user code: build an `NpgsqlDataSource` with `builder.MapEnum<ext.mood>("ext.mood")`.
- [#140](https://github.com/JordanMarr/SqlHydra/issues/140) (contributor michaelglass, June 2026) added code generation for the registration. It shipped in **v4.1.0-beta.3** (2026-06-12) and v4.1.0 (2026-08-23).

The generated code now contains:

```fsharp
[<RequireQualifiedAccess>]
module Enums =
    let register (builder: Npgsql.NpgsqlDataSourceBuilder) =
        builder.MapEnum<ext.mood>("ext.mood") |> ignore
        builder
```

- `QueryContextFactory.Create(connectionString)` builds its data source through `Enums.register` (`SchemaTemplate.fs` L370–410).
- `QueryContextFactory.Create(dataSource)` takes your own data source; you pipe the builder through `Enums.register` before `Build()` (README "Database-Specific Notes").
- Registration is generated only when `provider_db_type_attributes` is on (commit `9bdf11d`). It covers enums in every schema.
- An integration test inserts, filters and updates by enum (`src/Tests/Npgsql/QueryIntegrationTests.fs` ~L560–596).

**Fieldless F# unions.**

- Not generated, and not supported as column types.
- The one place a nullary DU appears is literal rendering: `formatNumericLiteral` quotes an enum or nullary DU as its name (`src/SqlHydra.Query/LinqExpressionVisitors.fs` L569–590).
- Parameter values go through `boxValueOrOption` (`Core.fs` L227), which unwraps options and `Nullable` but passes anything else straight to ADO.NET. A DU field would reach Npgsql as an unsupported CLR type. *Not verified by running.*
- Single-case wrappers (`OrderId of int`) are likewise not mapped. You map generated rows to domain types yourself, which is the author's stated intent (#80).
- The type-mapping extension point (`IExtendTypeMapping`, `docs/writing-extensions.md`) changes the *generated CLR type name*. It does not add a runtime converter, so it does not help with DUs.

**Other databases.**

- MySQL column enums and Oracle enum domains are not handled; #140 says each would need a different shape.
- SQL Server and SQLite have no native enums.

**Open issues about enums:** none. All 6 open issues are listed in §8; the enum issues #22, #23, #110 and #140 are closed.

**Plasm:**

- `Scalar.enum [ Pending, "pending"; ... ]` maps a **fieldless F# union** to text through an explicit, complete, duplicate-checked table. Unknown stored text is a materialisation error (`src/Plasm/Scalar.fs` L80–110).
- `Scalar.asPgEnum "type"` stores the same union in a PostgreSQL enum *type*: the value is sent as text with a cast to the type and read as `::text`, so there is no `MapEnum` registration, and the start-up check reads the type's labels and compares them with the union's cases, in order (`features/pg-enums.feature`). `Postgres.connect` creates its own `NpgsqlDataSource`; `PostgresProvider(dataSource)` accepts one you built.

| | SqlHydra | Plasm |
|---|---|---|
| F# fieldless union | no | **yes** (`Scalar.enum`, as text or as a PostgreSQL enum type) |
| .NET enum | yes, generated | no built-in mapping |
| PostgreSQL enum type | **yes**, generated enum plus `MapEnum` registration on the data source | **yes**, from an F# union (`Scalar.asPgEnum`), with no `MapEnum` |
| Single-case wrappers | no | yes (`Scalar.wrap`/`tryWrap`) |

## 2. Options and nulls

**Generation.**

- Nullable columns become `'T option`, or `Nullable<'T>` with the `NullablePropertyType` setting (`SchemaTemplate.fs` `columnPropertyType`).
- SQL Server tests run both modes (`src/Tests/SqlServer/QueryNullable*`).

**Queries.**

- `where (x = None)` and `<> None` render as `IS NULL` / `IS NOT NULL`; there are also `isNullValue` / `isNotNullValue` (README "Where operators").
- [PR #158](https://github.com/JordanMarr/SqlHydra/pull/158) (2026-09) fixed `having (maxBy col = None)`, which rendered `= @p0` with a NULL parameter. The null rule had been copied into ten places across three visitors, and one copy missed it.
- Comparing option columns means `Some` lifting: `where (d.OrderQty = Some 2s)`, `Some c.Id |=| subqueryMany ...`.
- Joins on nullable foreign keys use `.Value`: `on (p.ProductCategoryID.Value = c.ProductCategoryID)`.
- Aggregates over NOT NULL columns can still return NULL. You wrap them yourself, `minBy (Some p.ListPrice)`; the README carries a warning about it.
- `ISNULL`/`COALESCE` helpers take `Option<'T>` and return `'T`.

**Left joins.**

- Classic form: the joined record is `'T option`, and the ON clause uses `a.Value.AddressID`. This produced bugs such as [#92](https://github.com/JordanMarr/SqlHydra/issues/92) and [#94](https://github.com/JordanMarr/SqlHydra/issues/94).
- v5 adds **left-views** ([#153](https://github.com/JordanMarr/SqlHydra/issues/153)/[PR #159](https://github.com/JordanMarr/SqlHydra/pull/159), [PR #167](https://github.com/JordanMarr/SqlHydra/pull/167)). With `left_joined_views = true`, each table gets a `LeftJoined` twin whose every column is an option, so nullability is per column as in SQL. `ToOption()` recovers a whole-record option.
- Open [#71](https://github.com/JordanMarr/SqlHydra/issues/71) (2023) asks for selected left-joined columns to be wrapped in `Some`.

**Plasm:**

- Also `option` for nullable columns.
- `= None` → `IS NULL`, option-to-option equality → `IS NOT DISTINCT FROM`, plus `.IsSome`/`.IsNone`/`.Value`/`Option.get`.
- `leftJoin` gives `'T option`, with `Option.map`/`Option.bind`/`Option.exists`/`Option.forall` to reach nullable columns.
- `Agg.min/max/avg` return options **by type**, and `Agg.sum` of no rows is 0.
- `CheckSchema` reports a non-optional field over a nullable column.

Plasm's aggregate typing is stricter. SqlHydra's left-views are an ergonomic idea Plasm has not tried.

## 3. Relationships and loading

**SqlHydra has no relationship model.**

- The CLI does not read foreign keys. Grepping `src/SqlHydra.Cli` finds no FK catalog queries.
- Generated records have no navigation fields.
- The author rejected one-to-many hydration in #80 ("I purposefully avoided adding any 'ORM' style features"). His suggested pattern is two queries plus manual assembly in application code.

What exists:

- `join`, `leftJoin`, and predicate joins `join'`/`leftJoin'` with `on'` (full AND/OR conditions).
- Repeated joins to one table; they were broken until fixed: [#32](https://github.com/JordanMarr/SqlHydra/issues/32), [#113](https://github.com/JordanMarr/SqlHydra/issues/113).
- Subqueries (`subqueryMany`, `subqueryOne`), `whereExists`/`whereNotExists` with `correlate`.
- CTEs (`cte`, `cteFrom`) and PostgreSQL `lateralJoin`.
- `isIn`/`|=|` over a list or subquery.

So the N+1-free child load is hand-written:

```fsharp
let! orders = selectTask db { for o in dbo.Orders do where (o.CustomerId = cid); select o }
let ids = orders |> List.map _.Id
let! lines = selectTask db { for l in dbo.OrderLines do where (l.OrderId |=| ids); select l }
let byOrder = lines |> List.groupBy _.OrderId |> Map.ofList   // assemble yourself
```

**Split-query or `preload` equivalent:** none. `isIn` with a large list sends one parameter per element, and I saw no chunking (`grep -i chunk` in `src/SqlHydra.Query`). SQL Server's 2,100-parameter limit therefore falls on the user. *Not verified at runtime.*

**Plasm:**

- What a record owns or links follows from its field types (records holding its key are owned; `[<JoinTable>]` names a link table); the fields are plain values.
- What a record owns or links is loaded with every read of it, one `= ANY(@keys)` command per list per batch of keys, with configurable `MaxKeysPerCommand`. Nothing loads lazily, and nothing is left unloaded.
- Other aggregates are referred to by typed id and reached by joins, `exists (plasm { ... })` (a correlated EXISTS) or a second prepared query; `List.exists`/`forall` over owned and linked lists also compile to EXISTS.

This is Plasm's main advantage.

## 4. Writes

**Insert.**

- `entity` / `entities`; `entities` takes `AtLeastOne<'T>` or a seq, emitted as a multi-row `VALUES`.
- `getId e.Id` returns the generated identity.
- `returning (a.Id, a.ModifiedDate)` on PostgreSQL and SQLite; `output` on SQL Server.
- `fromSelect` for `INSERT ... SELECT`.
- Upserts:
  - `onConflictDoUpdate` / `onConflictDoNothing` / `doUpdateCoalesce` on PostgreSQL and SQLite; multi-row upsert works ([#67](https://github.com/JordanMarr/SqlHydra/issues/67)).
  - `insertOrUpdateOnUnique` on SQL Server: TRY/CATCH, retry, deliberately avoiding MERGE.
- No automatic splitting of large batches across parameter limits (I found no chunking code). *Not verified at runtime.*

**Generated columns** (v5):

- Tables with `GENERATED ALWAYS` columns get a `{table}_write` record without them, plus `IWriteOf`/`IHasWrite` interfaces.
- `writeEntity` takes only the write record, so writing a read-only column does not compile.
- Detection is PostgreSQL-only (README).

**Update.**

- `set` per column, or `entity` with `excludeColumn`/`includeColumn`.
- A mandatory `where` or explicit `updateAll` / `deleteAll`, which guards against accidental full-table writes.
- **No optimistic concurrency/version column support.** There is no `version` concept in the source (grep). *Unverified beyond grep.*
- No validation layer.

**Transactions.** Imperative on a shared context:

```fsharp
use! ctx = db.OpenContextAsync()
ctx.BeginTransaction()           // optional isolation level
do! updateTask ctx { ... } : Task
do! insertTask ctx { ... } : Task
ctx.CommitTransaction()          // failwith "No transaction was started." if none
```

- `QueryContext.fs` L92–135.
- No retry policy, no savepoints, and no transaction-as-value.
- Per-query `timeout` operation since v4.1 (#141).

**Plasm:**

- `Tx.update (original, changed)` writes only the columns that differ; no validation layer either (the domain's constructors validate).
- Optimistic concurrency by a version column or by the original record's columns (`TxFailure.Conflict`).
- `transaction { }` values with typed steps, `RetryPolicy` for serialisation failures, and savepoints in sandboxes.
- `Bulk.insert` split above 65,000 parameters in one transaction.
- `Bulk.update`/`delete`/`upsert`/`insertMissing`.
- Owned rows written with their owner by difference, and links as the new set.
- Plasm lacks `RETURNING` from bulk updates, `INSERT ... SELECT`, and `UPDATE ... FROM`. SqlHydra has `returning` on update and delete, and `fromSelect`.

## 5. Schema and migrations

**Database-first code generation.**

- `dotnet sqlhydra npgsql|mssql|sqlite|oracle|mysql` runs a wizard, then a TOML config. Its options: filters, per-schema modules, `left_joined_views`, readers, CLIMutable, nullable mode, extensions.
- The generated file is added to the `.fsproj` automatically (hidden).
- An optional MSBuild target regenerates on Rebuild in Debug.

**When the schema changes,** you regenerate:

- Renamed or dropped columns become **compile errors** at every use site. This is the main safety story.
- If you do not regenerate, you get runtime SQL errors. There is no runtime schema check.
- `VersionCheck.assertIsCompatible` checks only that the generated file came from a compatible CLI version, not that it matches the database (`src/SqlHydra.Query/VersionCheck.fs`).

**Other points.**

- No migrations; it reads whatever your migration tool produced.
- Extension points: custom providers (`ISqlHydraDbProvider`), type mappings (`IExtendTypeMapping`, e.g. the `SqlHydra.Query.Pgvector` package), and contributed columns (v5.1, `docs/writing-extensions.md`).
- PostgreSQL SQL functions are generated from `pg_proc` against an allowlist (README "PostgreSQL functions are generated from the catalog").

**Plasm:**

- Model-first. You write records with `[<Table>]` and a few field attributes, or use `repo.Scaffold "table"` in FSI to print a starting point (no relationships inferred).
- The schema is checked as the repository starts (`Repo.Start`), and by default a difference fails start-up, naming every one: missing tables or columns, incompatible types, nullability mismatches, unmapped NOT NULL columns, a unique field without its constraint, a reference without its foreign key, and an enum type whose labels differ from the union's cases.
- Also no migrations.
- Trade-off: SqlHydra catches drift at compile time *if you regenerate*. Plasm catches it at startup without generated code, but cannot make a dropped column a compile error.

## 6. Errors

**Exceptions throughout.**

- Database failures surface as provider exceptions (`PostgresException` 23505, `SqlException` 2627/2601, and so on), unwrapped and unclassified. No `Result` type appears anywhere in `src/SqlHydra.Query` (`grep "Result<"` returns nothing).
- A `select` that tries a .NET transformation (`select ($"City: {a.City}")`) throws at runtime (README "Transforming Results").
- An unmarked custom `sqlFn` throws `SqlFunctionNotRenderedException`.
- Commit or rollback with no transaction calls `failwith`.
- A stale generated file causes `failwith` with an upgrade message.
- Users wrap calls themselves (e.g. `AsyncResult.ofTask |> mapError` in [#92](https://github.com/JordanMarr/SqlHydra/issues/92)).

**Plasm:**

- `Result` values: transactions fail with `TxFailure.Rejected` (the domain's reason), `InvalidChanges` (per-field errors, from constraints with PostgreSQL's default names), `Conflict`, and `Failed` with a classified `DbFailureKind` (`UniqueViolation`, ...).
- `DbError.MoreThanOneRow`; `TranslationError.UnsupportedExpression`/`UnsupportedByProvider` naming the expression.

## 7. Database support and quality

Five built-in providers: SQL Server, PostgreSQL, SQLite (moved to Microsoft.Data.Sqlite in [PR #127](https://github.com/JordanMarr/SqlHydra/pull/127), April 2026), Oracle, and MySQL (CLI since 2024, [#100](https://github.com/JordanMarr/SqlHydra/issues/100)/[#101](https://github.com/JordanMarr/SqlHydra/pull/101)).

Test counts in the repo (`[<Test>]` attributes per folder, against AdventureWorks databases in dev containers):

| Provider | Test count | Notes |
|---|---|---|
| PostgreSQL | 241 | Most recent feature work targets it (RETURNING, ON CONFLICT variants, CTEs, lateral joins, write records, generated pg functions, enum registration, Pgvector). Effectively the best-served provider now. |
| SQL Server | 203 | The original provider; also runs the nullable mode. |
| SQLite | 73 | Type affinity caveats; DateOnly parsing fix [#123](https://github.com/JordanMarr/SqlHydra/issues/123); arm64 issue [#126](https://github.com/JordanMarr/SqlHydra/issues/126). |
| Oracle | 67 | Provider wrapper types need fallback conversions (`Hydration.fs` L139); quoting bug [#160](https://github.com/JordanMarr/SqlHydra/issues/160), fixed in Sept 2026. |
| MySQL | none | No integration-test folder in `src/Tests`; only a unit-test mention. In practice the least proven. *Inferred from repo layout.* |

Write-record detection for generated columns is PostgreSQL-only.

**Plasm:** PostgreSQL only, behind an `IProvider`/`ISqlDialect` boundary that has not yet been tested by a second provider.

## 8. Maintenance

**Activity.**

- Created 2021-04. MIT licence. 263 stars, 33 forks.
- **6 open issues** as of 2026-11-13: #152 (code-gen instead of reflection, for AOT), #119, #74, #71, #43 (UDFs/table-valued functions, from 2022) and #40 (tuples in where).
- 263 commits in the last 12 months: Jordan Marr 203, Michael Glass 53, others a handful (`git shortlog`). Effectively one maintainer plus one very active contributor since mid-2026.

**Recent release cadence** (NuGet publish dates):

- 3.3.0 (2026-01)
- 3.4 and 3.5 (2026-02)
- 3.6 (2026-04-02)
- 4.0 (2026-06-02, SqlKata removed)
- 4.0.1–4.0.4 (June)
- 4.1.0 (2026-08-23)
- 4.1.1 (2026-08-27)
- **5.0.0 (2026-09-08)**: removed the experimental `Diff.Compare`
- 5.1.0 (2026-09-28)
- 5.1.1 (2026-10-19)

That is three major versions in about nine months. It is active, but expect breaking changes and regenerating code on upgrade (`VersionCheck` enforces the CLI–runtime pairing).

**Users.**

- NuGet total downloads: SqlHydra.Query about 239k, SqlHydra.Cli about 99k (NuGet search API, 2026-11-13).
- The 5.0 commit message says the removed `Diff` feature had "zero public GitHub dependents". I could not check the dependents count for the package as a whole.
- Issue reporters mention professional use ([#67](https://github.com/JordanMarr/SqlHydra/issues/67)).

**Plasm:** 0.1.0-alpha, MIT licence, public repository, no package published, no production use.

## 9. Developer experience

**Idiomatic F#:**

- Immutable generated records, `option` for NULL, computation expressions reading like SQL.
- Compile-time checking against the real schema, and Task and Async variants.
- Conditional `where` via `opt.IsSome && col = opt.Value`; conditional `orderBy` via `^^`.
- `AtLeastOne` for non-empty batches.
- Escape hatch: `HydraReader` hydrates generated types from hand-written SQL. The README endorses plain SQL for the last 5–10% of queries.
- No hidden state: no change tracker, no lazy loading.

**Less idiomatic:**

- Generated types mirror tables (snake_case or PascalCase as in the database, `` `public` `` modules). They are DTOs, not your domain, so you write mapping code for every DU, wrapper or nested shape.
- `.Value` and `Some` noise in joins and option comparisons. Left-views reduce it.
- Runtime failures for constructs that type-check (transformations in `select`, unsupported expressions).
- Imperative transaction calls on a mutable context, and exceptions instead of Results.
- Writes take whole records or per-column `set`. There is no diff of what changed.
- Runtime reflection over records. #152 plans to move to generated metadata for AOT; the write path already moved in v5.
- Generation needs a live database at build time (or a committed generated file).

---

## What SqlHydra does that Plasm does not

- **Multiple databases:** SQL Server, SQLite and Oracle are tested; MySQL has no integration tests.
- **Code generation from the database:** schema drift becomes compile errors after regeneration. Plasm's `Scaffold` is one table at a time, prints source in FSI, and infers no relationships.
- **PostgreSQL enum types as CLR enums:** generated, with automatic `NpgsqlDataSourceBuilder.MapEnum` registration, which fits the Npgsql 8+ data-source model. Plasm maps an F# union to an enum type instead and generates nothing.
- **Generated read and write records for `GENERATED ALWAYS` columns:** writing a database-owned column is a compile error. In Plasm, assigning a generated field in a bulk write is rejected before execution, not at compile time.
- **Query features Plasm lacks:**
  - subqueries in `where` other than `exists` (`subqueryMany`/`subqueryOne`); Plasm has `exists` over a correlated query, and `selectDistinct`
  - CTEs
  - lateral joins
  - predicate joins with arbitrary AND/OR (`join'`/`on'`)
  - `INSERT ... SELECT`
  - `RETURNING` on update and delete
  - SQL Server `OUTPUT`
  - `nullsFirst`/`nullsLast`
  - per-query `timeout`
- **A catalog of SQL functions:** built-in per provider, PostgreSQL functions generated from `pg_proc`, user `sqlFn` wrappers, and infix-operator registration (pgvector).
- **Extensibility without forking:** custom providers, type mappings, contributed columns.
- **Left-views:** column-level nullability for left joins.
- **Maturity:** about 5 years old, about 239k downloads, active releases, MIT licence, an outside contributor.
- **Async variants** alongside Task. *Plasm's API is Task-based; not checked for Async.*

## What Plasm does that SqlHydra does not

- **Aggregates:** owned and linked records, loaded whole in batches (one `= ANY` command per list per key batch) and written by difference. SqlHydra has no model of related rows by design (#80); you write the second query and the grouping yourself.
- **Predicates over other aggregates and owned lists:** `exists (plasm { ... })` and `List.exists`/`forall` as correlated EXISTS.
- **Domain types in the model, by convention:** fieldless unions, unions with fields across columns, single-case wrappers, types with a `create` function, and typed keys that stop `CustomerId` and `OrderId` being mixed. SqlHydra maps only primitives, options and .NET enums.
- **Errors as values:** `Result` with classified failures (`UniqueViolation` and others), constraint-to-field mapping, `Conflict`, and the domain's own rejections. SqlHydra throws provider exceptions.
- **Writes by difference:** only changed columns, and owned rows by key; ownership is the removal policy.
- **Optimistic concurrency** via a version column, or by the original record's columns.
- **Transactions as values:** typed steps, retry policy, errors that say where the step is written.
- **Automatic batch splitting** above PostgreSQL's parameter limit, in one transaction.
- **Typed aggregates:** `min`/`max`/`avg` return options by type, and `sum` needs `+`/`Zero`. In SqlHydra you add `Some` by hand to avoid a NULL cast failure.
- **A runtime schema check** (`repo.CheckSchema`) and a **rolled-back test sandbox** and **FSI playground**; queries display their SQL in FSI.

## Could not verify

- How a DU or single-case-wrapper field behaves at runtime in SqlHydra (inferred from `boxValueOrOption`).
- Whether SqlHydra chunks large `isIn` or `entities` batches (no code found).
- MySQL provider quality in practice.
- The overall GitHub "used by" count for SqlHydra.
