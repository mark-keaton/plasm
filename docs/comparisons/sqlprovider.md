# SQLProvider vs Plasm

*Researched 2026-11-13. SQLProvider at commit `37927f4` (master, 2026-11-13), NuGet `SQLProvider` 1.5.31 (published 2026-11-13). Plasm as described in `README.md` and `docs/status.md` (as of 2026-11-12); Plasm-side statements corrected 2026-11-14 for the one style (relationships, preloads, public changesets, validation and repository writes were removed). The findings about SQLProvider are unchanged; it has since released 1.5.33, which these notes were not run against. Corrected again 2026-11-20 against `features/`: PostgreSQL enum types, unions with fields, composite keys, `selectDistinct`, `exists` and the start-up schema check.*

Evidence key: **[doc]** the project's docs say so; **[src]** I read it in the source; **[issue]** taken from the issue tracker; **[inferred]** my reading of the source, not run; **[unverified]** not checked.

## What SQLProvider is

SQLProvider (https://github.com/fsprojects/SQLProvider) is an *erasing F# type provider*. At compile time (and in the IDE) it connects to a live database, or reads a saved schema file or a SQL Server `.dacpac`. It then provides a `dataContext` type with one property per table/view, entity types with one mutable property per column, foreign-key navigation properties, stored procedures and "Individuals" (rows as IntelliSense members). Queries are F# `query { }` expressions translated from LINQ expression trees. Writes go through a change-tracking data context and `SubmitUpdates()`. Plasm is the reverse: you write records that name their tables, and the database is checked against the model at run time (`repo.CheckSchema`).

---

## 1. Enums and discriminated unions

**SQLProvider**

- **No union mapping at all.** Entities are provider-generated classes, so a column can't be typed as your union. Issue #392 "Discriminated Unions for Static Data" (opened 2017-04-12, still open) asks for it. The maintainer pointed to FSharp.Data.SqlClient's `SqlEnumProvider` (MSSQL only) and nothing has been built. The documented workaround is to map in the `select` projection or via `MapTo<'T>` onto your own record. `MapTo` maps by column name and supports `Option`/`ValueOption` targets (1.5.8, docs `mappers.fsx`). It has no union conversion **[doc; unverified that MapTo rejects unions rather than failing at run time]**.
- **PostgreSQL enum types are not mapped.** In `src/SQLProvider.Runtime/Providers.Postgresql.fs` the type table has `//"enum", typemap<obj> ["Enum"]` commented out (also `composite` and `range`). Columns are discovered with `format_type(atttypid, NULL)`, which returns the enum's own name (e.g. `order_status`), and the lookup `mappings.TryFind` misses it **[src]**. Before 1.5.23 an unknown type failed the whole table with "the type `X` is unknown to Npgsql type mapping" (same error text as #838). Since 1.5.23 (2026-06-12, #695) such columns are **skipped silently** ("Tolerance: the type is unknown to the Npgsql type mapping... Skip the column") **[src, release notes]**. So an enum column is **not visible in the entity at all** **[inferred from src]**. Inserting a row then can't set it, and a NOT NULL enum column without a default can't be inserted **[inferred]**. The workaround is a view casting `status::text`, or modelling enums as `text` + CHECK or as a lookup table.
- **PostgreSQL enum support was asked for in #427** (2017, still open; the thread includes `dataContext.Enums.Animals.Dog` ideas). It has not been implemented.
- **Npgsql 7/8+ `NpgsqlDataSource.MapEnum`.** SQLProvider opens connections with `new Npgsql.NpgsqlConnection(connectionString)` (`createConnection`, Providers.Postgresql.fs ~l.312) **[src]**. There is no `NpgsqlDataSource` and no hook to pass one in, so data-source-level `MapEnum` can't be used. The deprecated `NpgsqlConnection.GlobalTypeMapper` would still affect those connections, but it doesn't matter while the design-time schema skips the column. The provider package pins `Npgsql >= 8.0.7` (`paket.template`) **[src]**.
- **Npgsql version churn is a recurring source of breakage:** #838 (Npgsql 8 `timestamptz` unknown type, fixed 1.4.7 in Feb 2025, then an `InvalidCastException` DateTime/DateTimeOffset, mapped back to `DateTime`). Also #807 (Npgsql 8 needing `Microsoft.Extensions.Logging.Abstractions` at design time; PG16/Npgsql changed `array_dimensions` int32→int16) and **#869 (open since 2026-03-03)**: Npgsql 10 maps `date`→`DateOnly`, `time`→`TimeOnly`, and SQLProvider still maps `date` to `DateTime`, so reads throw `InvalidCastException`. The source still has `"date", typemap<DateTime>` **[src]**. The workaround in the thread (2026-08-26) is Npgsql's legacy date/time resolver.

**Plasm**

- `Scalar.enum [ (Pending, "pending"); ... ]` maps a fieldless union to text through an explicit table. It is checked: every case listed once, no duplicate names, unknown stored text is a materialisation error (`src/Plasm/Scalar.fs`). A union with fields is stored across columns by convention (`features/union-fields.feature`).
- **PostgreSQL native `CREATE TYPE ... AS ENUM` columns:** `Scalar.asPgEnum "type"` sends the union as text with a cast to the type and reads it as `::text`, with no `MapEnum`; the start-up check compares the type's labels with the union's cases (`features/pg-enums.feature`).

**Verdict:** Plasm has first-class, explicit union mapping, to text or to a PostgreSQL enum type. SQLProvider has none and currently hides PG enum columns.

## 2. Options and nulls

**SQLProvider**

- The static parameter `UseOptionTypes` takes `NullableColumnType.NO_OPTION | OPTION | VALUE_OPTION`. **The default is `NO_OPTION`** (`SqlDesignTime.fs` l.1496) **[src]**. With it, "you will always receive the default value of the column's type, even if it is null in the database" (docs `postgresql.fsx`). A NULL `int` reads as `0` unless you opt in.
- With `OPTION`, nullable columns become `'T option` and you filter with `.IsSome`/`.IsNone`/`.Value` (docs `querying.fsx` "When using Option types, check IsSome in where-clauses").
- Left joins: for years entities from a left join were not optional (#540, open since 2018). The workaround was the `(!!)` operator plus `(box a.Id) = null` checks. 1.5.24 (2026-06-12) added standard `leftOuterJoin ... into g; g.DefaultIfEmpty()`, where "unmatched columns are None/default and **a whole selected entity is null**" (release notes). So a left-joined entity comes back as a .NET `null`, not an `option`. The maintainer says on #540 (2026-06-17) it is "not properly tested yet".
- `OnConflict.Update` ignored `None` values on PostgreSQL (#814, closed 2024).

**Plasm**

- Options are part of the record type; non-optional fields over nullable columns are flagged by `CheckSchema`. `= None` translates to `IS NULL`, option-to-option equality to `IS NOT DISTINCT FROM`, and a left-joined record is `'T option` (status.md "Joins"). Nested options are a configuration error.

**Verdict:** Plasm is option-correct by construction. SQLProvider can be option-correct if you opt in, but its default and its left-join entity (`null`) are not.

## 3. Relationships and loading

**SQLProvider**

- Foreign keys appear as navigation properties named after the constraint, e.g. ``customer.``main.Orders by CustomerID`` `` (docs `general.fsx`). Enumerating one on an entity runs a query for that entity (`CreateRelated` → `QueryFactory.createRelated`, `SqlRuntime.DataContext.fs` l.152) **[src]**. Inside a `query { for c in ... do for o in c.``...`` }` it becomes a join, and outside a query it is **one query per parent (N+1)**.
- There is **no eager-loading/preload/include API and nothing like split queries.** The docs' own advice ("Recommended query style", `querying.fsx`) is: "**No N+1.** Don't run a query per row of another query. Join, or read the children of all the parents in one query (`parentIds.Contains child.ParentId`, with `parentIds` a sub-query) and **group them in memory**." That is a hand-written split query: you write the second query, the grouping and the assembly yourself.
- Joins: explicit `join`, navigation-property joins, `leftOuterJoin` (1.5.24/1.5.25), `groupJoin` with limits (1.5.25: aggregating a group "fails with a clear message instead of returning wrong results"). Results are tuples of entities or projections, so a join of parent + children returns the cartesian rows.
- Relationship writes: "doesn't have (yet) any automatic support for creating a properly ordered graph of related entities in a single transaction... SQL provider submits entities to a database in the same order they were created" (docs `constraints-relationships.fsx`). There are no many-to-many helpers.
- Sub-queries: `IN` via `|=|` or `.Contains` over an unexecuted query compiles into one SQL statement. Nested sub-query parameter binding was fixed only in 1.5.30 (2026-11-03).

**Plasm**

- What a record owns or links follows from its field types, is a plain field, and is loaded with every read of the record, each level with batched `= ANY(@keys)` commands. Command count is constant in the number of parents (3 commands for a page of orders with their lines and their payments, at 20, 100 and 1,000 orders; `docs/benchmarks.md`). Nothing is lazy and nothing is left unloaded; other aggregates are reached by joins, `exists` or a second query.

**Verdict:** this is Plasm's main advantage. SQLProvider gives you lazy navigation (an N+1 trap) and joins. Its docs tell you to do the batching yourself.

## 4. Writes and transactions

**SQLProvider**

- **Mutable, change-tracked entities.** `ctx.Main.Employees.Create()` then set properties, or typed ``Create(FirstName, LastName)`` (required columns in the name, so a new NOT NULL column breaks the build), `row.Delete()`, `ctx.SubmitUpdates()` / `SubmitUpdatesAsync()`. You can also update without reading by setting `_State <- Modified [cols]` (docs `crud.fsx`).
- `SubmitUpdates` wraps all pending entities of the context in a `TransactionScope(Required)`. It joins an ambient scope, otherwise it starts one whose **default isolation is Serializable** (docs `crud.fsx`, "Isolation level gotcha"). Reads aren't in the transaction unless you open your own `TransactionScope`, and async needs `TransactionScopeAsyncFlowOption.Enabled`.
- A context collects *every* pending change, so the docs say "**Create a fresh context per transaction — don't reuse a shared/long-lived one**", because a submit could commit another request's pending changes.
- **One command per entity**, sorted by creation order (`ProcessUpdates`, Providers.Postgresql.fs l.1378) **[src]**. No batching and no multi-row insert except through `OnConflict`.
- **Partial-failure state** **[inferred from src]**: each entity's `_State` is set to `Unchanged` right after its command runs, inside the scope. If a later command throws, the transaction rolls back but the earlier entities are already marked `Unchanged`, and the exception propagates (no catch). Retrying the same context would skip them. Not run.
- **No optimistic concurrency.** There is no version/rowversion support in the write path (grep found none). `locking.fsx` advises keeping a version column yourself and checking "zero rows changed". `SubmitUpdates` doesn't report affected rows for updates **[src]**. Pessimistic `Seq.lockForUpdate` (FOR UPDATE etc.) was added in 1.5.31, released the day of this research.
- Upserts: `OnConflict.DoNothing/Update` (PG, SQLite, MySQL), and from 1.5.31 named conflict columns and typed `row.OnConflictUpdate(...)`. SQLite's implementation was wrong (#796, open).
- Bulk/set-based: `Seq.``delete all items from single table``` (returns count since 1.5.23). I found no set-based UPDATE **[unverified]**.
- No validation layer. Constraint violations surface as provider exceptions.
- Transactionality bugs per driver: with Microsoft.Data.Sqlite, updates were not transactional (#816, 2024; a parent row persisted after the child insert failed).
- A read-only context type (`GetReadOnlyDataContext`) gives a compile-time read/write split (CQS).

**Plasm**

- Immutable records; every write a typed transaction step (`Tx.insert`, `Tx.update (original, changed)`, `Tx.delete`) that writes only what differs, owned rows by difference; constraint mapping by default names (unique/FK/check → field errors); optimistic concurrency by a `version` field or the original record's columns (`TxFailure.Conflict`); no validation layer (the domain's constructors validate). Also `Bulk.update/delete/insert/upsert` (multi-row, split above 65,000 parameters in one transaction), and an explicit repo with no ambient tracked state.

**Verdict:** SQLProvider's model is EF-style unit-of-work on mutable proxies, with ambient `TransactionScope`. Plasm's is explicit values and results. SQLProvider's typed ``Create(...)`` (compile-time required columns) is something Plasm checks only at run time, by the schema check as the repository starts.

## 5. Schema, migrations, and compile-time access in builds/CI

**SQLProvider**

- **No migrations and no DDL.** The schema is read from the database, so you need a separate migration tool (DbUp, Evolve, Grate, raw SQL).
- **Compile time needs schema access.** That means one of three things:
  1. A **live database at build time** (default). Connection string literal in source, or `ConnectionStringName`. The connection string "fetching it automatically from the application configuration is not supported" on .NET Core (docs `netstandard.fsx`).
  2. **`ContextSchemaPath`** offline file, generated by typing ``ctx.``Design Time Commands``.SaveContextSchema`` in the IDE. It is *lazy*: it saves only the part of the schema referenced so far, so "it is recommended that it be executed after the successful build of the whole solution", and you regenerate by deleting the file and reloading the solution (docs `parameters.fsx`). There is a history of this producing empty-column files: #644 (2019), #835 (2024, "the schema files are generated but they do not contain any column names"; the reporter: "I think there quite some caveats").
  3. **SSDT `.dacpac`** (SQL Server only), the maintainer's recommendation for MSSQL offline builds (#835 comment).
- The IDE caches the schema per session. After a DB change you restart the IDE or invoke `ClearDatabaseSchemaCache` by typing a dot (docs `general.fsx`).
- **CI:** your CI needs either a seeded database before `dotnet build` or a committed schema file kept in sync. The docs say "If you are running an off-line solution like SSDT or ContextSchemaPath, you should be able to run also these unit-tests with your CI" (`unittest.fsx`). SQLProvider's own CI (`.github/workflows/CI.yml`) has no DB services, and the FAKE target chain (`build.fsx` l.479–492) doesn't include `SetupPostgreSQL`/`SetupMSSQL`. So PostgreSQL/SQL Server tests don't appear to run in its CI **[src, inferred]**; SQLite, the mock context and the dacpac do.
- **Design-time / SDK / IDE problems** (a long tail, still recurring):
  - #875 (2026-05-30): `SQLProvider.PostgreSql` 1.5.21 design-time DLL fails to load with an FSharp.Core 10.1 mismatch. The build was fixed by an SDK update, but **VS Code/Ionide still couldn't see the namespace** (ionide/FsAutoComplete#1531, open: "FSharp.Core 10.1.0.0 assembly mismatch when loading type providers compiled against SDK 10.0.200+").
  - #871 / #873 (2026-03): with .NET SDK 10.0.200 / FSharp.Core 10.1, `select (tuple)` with `where`/`sortBy` generated `SELECT 1` and **returned all `None`**, silently wrong data. Fixed in 1.5.18/1.5.19. Cause: the compiler changed its LINQ expression-tree shape.
  - #847 (2025): assembly-name casing broke `dotnet build` in Linux Docker (fixed 1.5.8). The repo still has case-colliding `PostgreSql`/`Postgresql` project folders (they collided when I cloned on macOS).
  - #807 (2023/24): works in VS Code, fails in `dotnet build`. #792: works in Rider/VS Code, not in VS 2022. #783: VS fails, `dotnet` works. #826: SQLite runs but the editor shows errors.
  - 1.5.30 (2026-11-03): "builds don't fail at random with FS0193 'dataContext.mainSchema is not compatible with readDataContext.mainSchema'".
  - Things improved in 1.5.0 (Apr 2025). Per-database packages (`SQLProvider.PostgreSql` etc.) bundle fixed driver versions so `ResolutionPath` isn't needed (#868), and 1.5.3 fixed "platform not supported" on .NET 8/9. The generic `SQLProvider` package still uses `ResolutionPath`.

**Plasm**

- No migrations either (deliberate, ADR 0001). The model is code, so **builds need no database**. `repo.CheckSchema()` reports drift at start-up/in tests (missing tables/columns, incompatible types, non-optional over nullable, unmapped NOT NULL without default). Tests run against Docker PostgreSQL.

**Verdict:** for CI and reproducible builds Plasm is far simpler. SQLProvider gives you a stronger guarantee: drift is a *compile* error against the schema you built with. Plasm finds drift at run time.

## 6. Errors: exceptions or values

**SQLProvider:** exceptions throughout. Query translation problems are run-time exceptions (the query compiles because it is a LINQ expression, e.g. "IN / NOT IN sub-queries with a projection computed in .NET ... fail with an error", 1.5.30). DB errors come back as driver exceptions from `SubmitUpdates`, read-only misuse is `failwith "Context is readonly"`, and type mismatches show up as `InvalidCastException` (#838, #869) **[src, release notes]**. There are no Result-returning APIs. `Seq.tryHeadAsync` returns an option for "no row" (docs `async.fsx`). Design-time errors are compiler errors (FS3033 etc.) with the provider's message.

**Plasm:** `Result` values with typed unions (`DbError`, `TranslationError.UnsupportedExpression/UnsupportedByProvider`, and `TxFailure<'E>`: `Rejected/InvalidChanges/Conflict/Failed/Misused`), classified `DbFailureKind`. Exceptions only for programming errors found before database work, and cancellation (status.md "Failures").

## 7. Database support (evidence of state per provider)

SQLProvider supports far more databases: MSSQL (+SSDT dacpac), PostgreSQL, MySQL (MySql.Data and MySqlConnector), SQLite (System.Data.SQLite and Microsoft.Data.Sqlite), Oracle, Firebird, DuckDB, MS Access and ODBC ("Experimental, only supports SELECT & MAKE", `general.fsx`). Since 1.5.0 there is one NuGet package per database, all at 1.5.31.

| Provider | Evidence of state |
|---|---|
| **PostgreSQL** | Most active. Open: #869 Npgsql 10 `date`/`time` (DateOnly/TimeOnly) InvalidCastException; #427 composite types/enums; #400 `Owner` param; #540 left joins. No enum/composite/range types **[src]**, unknown-type columns skipped. Recent fixes: matviews (#136, 1.5.23), PostGIS columns skipped (#695), identity columns treated as generated (1.5.30, breaking), FOR UPDATE (1.5.31), functions inside TransactionScope (1.5.31). Not exercised in project CI **[inferred]**. NuGet `SQLProvider.PostgreSql`: 12,215 downloads, the highest of the per-database packages. |
| **SQL Server** | Mature; SSDT/dacpac gives real offline builds. Open #791 (sprocs with schemas), #851 (Azure Synapse). #507 count over sorted query fixed in 1.5.23. MERGE/upsert not supported (`crud.fsx`). Locking via UPDLOCK/ROWLOCK. |
| **SQLite** | The provider the docs and the project's tests use (Northwind). Open #841 (typed ``Create(...)`` misses NOT NULL PK columns), #796 (OnConflict uses the wrong SQLite feature). #816 (Microsoft.Data.Sqlite writes not transactional, closed 2024). The old `netstandard.fsx` table says build is Windows-only for SQLite. That page is stale (it describes .NET Core 2.0 and Mono), so I didn't rely on it **[unverified current state]**. |
| **MySQL** | 78 issues ever, **0 open** in the search sample. Fixes: boolean mapping (1.5.5), inserted PK read back (#638, 1.5.23). Two driver packages. |
| **Oracle** | 4 open (#778 quoted identifiers, #682, #624, #595 keyword escaping, 2019–2022 and untouched). Docs page is 38 lines. `lockForUpdate` limited with skip/sorted take. Least maintained of the big ones **[inferred from issue ages]**. |
| **DuckDB** | Added 2024 (#819). 1.5.31 fixed basic bugs: "DuckDB found no table's primary key ... so updates, deletes and OnConflict.Update failed", and OnConflict emitted MySQL syntax. So writes had been broken until this release **[release notes]**. |
| **Firebird / Access / ODBC** | Firebird: identity-return fix (#760). Access/ODBC: refuse locking; ODBC experimental. |

Download counts per package (`SQLProvider.Oracle` 8,659, `.Firebird` 8,694, `.DuckDb` 8,959, `.SQLite` 8,799) sit close together. Likely a floor of mirror/bot downloads, so real per-database usage is hard to read from them **[inferred]**.

**Plasm:** PostgreSQL (Npgsql) and SQLite (Microsoft.Data.Sqlite), plus a `Plasm.PGlite` project in `src/`, each behind an `ISqlDialect`/capability abstraction.

## 8. Maintenance

- **Age and scale:** started 2014–15, 626 stars, 149 forks. The `SQLProvider` package has 917,792 total downloads (all versions, since about 2015) **[NuGet API, 2026-11-13]**.
- **Cadence:** very active. 1.5.0 → 1.5.31 between 2025-04-24 and 2026-11-13. In 2026: 1.5.18 (03-31), .19 (04-07), .20 (05-26), .21 (05-28), .22 (06-11), .23 (06-13), .24 (06-14), .25 (07-03), .26 (07-10), .27 (07-29), .28 (10-17), .29/.30 (11-03), .31 (11-13). Gap Sep 2025 → Mar 2026.
- **Bus factor: about one.** Of 59 commits since 2025-11-13, 54 are by Thorium (Tuomas Hietanen), 4 by knocte, 1 other. All-time: Thorium 1,182, then colinbull 233, piaste 184, pezipink 181 (original author). On #875 (2026-07-09) Thorium wrote "I think the maintainers are on holiday".
- **Issues:** 56 open. 41 of them were opened 2015–2021; 2025: 5, 2026: 3. Many old issues were touched on 2026-06-12 (a triage sweep). Responses are often same-day (#838 fixed in a day; #847 next day).
- **Users:** confirmed in issue threads (enterprise/legacy MSSQL users, e.g. #835). The maintainer says it is "rumoured" to be in old enterprise systems (#838). I didn't verify any public production users **[unverified]**.

**Plasm:** first delivery 2026-11-12, no releases on NuGet ("once published"), no production use (status.md).

## 9. Developer experience

**Where SQLProvider feels like good F#**

- Zero mapping code: point it at a database and get typed tables, columns, FKs, sprocs and column comments as tooltips. Very good for exploring in scripts/FSI and for legacy or large databases (`TableNames` filter, `Individuals`).
- `query { }` is standard F#. Queries compose as unexecuted `IQueryable`s, sub-queries nest, and there's async/`task` execution (`Seq.executeQueryAsync`, `Seq.tryHeadAsync` → option).
- Schema drift is a compile error. The typed ``Create(Col1, Col2)`` makes adding a NOT NULL column a build break.
- A mock data context for unit tests without a database (`unittest.fsx`), `QueryEvents.SqlQueryEvent` for SQL logging, and a read-only context type.

**Where it doesn't**

- Entities are mutable, erased `SqlEntity` (a `Dictionary<string,obj>` wrapper, `techdetails.fsx`). They aren't your domain types, so "when it leaves the function... build your real domain type in the `select`" (docs). Every domain record, option wrapper and union conversion is hand-written in projections.
- Nulls default to the type's default unless you opt in. Left-joined entities are `null`. There are no unions.
- Change tracking with an ambient `TransactionScope` (Serializable by default, async flow flag needed), and a "fresh context per transaction" rule. That's the EF/LINQ-to-SQL model, not F#'s values.
- Exceptions, not Results. Many query mistakes fail at run time, not compile time, despite the type provider.
- Translation depends on compiler-emitted LINQ expression shapes. An SDK update (10.0.200) silently returned wrong data (#871).
- Tooling fragility: design-time assembly loading, FSharp.Core/SDK mismatches, Ionide vs Rider vs VS differences, IDE restarts to refresh schema, the magic "type a dot to run a design-time command" UX, and committed schema files that are lazily partial.
- Backtick constraint names like ``main.Orders by CustomerID`` and names derived from DB naming (`CaseSensitivityChange`).

---

## What SQLProvider does that Plasm does not

- **Compile-time schema checking**: removed/renamed columns break the build, and typed ``Create(...)`` enforces required columns at compile time. Plasm checks at run time: the schema check runs as the repository starts and by default fails start-up.
- **No mapping code at all**; you can explore an unknown database from IntelliSense or FSI, including column comments and `Individuals` (rows as members).
- **Many databases**: SQL Server (+ dacpac offline builds), MySQL/MariaDB, SQLite, Oracle, Firebird, DuckDB, Access, ODBC. Plasm has PostgreSQL and SQLite.
- **Stored procedures and functions** as typed members, including refcursors and multiple result sets (`programmability.fsx`, sproc fixes #877/#878). Plasm has `repo.Sql` only **[unverified whether Plasm calls functions beyond raw SQL]**.
- **Views and materialized views** typed automatically. Composite keys are at least representable (entity per table) **[unverified for writes]**. Plasm has composite keys (`features/composite-keys.feature`) and no views.
- **A mock data context** for testing query logic without a database.
- **Row locking** (`Seq.lockForUpdate`, 1.5.31) across five databases; Plasm locks on PostgreSQL only: `locking RowLock.ForUpdate` and the other three strengths, waiting, `NoWait` or `SkipLocked`, read only inside a transaction (`features/row-locks.feature`).
- **Maturity**: about 11 years, ~918k downloads, a responsive maintainer, and fixes shipped within days.
- Set unions, `groupJoin` and sub-queries in more positions through LINQ **[doc]**. Of these Plasm has only `exists` over a correlated query; it has `selectDistinct`.

## What Plasm does that SQLProvider does not

- **Your own immutable records** as entities, with what they own or link as plain fields, always loaded.
- **Union mapping** (fieldless unions, and unions with fields across columns), single-case wrappers and validating `create` functions on columns, by convention. SQLProvider has none (#392, open since 2017).
- **Options by construction**, with `IS NOT DISTINCT FROM` semantics and left joins as `'T option`. SQLProvider defaults to no options and returns `null` for left-joined entities.
- **Batched loading of whole aggregates**: every level, constant command count, no cartesian product. SQLProvider has lazy navigation (N+1 outside queries), joins, and "do it yourself and group in memory" advice.
- **Writes by difference**: field-level errors from constraints, **optimistic concurrency** (`version`, or the original record's columns), and owned rows and links written with their owner. SQLProvider has mutable proxies and no concurrency tokens.
- **Results, not exceptions**, with classified failures and typed transaction steps.
- **Set-based bulk writes** (`Bulk.update` with computed assignments, multi-row insert/upsert split over the parameter limit). SQLProvider writes one statement per entity.
- **No database needed to build**, so there's no design-time assembly loading, IDE schema cache or schema file. CI only needs a database for tests.
- **Prepared queries**, a rolled-back test sandbox (`repo.Sandbox`), an FSI playground, and typed interpolated raw SQL (`FormattableString` only).
- **Explicit transactions on the repo**, not ambient `TransactionScope` with a Serializable default.

**Caveat both ways:** Plasm's advantages are verified by its own acceptance scenarios and unit tests ([acceptance.md](../acceptance.md)) but have had no production use and no published release. SQLProvider's weaknesses above come from docs, source reading and issues, not from running it. The PostgreSQL-enum column skip and the partial-failure `_State` behaviour are inferred from source and should be confirmed with a run before anyone relies on them.
