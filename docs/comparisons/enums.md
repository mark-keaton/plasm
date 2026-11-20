---
name: plasm-pg-enums-research
status: complete
created: 2026-11-14T01:20:30Z
updated: 2026-11-14T01:20:30Z
---

# PostgreSQL enums for Plasm: research and recommendation

Labels used below:
- **[verified]**: run against Npgsql 10.0.3 (the version in `Directory.Packages.props`) on `postgres:17-alpine`, and/or PGlite 0.5.16 (the copy in `koans/browser/app/node_modules`) in Node. Prototype scripts are in `scratchpad/orm/proto/` (`enums.fsx`, `fresh.fsx`, `pglite.mjs`).
- **[docs]**: taken from official release notes or docs, with a citation.
- **[unverified]**: inferred or second-hand. Check it before relying on it.

## 0. Where Plasm is today

- `Scalar.enum` (src/Plasm/Scalar.fs) takes an explicit `(case * name)` table for a fieldless union. It sets `ProviderType = string`, so the column is expected to be text. Unknown stored text is a materialisation error.
- Parameters are untyped ADO.NET parameters (`p.Value <- v`, src/Plasm/Execution.fs `prepare`). A union therefore reaches Npgsql as a `string`, and Npgsql sends it as `text`. PGlite sends OID 25 (`Oid.ofType`, src/Plasm.PGlite/PGlite.fs).
- Membership lists become one array parameter, `= ANY(@pN)`, sent as `text[]`.
- Schema check (src/Plasm/SchemaCheck.fs) compares `information_schema.columns.data_type` against `dialect.ColumnTypes typeof<string>` = text / varchar / char / citext. A PG enum column reports `data_type = 'USER-DEFINED'` (with `udt_schema`/`udt_name` holding the type) [verified]. It would be reported as incompatible.
- Net effect: **a `Scalar.enum` field over a `CREATE TYPE ... AS ENUM` column fails today** on every write and every filter, on both providers (see §2 errors). Reads would work on a data source that loaded the type.
- **Probable existing semantic bug, independent of PG enums** [unverified, inferred from code]: `Translate.fs` (around line 514) translates `<`, `<=`, `>`, `>=` on any scalar to SQL with no check on its mapping. F# compares fieldless unions by **case declaration order**. A `Scalar.enum` text column compares **alphabetically**. So `o.Status < Shipped` (and `sortBy o.Status`) gives different answers in SQL and in F#. That breaks the "Queries keep their F# meaning" feature. A test should confirm it. Then either reject ordering on text enums or make it follow declaration order (`array_position(ARRAY[...], col)`, or a real PG enum, below).

## 1. What changed in Npgsql for enums (7 → 10) and EF Core (8 → 9)

