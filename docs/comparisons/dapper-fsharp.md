# Dapper.FSharp compared with Plasm

Researched 2026-11-13. Dapper.FSharp **4.10.0** (NuGet, 2026-04-01; depends on Dapper >= 2.1.72 and FSharp.Core >= 10.1.201; targets net6.0 to net10.0). Repository at commit `a83c9d5` (master, 2026-04-01). Plasm as of `docs/status.md` dated 2026-11-12; Plasm-side statements corrected 2026-11-14 for the one style (relationships, preloads, public changesets, validation and repository writes were removed). The findings about Dapper.FSharp are unchanged.

Sources: [README](https://github.com/Dzoukr/Dapper.FSharp/blob/master/README.md), [src](https://github.com/Dzoukr/Dapper.FSharp/tree/master/src/Dapper.FSharp), [NuGet](https://www.nuget.org/packages/Dapper.FSharp), [issues](https://github.com/Dzoukr/Dapper.FSharp/issues).

**What it is.** Dapper.FSharp is a thin SQL generator on top of Dapper. Four computation expressions (`select`, `insert`, `update`, `delete`) build plain query records (`SelectQuery`, `InsertQuery<'a>` and so on), and extension methods on `IDbConnection` (`SelectAsync`, `InsertAsync`, ...) turn them into SQL plus a `Map<string,obj>` of parameters and call Dapper's `QueryAsync` / `ExecuteAsync`. The author states its scope plainly: "Simplify 90% of repetitive SQL queries you would have to write manually. Nothing. Else." For anything else the advice is to "fallback to plain Dapper". It is not an ORM. It has no model, no relationships, no change tracking and no identity map. Record field names must equal column names.

---

## 1. Enums and discriminated unions

**Dapper.FSharp**
- There is no union support of any kind. The library registers only `option<'T>` handlers for built-in scalars ([OptionTypes.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/OptionTypes.fs)). It also ships helper bases `TypeHandlerWithParser<'T>`, `TypeHandlerWithConverter<'T>` and their option variants, so you can write a Dapper `SqlMapper.TypeHandler` yourself.
- [#100](https://github.com/Dzoukr/Dapper.FSharp/issues/100) (open since 2023-11, 3 upvotes, no maintainer reply) asks for built-in handlers for fieldless and single-case unions and includes a community implementation.
- [#88](https://github.com/Dzoukr/Dapper.FSharp/issues/88) (open; comments 2025-09): a user reports that a single-case-union type handler is not invoked through the `insert` CE, although it works with plain Dapper SQL. The maintainer replied: "avoid any 'complex' data in records used for DB queries … do the mapping from DU into Int32 … the intention of Dapper.FSharp is to be as close to DB as possible." The same thread defers jsonb to plain Dapper. *Why the handler is skipped is unverified.* The parameter path runs values through `Reflection.boxify`, which unwraps any object that has a `Value` property, and then puts them in a `Map<string,obj>`.
- Dapper itself has long-standing limits that apply here. [DapperLib/Dapper#259](https://github.com/DapperLib/Dapper/issues/259) "Custom type handlers for enums are ignored" has been open since 2015. Type handlers are also not applied to list-expanded parameters ([#1649](https://github.com/DapperLib/Dapper/issues/1649), [#1972](https://github.com/DapperLib/Dapper/issues/1972)), and `isIn` uses list expansion.
- **PostgreSQL enum types / Npgsql 7+.** Dapper.FSharp does not reference Npgsql. You pass any `IDbConnection`, so a connection from `NpgsqlDataSource` built with `NpgsqlDataSourceBuilder.MapEnum<T>()` works as a connection. The documented replacement for the obsolete `NpgsqlConnection.GlobalTypeMapper` is the data-source mapping ([Npgsql enums docs](https://www.npgsql.org/doc/types/enums_and_composites.html)). However, Dapper converts .NET enums to their underlying integer when it binds parameters, so a mapped CLR enum reaches PostgreSQL as `integer` and fails with 42804 ("column is of type X but expression is of type integer"). Reports: [SO 39880743](https://kiwix.lema.org/stackoverflow.com_en_all_2023-11/questions/39880743/how-to-map-c-enum-to-postgresql-enum-using-dapper-fastcrud), Dapper#259. *I did not run this against Dapper 2.1.72 + Npgsql 8/9/10 (unverified).* F# unions are not CLR enums, so `MapEnum` does not apply to them at all. In practice you would store text (or a PG enum fed by text with an explicit cast in plain Dapper SQL) and map it in your own code.
- There is no issue about the `MapEnum` / global-mapper change in this repo (searched open and closed issues, 2026-11-13).

**Plasm**
- `Scalar.enum` maps a fieldless union to text through an explicit, complete table. Missing cases, duplicates and unions with fields are configuration errors, and unknown stored text is a materialisation error (`src/Plasm/Scalar.fs`). `Scalar.wrap` and `Scalar.tryWrap` cover single-case wrappers and validating constructors. Unions with fields are rejected as configuration errors.
- Native PostgreSQL enum columns: Plasm sends text, and I found no test that uses `CREATE TYPE ... AS ENUM` in `tests/`, `features/` or `koans/`. Whether a text parameter is accepted by a PG enum column under Plasm's parameter typing is **unverified** (Npgsql typically requires an untyped or unknown parameter, or an explicit cast). Plasm builds on `NpgsqlDataSource` (`src/Plasm.Postgres/Postgres.fs`), so data-source-level mapping is where it would hook in. Both libraries have this gap.

**Verdict:** Plasm is clearly ahead for unions: they are first-class, explicit and validated. Neither has verified PostgreSQL native enum support.

## 2. Options and nulls

**Dapper.FSharp**
- `Dapper.FSharp.PostgreSQL.OptionTypes.register()` must be called once at startup. It registers global Dapper `OptionHandler<'T>` for 20 scalar types ([PostgreSQL/OptionTypes.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/PostgreSQL/OptionTypes.fs)). This is global mutable state. Per-provider registration lists drift: SQLite lacked `string option` until [#106](https://github.com/Dzoukr/Dapper.FSharp/issues/106) (fixed in 4.9.0, 2024-08), and `float` is still reported missing in [#102](https://github.com/Dzoukr/Dapper.FSharp/issues/102) (open). Arrays: [#58](https://github.com/Dzoukr/Dapper.FSharp/issues/58).
- In `where`, `p.X = None` becomes `IS NULL`, `p.X = Some v` becomes a comparison, and there are `isNullValue` / `isNotNullValue` helpers. The builder has no `.IsSome` / `.IsNone` translation.
- Left-joined records come back as `'T option` through `SelectAsyncOption`. The split column for each joined record is its **first field**, and `None` means Dapper produced `null` for that slice ([IDbConnection.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/IDbConnection.fs), `Reflection.makeOption`). *Inference, unverified:* a matched row whose first column is NULL would also become `None`.
- Materialisation goes through Dapper's constructor matching for records. Type mismatches between column and field (for example SQLite `INT` to `bool`, `REAL` to `decimal`) fail at runtime, and users work around them with `[<CLIMutable>]` ([#96](https://github.com/Dzoukr/Dapper.FSharp/issues/96), open, also reported on MSSQL).

**Plasm**
- Options are built in for every supported scalar, with no global registration. `= None` becomes `IS NULL`. Option-to-option equality uses `IS NOT DISTINCT FROM`, which follows F# semantics. `.IsSome`, `.IsNone`, `.Value`, `Option.exists`, `Option.forall`, `Option.map` and `Option.bind` translate. `CheckSchema` reports non-optional fields over nullable columns. Nested options are configuration errors.

**Verdict:** both treat `option` as NULL. Plasm's handling is more complete and does not depend on global state. Dapper.FSharp's is adequate for simple CRUD.

## 3. Relationships and loading

**Dapper.FSharp**
- There are no relationships in the model. You get `innerJoin` / `leftJoin` with up to 4 joins (5 tables), mapped by Dapper multi-mapping to flat tuples (`SelectAsync<'a,'b,...>`, `SelectAsyncOption<...>`) ([PostgreSQL/IDbConnection.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/PostgreSQL/IDbConnection.fs)). The README is inconsistent here: its FAQ still says "limitation is 3 tables", while the Joins section says 4 joins. Join keys must be equalities. Multi-column joins use tuples, `((a.X, b) = (c.Y, c.Z))`, with quirks: an inline `DateTimeOffset.MaxValue` fails while a let-bound value works ([#77](https://github.com/Dzoukr/Dapper.FSharp/issues/77), open). The contributor advice there is to move extra conditions to `where`.
- Graph assembly is your job. You get a row per joined combination, so a cartesian product across two child collections, and you group the tuples yourself (`List.groupBy`). To avoid N+1 you write your own second query with `isIn` over parent keys and stitch the results. Nothing batches, deduplicates or nests for you.
- There is no subquery, `EXISTS`, `UNION` or CTE support ([#101](https://github.com/Dzoukr/Dapper.FSharp/issues/101) union closed).

**Plasm**
- What a record owns (`owns`, `ownsOptional`, `ownsOne`) or links to through a join table (`links`) is declared in the schema and is a plain field, always loaded: one batched `= ANY(@keys)` command per list per level. Other aggregates are referred to by typed id and reached by joins, `exists (plasm { ... })` or a second query; a page of orders with their lines and their payments takes 3 commands at any parent count. `List.exists` / `List.forall` over owned and linked lists compile to correlated `EXISTS`. Joins are explicit flat tuples, with left-joined records as options, just like Dapper.FSharp's, but with no fixed arity limit stated and with composite keys.

**Verdict:** this is the dimension that matters most for the stated goal ("as easy as AsSplitQuery"), and Dapper.FSharp has nothing comparable. Its joins are on a par with Plasm's `join` / `leftJoin` for flat reads.

## 4. Writes and transactions; generated keys

**Dapper.FSharp**
- `insert { into t; value r }` / `values [..]` produces a single multi-row INSERT with parameters `Field0`, `Field1`, and so on. I found no splitting for parameter limits (unverified beyond reading `GenericDeconstructor.fs`). `excludeColumn` / `includeColumn` are available, with the README warning "never mix both".
- `update { set record; where ... }`, `includeColumn` for partial updates, and `setColumn p.X v`. `match` and `if` are not allowed inside the CE, so conditional partial updates are awkward ([#104](https://github.com/Dzoukr/Dapper.FSharp/issues/104), open; FS3086). `andWhereIf` / `orWhereIf` exist for selects (4.7.0).
- `delete` requires `where` or an explicit `deleteAll`, which guards against accidental full deletes.
- **Generated keys:** use `InsertOutputAsync<'Input,'Output>`. On PostgreSQL it emits `INSERT ... RETURNING "col", ...` ([Evaluator.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/PostgreSQL/Evaluator.fs)), and on MSSQL `OUTPUT`. You normally insert a separate record type without the identity column and read back an output record type ([#80](https://github.com/Dzoukr/Dapper.FSharp/issues/80) open: "Can not set explicit value on identity", and the docs gap acknowledged by the maintainer). `UpdateOutputAsync` and `DeleteOutputAsync` also exist.
- **Upserts:** none for PostgreSQL. The maintainer declined because the DSL had to be identical across databases ([#22](https://github.com/Dzoukr/Dapper.FSharp/issues/22), 2021). v4 later split the DSL per provider ([#71](https://github.com/Dzoukr/Dapper.FSharp/issues/71)), but only SQLite got `InsertOrReplaceAsync`.
- **Transactions:** pass an `IDbTransaction` as the optional `trans` argument to each call ([#85](https://github.com/Dzoukr/Dapper.FSharp/issues/85), [#17](https://github.com/Dzoukr/Dapper.FSharp/issues/17)). You manage begin, commit, rollback and isolation with ADO.NET. There is no transaction abstraction, retry or optimistic concurrency, and no validation.

**Plasm**
- Every write is a step of a typed transaction: `Tx.insert`, `Tx.update (original, changed)` (only the columns that differ; owned rows by difference; links as the new set) and `Tx.delete`. Plasm validates no input (the domain's constructors do); constraints with PostgreSQL's default names (unique, foreign key, check) come back as field errors; concurrency is checked by a version field or by the original record's columns (`TxFailure.Conflict`). `transaction { }` provides isolation levels and an opt-in `RetryPolicy`, and the connection and transaction are not exposed to steps. Bulk `update`, `delete`, `insert` (split above 65,000 parameters in one transaction), `upsert` and `insertMissing` are supported. Generated keys are handled through `identityKey` / `generated` with `RETURNING`.

**Verdict:** Plasm is far richer. Dapper.FSharp is honest, explicit CRUD where you own the transaction.

## 5. Schema and migrations

**Dapper.FSharp:** none. There are no migrations, no DDL and no schema check. Its only mapping rule is that field name equals column name. Snake_case mapping was declined ([#21](https://github.com/Dzoukr/Dapper.FSharp/issues/21)), and so were attributes and name mapping ([#97](https://github.com/Dzoukr/Dapper.FSharp/issues/97), README FAQ: "Nope. Sorry. Not gonna happen"). PostgreSQL identifiers are quoted, so with snake_case tables your record fields must be snake_case. `table'<T> "name" |> inSchema "s"` sets the table name and schema.
**Plasm:** also no migrations, by design. It adds `repo.CheckSchema()` (missing tables and columns, incompatible types, nullability mismatches, unmapped NOT NULL columns), default snake_case with per-field overrides, and `repo.Scaffold`.
**Verdict:** neither migrates. Plasm checks the schema and maps names, Dapper.FSharp does neither.

## 6. Errors: exceptions or values

**Dapper.FSharp:** exceptions throughout. Methods return `Task<seq<'T>>` / `Task<int>`, and database errors surface as provider exceptions (`PostgresException`). Unsupported expressions in a CE throw `NotImplementedException` at runtime. Examples: the README warns against using `|>` inside queries "unless you like exceptions", and after the F# 10 compiler changed expression shapes, joined queries threw `NotImplementedException` from `visitWhere` until 4.10.0 ([#111](https://github.com/Dzoukr/Dapper.FSharp/issues/111), fixed 2026-04-01). Materialisation mismatches are Dapper exceptions (#96). Nothing is a `Result`.
**Plasm:** expected failures are values: `Result` with `DbError` (a classified `DbFailureKind`, `Materialization`, `Translation`, `MoreThanOneRow`) for reads, and `TransactionError<'E>` with a typed `TxFailure<'E>` (`Rejected | InvalidChanges | Conflict | Failed | Misused`) for transactions. Exceptions are reserved for programming errors and cancellation.
**Verdict:** Plasm matches the F# Result idiom. Dapper.FSharp matches plain .NET style.

## 7. Database support

**Dapper.FSharp:** MSSQL 2012+ / Azure SQL, MySQL 8.0, PostgreSQL 12+ and SQLite 3, each in a separate namespace with its own query records since v4 (2022-11, [#71](https://github.com/Dzoukr/Dapper.FSharp/issues/71)). Tests for all four run in Docker ([docker-compose.yml](https://github.com/Dzoukr/Dapper.FSharp/blob/master/docker-compose.yml)). Database-specific extras are thin:
- MSSQL: `optionRecompile`, `optionOptimizeForUnknown`, `OUTPUT`.
- PostgreSQL: `iLike` / `notILike` and `RETURNING`. No upsert, JSON/jsonb ([#69](https://github.com/Dzoukr/Dapper.FSharp/issues/69), open), full-text search ([#83](https://github.com/Dzoukr/Dapper.FSharp/issues/83), open), arrays or enums. A 2026 report says a Guid `isIn` behaves oddly on PostgreSQL ([#113](https://github.com/Dzoukr/Dapper.FSharp/issues/113), open, awaiting a repro).
- SQLite: `InsertOrReplaceAsync`. Type-affinity friction (#96, #102).
- MySQL: no `InsertOutput` ([#61](https://github.com/Dzoukr/Dapper.FSharp/issues/61)).

Roughly, MSSQL and PostgreSQL get the most attention. All four cover the same basic CRUD subset. *Relative quality per database is my reading of the issues, not measured.*
**Plasm:** PostgreSQL only. A provider and dialect boundary with capabilities exists, but no second provider has been built.
**Verdict:** Dapper.FSharp wins on breadth. On PostgreSQL depth, Plasm offers more (arrays via `= ANY`, upserts, snapshot reads, row locking, SQLSTATE classification).

## 8. Maintenance

- Releases, from NuGet: v4.0.0 2022-11-07, then frequent releases through 4.7.0 (2023-08-28), 4.8.0 (2024-03-27, .NET 8), 4.9.0 (2024-08-26) and 4.10.0 (2026-04-01, an F# 10 / .NET 10 fix contributed in [PR #112](https://github.com/Dzoukr/Dapper.FSharp/pull/112)). That is about one release per year since 2024, mostly fixes. The last push was 2026-04-01. The repository has no GitHub Releases; versions are tags and NuGet only.
- 407 stars and 35 forks; MIT; a single maintainer (Roman Provazník, Dzoukr) plus occasional contributors (Jordan Marr, Jindřich Ivánek). There are 15 open issues, the oldest from 2021 (#34), and several feature requests have no reply (#99 `IAsyncEnumerable`, #100, #108). The maintainer still answers within days (#113, 2026-04). Open [#105](https://github.com/Dzoukr/Dapper.FSharp/issues/105): 4.8.0 broke the advertised .NET 6 minimum. *Unverified:* the 4.10.0 nuspec's `FSharp.Core >= 10.1.201` may force a recent FSharp.Core on consumers.
- NuGet downloads: 427.0K in total, 19.6K for 4.10.0 and 143K for 4.9.0. Its user base is real and modest; it is one of the more widely used F# SQL helpers.
- **Plasm:** pre-release, unpublished, no license yet, no production use (status.md). Dapper.FSharp is far ahead on maturity and real-world use.

## 9. Developer experience

**Dapper.FSharp**
- The CEs use `for p in table do` with custom operations. `where`, `orderBy`, `groupBy` and join keys are LINQ-expression parameters, so **field names are checked at compile time** (`p.FirstNmae` does not compile). Aggregates take **strings** (`count "*" "Value"`, `max "Position" "Value"`), and the alias must match a field of the result record by hand, so typos there are caught only at runtime. The result type is a free type argument (`conn.SelectAsync<Person>`) that is **not tied to the table** in the query. A wrong record or a missing column is a runtime Dapper error, and so is a wrong tuple arity in joins.
- Unsupported expressions in `where` compile and then throw at runtime (`notImpl()` in [LinqExpressionVisitors.fs](https://github.com/Dzoukr/Dapper.FSharp/blob/master/src/Dapper.FSharp/PostgreSQL/LinqExpressionVisitors.fs)), as do `|>` and helper functions. There is no `match` / `if` inside the CEs (#104). Reusable query fragments have no documented composition story ([#108](https://github.com/Dzoukr/Dapper.FSharp/issues/108), open). A pipeline API was requested ([#86](https://github.com/Dzoukr/Dapper.FSharp/issues/86)). `selectAll` is needed when there is no other clause.
- **Where it feels idiomatic:** queries are plain immutable records you can inspect, and `Deconstructor` gives you `(sql, Map)` to log or hand to plain Dapper. There are no attributes and no magic, it works with anonymous records for partial reads, and the per-call `logFunction` and `CancellationToken` help. Learning cost is tiny if you know SQL, and dropping to raw Dapper is seamless.
- **Where it does not:** global type-handler registration, exceptions instead of Results, field names forced to match columns, unions unsupported and discouraged by the maintainer (#88), and no options in `isIn` until a fix in 2021 (#46).

**Plasm**
- Quotation-based `plasm { }` queries are typed end to end. Result types follow from the query. `Agg.sum` / `Agg.avg` carry type constraints, so summing text does not compile. Prepared queries, `Raw.fragment` with format-string typing, interpolated `repo.Sql` where a plain string does not type-check, FSI display of SQL, and koans are all available. Some checks are at plan time rather than compile time (grouping validity, unsupported expressions return `UnsupportedExpression`). The cost is a larger surface to learn and higher per-call overhead when not prepared (about 80 µs to build plus 60 µs to plan, per status.md).

---

## What Dapper.FSharp does that Plasm does not

- Supports **four databases** (MSSQL, MySQL, PostgreSQL, SQLite), with a tested Docker matrix.
- **Maturity and users:** about 6 years old (repo created 2020-01-07), 427K downloads, MIT licensed and published. Plasm is unpublished and unlicensed.
- **Near-zero abstraction:** no model to declare; any record (including anonymous records and view records) maps by name. Ad-hoc reads need no setup beyond `table<T>`.
- **Seamless fallback to plain Dapper** via `Deconstructor` (`sql, Map<string,obj>`), with your own `IDbConnection` / `IDbTransaction` usable everywhere.
- **Plain ADO.NET transactions:** any connection or transaction, including one shared with other libraries, works without an adapter. Plasm deliberately hides the connection and transaction (only `TxContext.CreateCommand()`).
- **MSSQL `OUTPUT` / PG `RETURNING` on update and delete** (`UpdateOutputAsync`, `DeleteOutputAsync`). Plasm lists `RETURNING` from bulk updates as not built.
- **`distinct`**, a listed Plasm gap, and MSSQL query hints.
- **Lower per-query overhead** is likely (a thin reflection-based generator over Dapper). *Unverified, no benchmark run.*

## What Plasm does that Dapper.FSharp does not

- **Aggregates loaded whole** (`owns` / `ownsOptional` / `ownsOne` / `links`, at every level), with constant command count and no cartesian product. Dapper.FSharp has only flat join tuples, and you avoid N+1 by hand.
- **Unions and domain types as scalars** by convention (wrappers, `create` functions, fieldless unions, unions with fields across columns), with `Scalar.*` overrides. Dapper.FSharp has none (#100 open) and its CE reportedly bypasses user union type handlers (#88).
- **Result-valued errors** with classified database failures, versus exceptions.
- **Writes by difference**: constraint-to-field mapping, optimistic concurrency, owned rows and links written with their owner.
- **Transactions as values** (`transaction { }`, isolation, retry policy), test **sandbox** and FSI **playground**.
- **Bulk update, delete and upsert** (`ON CONFLICT`), computed assignments, and parameter-limit splitting. Dapper.FSharp has no PG upsert (#22 declined).
- **Richer typed predicates**: `IS NOT DISTINCT FROM` option equality, `.IsSome` / `.Value`, `exists (plasm { ... })` and `List.exists` / `forall` (`EXISTS` subqueries), string functions, and `Contains` as `= ANY`. Its aggregates are typed (Dapper.FSharp's take strings), and its result types follow the query (Dapper.FSharp's are a free type argument).
- **Prepared queries**, `Raw.fragment`, and a type-checked interpolated `repo.Sql`.
- **Schema check, scaffold and snake_case mapping** with per-field overrides (Dapper.FSharp: names must equal columns, #21 and #97 declined).
- **Diagnostics callback with timing, and cancellation to the server.** Dapper.FSharp has a log function (SQL plus parameters) and a `CancellationToken`, but no timing.

## Unverified items (summary)

- Why union type handlers are skipped in the CE (#88). I did not reproduce it.
- Dapper sending CLR enums as integers against a PG enum mapped via `NpgsqlDataSource.MapEnum` on current Dapper 2.1.72 / Npgsql 8+. This is based on Dapper#259 and older reports, not run.
- Whether Plasm's text `Scalar.enum` works against a native PG enum column. Not tested in the Plasm repo.
- Whether a left-joined record whose first column is NULL is wrongly mapped to `None` (inferred from `splitOn` = first field).
- Whether large multi-row inserts are split for parameter limits (no splitting seen in source).
- FSharp.Core 10 dependency effect on older hosts; relative per-database quality; performance relative to Plasm.