### Npgsql 7.0 (Nov 2022): data sources replace global/connection mapping [docs]
- `NpgsqlDataSource` / `NpgsqlDataSourceBuilder` were introduced and became "the recommended way to manage type mappings". Code changed from `NpgsqlConnection.GlobalTypeMapper.MapEnum<Mood>()` to `dataSourceBuilder.MapEnum<Mood>()`. ([7.0 release notes](https://www.npgsql.org/doc/release-notes/7.0.html))
- **Breaking:** connection-level mapping (`conn.TypeMapper.MapEnum`) was removed.
- **Breaking:** "global type mappings must be done before the data source or pool is created". A `MapEnum` call after first use is silently not applied to existing pools.
- `GlobalTypeMapper` was marked obsolete ("we do not plan on removing global type mappings any time soon"). In 10.0.3 it still compiles with warning FS0044 "Global-level type mapping has been replaced with data source mapping, see the 7.0 release notes" [verified].

### Npgsql 8.0 (Nov 2023): slim builder and opt-ins [docs]
- `NpgsqlSlimDataSourceBuilder` (trimming/NativeAOT) arrived with opt-ins. The breaking-change table maps "Unmapped enums, ranges, multiranges" to `EnableUnmappedTypes()`. "Existing code using the above features will start throwing exceptions after upgrading". ([8.0 release notes](https://www.npgsql.org/doc/release-notes/8.0.html))
- The type-handler system was rewritten (resolvers / `PgTypeInfo`). Error text changed to "Reading as 'X' is not supported for fields having DataTypeName ...".
- Observation, Npgsql 10.0.3 [verified]: the slim builder **without** `EnableUnmappedTypes()` still read an unmapped enum as `string` and accepted `DataTypeName = "order_status"` parameters. The 8.0 opt-in may have narrowed since, or may apply to other paths (for example `GetValue` on arrays). Don't depend on either behaviour.
- SqlHydra's README once carried a note that Npgsql 8.0.0 failed when inserting an enum [unverified, second-hand via search snippet; the current README no longer has it].

### Npgsql 9.0 (Nov 2024)
- Nothing enum-specific in the [9.0 release notes](https://www.npgsql.org/doc/release-notes/9.0.html) [docs]. They drop .NET Framework / netstandard2.0, and some connection-string type options became builder APIs.

### Npgsql 10.0 (Nov 2025) [docs]
- "Starting with Npgsql 10, setting DataTypeName takes precedence over NpgsqlDbType". This matters if you type enum parameters with `DataTypeName`.
- `date`/`time` now map to `DateOnly`/`TimeOnly` and `cidr` to `IPNetwork`. A new `IDbTypeResolver` remaps `DbType`. ([10.0 release notes](https://www.npgsql.org/doc/release-notes/10.0.html))
- Enum mapping is unchanged from 7/8. `MapEnum<TEnum>` where `TEnum : struct, Enum`, plus a non-generic `MapEnum(Type, pgName, nameTranslator)`.

### Name translation [docs]
- `MapEnum` translates CLR member names with `NpgsqlSnakeCaseNameTranslator` by default (`InProgress` → `in_progress`). `[PgName("...")]` overrides a member, and a translator can be passed per mapping. ([enums and composites](https://www.npgsql.org/doc/types/enums_and_composites.html))
- An enum outside `public` must be mapped with a schema-qualified name. Unmapped enums can be written as a string with `DataTypeName = "mood"` and read with `GetFieldValue<string>`.

### Concrete behaviour on Npgsql 10.0.3 (default `NpgsqlDataSource.Create`, enum not mapped) [verified]

| What | Result |
|---|---|
| `WHERE status = @p` with a `string` | **42883 operator does not exist: order_status = text** |
| `INSERT ... VALUES (@p)` with a `string` | **42804 column "status" is of type order_status but expression is of type text** |
| `WHERE status = @p::order_status` | works |
| `WHERE status = ANY(@p)` with `string[]` | **42883** |
| `WHERE status = ANY(@p::order_status[])` with `string[]` | works |
| `INSERT ... (@p::order_status)` / `unnest(@p::order_status[])` | works |
| Parameter `DataTypeName = "order_status"` (scalar) / `"order_status[]"` (array) | works, **if** the type was loaded by the data source |
| Parameter `NpgsqlDbType.Unknown` | works for scalars; arrays (`Array ||| Unknown`) throw `InvalidCastException` |
| Bad label `'lost'::order_status` | **22P02 invalid input value for enum order_status** (raised at bind/plan time, even under `EXPLAIN`) |
| `GetValue` / `GetFieldValue<string>` on an enum column | returns `string` ("shipped"); enum arrays come back as `string[]` |
| Same, but `CREATE TYPE` ran **after** the data source opened its first connection | **InvalidCastException: Reading as 'System.String' is not supported for fields having DataTypeName '-.-'**. Parameters with `DataTypeName` fail with "data type name 'order_status' could not be found in the types that were loaded by Npgsql". Fixed by `conn.ReloadTypes()` or a new data source. `status::text` always works |
| `MapEnum<ClrStatus>("order_status")` | CLR enum parameters/arrays work; `GetValue` returns `ClrStatus`. **A `string` parameter still fails with 42883**. `GetFieldValue<string>` still works |
| `MapEnum(typeof<FsUnion>, "order_status")` (F# fieldless union) | **ArgumentException: Type must be a concrete Enum** |
| `ALTER TYPE ... ADD VALUE 'x'` then use `'x'` in the same transaction | **55P04 unsafe use of new value** |
| Index use | `status = $1::order_status` and `= ANY($1::order_status[])` use the index. `status::text = $1` filters after an index scan (the cast is on the wrong side) |
| Order | `ORDER BY status` gives declaration order; `ORDER BY status::text` gives alphabetical. `min`/`max` follow declaration order |

The "type created after first connection" row is the trap for Plasm users. It hits tests and sandboxes that run migrations on a warmed-up data source, and apps that run migrations at start-up through the same data source.

### PGlite 0.5.16 (through the repo's text bridge) [verified]
- Same errors as Npgsql for `text` (OID 25) parameters: 42883 on `=`/`ANY`, 42804 on insert.
- `$1::order_status` and `ANY($1::order_status[])` with text/`text[]` parameters work.
- OID 0 (let the server infer) works for scalars. For arrays it fails, with 22P02 "malformed array literal: "shipped,pending"", because the JS serializer flattens the array.
- Results come back with the enum's runtime OID (16385, array 16384). The .NET side's `Values.ofText` falls through to `box s`, so scalars read as `string`, which is fine. An enum **array** column would come back as raw `{a,b}` text. Plasm never reads one: a list of a type stored as an enum is refused when the model is built, by name (array-columns.feature), though a list of the same union stored as text is a `text[]` column.

### EF Core provider (Npgsql.EntityFrameworkCore.PostgreSQL) [docs]
- **Before 9.0** you needed three steps: `HasPostgresEnum<Mood>()` in `OnModelCreating` (creates the type in migrations), plus `NpgsqlDataSourceBuilder.MapEnum<Mood>()` or `GlobalTypeMapper.MapEnum` at the ADO level, plus a static constructor ordering gotcha with several contexts. ([EF enum docs](https://www.npgsql.org/efcore/mapping/enum.html))
- **9.0**: `UseNpgsql(cs, o => o.MapEnum<Mood>("mood"))` configures EF and an internally built data source in one place, and adds the enum to the model. `ConfigureDataSource(...)` exposes the builder. **Breaking:** "Enum mappings must now be configured at the EF level". If you pass your own `NpgsqlDataSource`, you need **both** `dataSourceBuilder.MapEnum<Mood>()` and `o.MapEnum<Mood>("mood")`. ([EF 9.0 release notes](https://www.npgsql.org/efcore/release-notes/9.0.html))
- Breakages users hit:
  - [efcore.pg #3390](https://github.com/npgsql/efcore.pg/issues/3390): after moving to 9.0, migrations re-created enums with labels in **alphabetical** order. The maintainer called it a one-time transition and said "enum label ordering is irrelevant", which was disputed because PG enums are ordered. Follow-ups are #3398 (default to declaration order) and #3424 ("enum label already exists" on `ADD VALUE`). Workaround: an empty migration.
  - [efcore.pg #3675](https://github.com/npgsql/efcore.pg/issues/3675): seeding in a migration that adds a new enum type fails (9.0.4) [unverified detail, from a search snippet]. This is consistent with the type-loading and 55P04 rows above.
  - Reports (SO, EF 10) of keeping only the data-source `MapEnum` and dropping the EF mapping, so the column is created as `integer` and inserts fail with 42804 [unverified, mirror of a Stack Overflow question].
  - Scaffolding emits `HasPostgresEnum` but skips enum columns, because there is no CLR type [docs].

## 2. Options for Plasm

### (a) Map through Npgsql `MapEnum`
- **Does not work for F# unions.** The generic constraint is `struct, Enum`, and the non-generic overload rejects them at run time [verified]. Plasm would need a parallel CLR enum per union (generated or hand-written) and would convert union → CLR enum in `ToProvider`. The user still has to call `MapEnum` on their data-source builder, with a name translator or `[PgName]` matching Plasm's table. That is two sources of truth for names, and exactly the setup churn users complain about.
- Parameters and `ANY` arrays work (typed). Reads return the CLR enum, which Plasm would convert back.
- PGlite has no `MapEnum`. It would need its own path anyway.
- It ties Plasm to whatever Npgsql's mapping API does next (7.0 and 8.0 both broke it).
- Verdict: **reject**.

### (b) Text on the wire, casts in the SQL (recommended)
Plasm already owns the SQL and the explicit name table, so it can render:
- Parameters compared with or assigned to the column: `@p0::"order_status"` (schema-qualified and quoted: `"sales"."tier"`). Lists: `= ANY(@p0::"order_status"[])`. Bulk/unnest: `unnest(@p0::"order_status"[])`.
- Reads: `SELECT t."status"::text`, including in `RETURNING`.
- With these two rules, **no Npgsql configuration is needed at all**: no `MapEnum`, `GlobalTypeMapper`, `EnableUnmappedTypes` or `ReloadTypes`. The type-loaded-before-CREATE-TYPE trap disappears, because Npgsql only ever sees `text`.
- Identical SQL works on PGlite (OID 25 + cast) [verified both].
- Comparisons and ordering: `status < @p::order_status` and `ORDER BY status` use **declaration order**. That is the same order as F# union comparison, provided the PG labels are declared in case order. The schema check can verify this from `pg_enum.enumsortorder`.
- The index is used because the cast is on the parameter side [verified].
- Errors: a label the database lacks gives 22P02, which `Classify` would currently report as `OtherDatabaseError` [inferred]. The schema check should catch it before run time.
- Cost: Plasm must know, wherever it emits a parameter, which column mapping it came from. Today `Sql.Expr.Param of value: obj` carries no type, so it needs a typed parameter node (below). Raw SQL holes (`{status}`) can get the cast automatically, because the hole's domain type is known.

### (c) Typed parameters (`NpgsqlParameter.DataTypeName = "order_status"`)
- Works on Npgsql for scalars and arrays without SQL casts, but **only if the data source has loaded the type**. It fails after a later `CREATE TYPE` [verified]. Since Npgsql 10 it overrides `NpgsqlDbType`.
- Needs provider-specific parameter creation (Plasm uses generic `DbParameter`). PGlite would need the enum's OID, which requires a catalogue lookup.
- Verdict: possible as an optimisation, but strictly worse than (b).

### (d) Other options
- **`NpgsqlDbType.Unknown`**: scalars only; arrays fail [verified]. Reject.
- **`CREATE CAST (text AS order_status) WITH INOUT AS ASSIGNMENT`**: fixes INSERT/UPDATE, but `status = $1` still fails with 42883 [verified]. It also changes the user's schema. Reject.
- **Compare as text (`status::text = $1`)**: defeats the index and gives alphabetical ordering. Reject.
- **Don't use PG enums.** Use `text` plus a `CHECK (status IN (...))` constraint, or a domain `CREATE DOMAIN order_status AS text CHECK (...)`. Domain columns report `data_type = 'text'`, and text parameters work [verified]. This already works with `Scalar.enum` unchanged. Document it as the zero-configuration alternative, while noting that ordering is then alphabetical (see the §0 bug).

## 3. Other F# / .NET libraries

- **EF Core**: CLR enums only, via `MapEnum` (see above). F# unions need a value converter to string with a text column. Pairing a converter with a PG enum column type is [unverified]; it likely hits the same text-vs-enum 42804 unless EF types the parameter.
- **SqlHydra**: generates PG enums as **CLR enums**. Its current README says that when enums exist (with `provider_db_type_attributes`), the generated code includes `Enums.register`, which `QueryContextFactory.Create` applies. "If you build your own NpgsqlDataSource, pipe the builder through `Enums.register` before Build()". So it relies on Npgsql `MapEnum`, and F# unions are not used. ([SqlHydra README](https://github.com/JordanMarr/SqlHydra))
- **Dapper.FSharp**: the README says nothing about enums or unions, only option-type handlers. ([README](https://github.com/Dzoukr/Dapper.FSharp)) Dapper itself converts CLR enums to integers before consulting type handlers, a long-standing issue. The common workaround is `::type` casts in SQL, or setting `DataTypeName` (for example the StrEnum.Npgsql package) [docs/second-hand].
- None of them maps an F# union to a PG enum without a CLR enum shim. Plasm doing so with zero driver setup would be a real differentiator.

## 4. Recommendation

### API
Keep one explicit table, and add the store type as an option on the same mapping:

```fsharp
type OrderStatus = Pending | Shipped | Cancelled

// Text column (today's behaviour, unchanged)
Scalar.enum [ Pending, "pending"; Shipped, "shipped"; Cancelled, "cancelled" ]

// PostgreSQL enum column: same table, stored as the named enum type
Scalar.enum [ Pending, "pending"; Shipped, "shipped"; Cancelled, "cancelled" ]
|> Scalar.asPgEnum "order_status"          // or "sales.order_status"

// Convenience when the stored names follow a rule (optional, still explicit at call site)
Scalar.enumBy Scalar.Names.snakeCase<OrderStatus>
|> Scalar.asPgEnum "order_status"
```

Implementation sketch:
- `ScalarMapping` gains `StoreType: StoreType option`, where `StoreType = { Schema: string option; Name: string }`.
- `Sql.Expr` gains `TypedParam of value: obj * storeType` (and `TypedArrayParam`). The dialect renders it, through a new `ISqlDialect.CastParameter: name * storeType * isArray -> string`, as `@p0::"schema"."name"` / `[]`.
- The translator emits typed params wherever a value came through that mapping: `=`/`<>`/ordering comparisons, `List.contains`/`ANY`, insert/update/upsert values, bulk `unnest`, prepared-query bound args, raw SQL holes.
- Column reads of such fields render `col::text` (select list and `RETURNING`).
- PGlite needs no bridge change. Scalar reads already fall back to string.
- `ColumnTypes` and the schema check: for a mapping with a `StoreType`, expect `data_type = 'USER-DEFINED'` and the matching `udt_schema.udt_name`. Read `pg_enum` labels ordered by `enumsortorder`.

### Guarantees
1. No Npgsql type-mapping configuration is required or consulted. It works on a data source that loaded types before the enum was created, on the slim builder, and on PGlite. If a user *also* calls `MapEnum` for a CLR enum on that type, Plasm keeps working (reads use `::text`).
2. Filters, membership lists, inserts, updates, upserts, bulk inserts, prepared queries and raw-SQL holes all accept the union. No text-vs-enum errors (42883/42804) can come from Plasm-generated SQL.
3. Ordering comparisons and `sortBy` on a PG-enum field agree with F# `compare` on the union. The schema check rejects a type whose label order differs from case order. Ordering on a **text** `Scalar.enum` is either rejected with a translation error or rendered in declaration order. Pick one, and fix the §0 bug.
4. The schema check reports a missing type, a column of a different type, labels the model lacks (reads would fail), cases the type lacks (writes would fail), and label-order mismatch.
5. A stored label outside the table is a materialisation error naming the type, as today.
6. Filters on enum columns can use an index (casts on the parameter side only).

### Gherkin (style of features/*.feature)

```gherkin
# Executable acceptance specification (Reqnroll): PostgreSQL enum types.
# Written before the implementation, following the conventions of core.feature.
Feature: Fieldless unions stored as PostgreSQL enum types
  As an F# application developer
  I want to map a fieldless union to a PostgreSQL enum type
  So that I keep the database's enum without configuring the driver for it

  Plasm sends enum values as text with a cast to the named type and reads them
  as text, so no Npgsql type mapping (MapEnum, the global type mapper,
  unmapped-type options or type reloading) is needed, on either provider.

  Background:
    Given a configured relational database
    And an order_status enum type with labels pending, shipped and cancelled, in that order
    And mappings for Orders whose Status is stored as the order_status enum type

  Rule: Values round-trip without driver configuration

    Scenario: Inserting and reading an Order keeps its status
      Given a data source with no type mappings
      When I insert an Order with status Shipped
      And I read the Order back
      Then its status is Shipped

    Scenario: The enum type is created after the data source has connected
      Given a data source that opened a connection before the enum type existed
      When I insert and read back an Order with status Pending
      Then its status is Pending

    Scenario: Updating and upserting a status
      Given an Order with status Pending
      When I update only the Order status to Cancelled
      And I upsert the same Order with status Shipped
      Then the stored status is Shipped

    Scenario: Bulk inserts accept enum statuses
      When I insert 1,000 Orders with mixed statuses in one bulk write
      Then every Order reads back with the status it was given

  Rule: Queries keep their F# meaning

    Scenario: Filtering on a status
      Given Orders with each status
      When I keep Orders whose status is Shipped
      Then only the Shipped Orders are returned

    Scenario Outline: Filtering on a captured list of statuses
      Given Orders with each status
      When I keep Orders whose status is in a captured <container> of Pending and Cancelled
      Then the result equals evaluating the predicate over the same Orders in F#

      Examples:
        | container |
        | list      |
        | array     |
        | set       |

    Scenario: Ordering follows the union's case order
      Given Orders with each status
      When I sort Orders by status
      Then the order is Pending, Shipped, Cancelled
      And it equals sorting the same Orders in F#

    Scenario: Ordering comparisons follow the union's case order
      Given Orders with each status
      When I keep Orders whose status is greater than Pending
      Then the result equals evaluating the predicate over the same Orders in F#

    Scenario: A prepared query binds a status argument
      When I prepare a query with a status argument and run it with Cancelled
      Then only the Cancelled Orders are returned

    Scenario: A raw SQL hole accepts a status
      When I run raw SQL that filters on a status hole with Shipped
      Then only the Shipped Orders are returned

    Scenario: A filter on the status can use an index
      When I explain a query that filters on a status
      Then the status column is compared without a cast on the column

  Rule: The schema check finds mismatches before a request fails

    Scenario: A matching enum type passes
      When I check the model against the database
      Then no problems are reported

    Scenario: A missing enum type is reported
      Given the order_status type does not exist
      When I check the model against the database
      Then the missing type is reported with its model and field

    Scenario: A label the model does not know is reported
      Given the order_status type also has the label returned
      When I check the model against the database
      Then the unknown label is reported because reading it would fail

    Scenario: A case the type does not have is reported
      Given the order_status type lacks the label cancelled
      When I check the model against the database
      Then the missing label is reported because writing Cancelled would fail

    Scenario: Labels declared in a different order are reported
      Given the order_status type declares cancelled before shipped
      When I check the model against the database
      Then the order mismatch is reported because sorting would differ from F#

    Scenario: A text column mapped as an enum type is reported
      Given the status column is text
      When I check the model against the database
      Then the incompatible type is reported with the expected and actual types

  Rule: Stored values outside the table are errors

    Scenario: An unknown stored label is a materialisation error
      Given an Order whose stored status is a label the model does not list
      When I read the Order
      Then the read fails with a materialisation error naming OrderStatus and the label

  Rule: Mapping errors are found when the model is built

    Scenario: An invalid enum type name is rejected
      When I map Status to the enum type "order status; drop table"
      Then building the model fails with a configuration error naming the field
```

Also add to `query-semantics.feature` (independent of PG enums; exposes the §0 bug):

```gherkin
    Scenario: Ordering a text-stored union follows F# comparison
      Given Orders with each status stored as text
      When I sort Orders by status and keep Orders whose status is greater than Pending
      Then both results equal evaluating the same query over the Orders in F#
```

### Open questions for the user
- For the text `Scalar.enum`: reject ordering comparisons, or translate them to declaration order (`array_position(ARRAY['pending',...], col)`, which can't use a plain index)?
- Should `asPgEnum` default the schema to `current_schema()` (matching the schema check, which uses `current_schema()`), or require it?
- Plasm doesn't migrate. Should the docs show the `CREATE TYPE` (and the `ADD VALUE` outside-a-transaction rule, 55P04) for users' migration tools?
