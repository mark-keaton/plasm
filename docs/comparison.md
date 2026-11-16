# Plasm and the F# data-access libraries

Plasm's goal is to be an EF Core alternative that fits F#: plain immutable
records, options, unions and Results, with aggregates loaded as easily as
EF Core's `AsSplitQuery` loads a graph. This page compares it with the libraries F# teams use
today. The detailed research notes, with sources (issues, release notes, source
lines) and probes run against real databases, are in
[`comparisons/`](comparisons/):

| Library | What it is | Notes |
|---|---|---|
| EF Core from F# | the full ORM Plasm replaces | [efcore.md](comparisons/efcore.md) |
| SqlHydra | database-first generated types and typed query builders | [sqlhydra.md](comparisons/sqlhydra.md) |
| SQLProvider | a type provider reading the schema at compile time | [sqlprovider.md](comparisons/sqlprovider.md) |
| Dapper.FSharp | computation-expression SQL builders over Dapper | [dapper-fsharp.md](comparisons/dapper-fsharp.md) |
| Facil, Npgsql.FSharp, Donald | SQL-first: generated from SQL, or thin ADO.NET wrappers | [sql-first.md](comparisons/sql-first.md) |
| PostgreSQL enums | Npgsql's enum changes and a design for Plasm | [enums.md](comparisons/enums.md) |

Researched 2026-11-13, against EF Core 11.0.0-rc.3 with Npgsql's EF provider
11.0.0-rc.3, Npgsql 10.0.3, SqlHydra 5.1.1, SQLProvider 1.5.31,
Dapper.FSharp 4.10.0, Facil 3.3.0, Npgsql.FSharp 8.0.0 and Donald 10.1.0.
What this page says about Plasm was brought up to date on 2026-11-14, when
Plasm became one style (`docs/status.md`); the research notes under
`comparisons/` each say how they were corrected.

## The short version

Only EF Core and Plasm are ORMs: they know about relationships and load them.
The other libraries are query builders or SQL runners. Their authors say so
(SqlHydra: "not an ORM"; Dapper.FSharp declines relationships), and loading a
parent with its children is left to you: a second `IN` query and grouping in
memory, or flat join rows.

EF Core has the relationships but is orthogonal to F#. Probes against EF Core
11 found that plain immutable records fail to materialise, F# `list`
navigations fail, `Include(...).ThenInclude(...)` needs an upcast to compile,
`{ o with ... }` on a tracked entity throws, options need EFCore.FSharp
(maintained, but by volunteers short of time), captured values are inlined as
constants unless wrapped in `EF.Parameter`, and failures are exceptions.

Plasm's place is the intersection: an ORM's loading of whole aggregates, with
F# types and semantics throughout.

## What Plasm does that none of the others do

- **Aggregates with plain F# types.** The application's own immutable records,
  without `CLIMutable` or base classes: F# lists, options, single-case
  wrappers, types with a `create` function and unions with fields, stored by
  convention. The record names its table (`[<Table>]`) and marks what no type
  can say (`[<Unique>]`, `[<Version>]`, `[<OrderedBy>]`); what it owns and
  links follows from its field types. The attributes come from a package with
  no dependencies, which Fable compiles. Of the others, only EF Core loads
  related rows, and not into these types.
- **Split loading as the only mode.** What a record owns or links is loaded with
  it, one command per list per batch of owners (`= ANY(@keys)`), at every
  level. There is no cartesian explosion to avoid and no `AsSplitQuery` to
  remember. Other aggregates are referred to by id and read by a join,
  `exists`, or `including` (each record paired with a list, in the result's type). In Plasm's own benchmark a page of 1,000 orders
  with their lines and payments took 14.5 ms against EF Core's 21 ms (split
  query) and 66 ms (single query) ([benchmarks.md](benchmarks.md)).
- **No hidden queries.** Owned and linked fields are plain values, always
  loaded; nothing is lazy and nothing is left unloaded (SQLProvider's lazy
  navigation gives N+1; EF uses `null` for both "none" and "not loaded").
- **F# meaning in queries.** `= None` is `IS NULL`; `not` keeps F#'s meaning;
  `List.contains`, `.IsSome`, `.Value` and `Option.map` translate; anything that
  cannot run in the database is refused, never evaluated in memory. EF fails to
  translate several of these from F#.
- **Failures as values.** A transaction returns a `Result` whose failure is
  the domain's own rejection, a field error (a constraint with PostgreSQL's
  default name, mapped to its field), a concurrency conflict, or a classified
  database failure. Every other library throws.
- **Writes by difference.** `Tx.update (original, changed)` writes only the
  columns that differ, and what the record owns by key (EF's `Update` on a
  detached record writes every column). Plasm validates no input: the domain's
  constructors do.
- **Transactions as values** of typed steps, with failures that say what
  failed and the file and line of the step.
- **Sandboxes for tests** that always roll back, on the real database.
- **A schema check** against the live database at startup, instead of at
  compile time (SQLProvider, which needs a database in every build) or not at
  all (Dapper.FSharp, Npgsql.FSharp, Donald).
- **F# Interactive and the browser.** Queries show their SQL in FSI, and the
  PGlite provider runs the same code in a web page.

## Where Plasm is behind

In rough order of importance for teams leaving EF Core:

1. **One database.** PostgreSQL only. SqlHydra supports five, Dapper.FSharp
   four, SQLProvider about ten, EF Core many.
2. **No migrations.** Plasm checks the schema but does not change it. EF Core
   generates migrations; the others leave schema to other tools, as Plasm does.
3. ~~**PostgreSQL enum types.**~~ Done with `Scalar.asPgEnum` (below). Before
   it, a native `CREATE TYPE ... AS ENUM` column failed on both providers
   (filters with 42883, inserts with 42804, `= ANY` with 42883), verified on
   Npgsql 10.0.3 and PGlite 0.5.16 ([enums.md](comparisons/enums.md)).
4. **Modelling.** Owned records live in their own tables; a record with no
   table is stored in its owner's row (a value, like EF Core's complex types).
   No JSON columns, composite keys, array columns, inheritance, or keyless and
   view entities. A join table cannot carry columns of its own.
5. **Querying.** No set operations, no nested collections
   projected into DTOs, no streaming, a
   fixed set of translated functions, and raw SQL that cannot be composed
   further.
6. **Writing.** Only what an aggregate owns or links is written with it;
   another aggregate is written by its own transaction step, and there is no
   unit of work across arbitrary records. Bulk updates have no `RETURNING`.
7. **Operations.** No OpenTelemetry, interceptors or connection resiliency
   beyond a transaction retry policy.
8. **Maturity.** No releases or users yet.

## Enums: the "pain" in detail

Npgsql 7 moved enum mapping from the global type mapper to the data source
(`NpgsqlDataSourceBuilder.MapEnum`), and EF Core's provider followed in 9.0
(`MapEnum` inside `UseNpgsql`). The breakages users hit: enums mapped in one
place but not another (EF #3667), migrations recreating labels in alphabetical
order (EF #3390), Dapper ignoring enum handlers, and an enum type created after
the data source first connected being unreadable until `ReloadTypes()`.
`MapEnum` needs a CLR enum and rejects an F# union at run time, so every
library that supports PostgreSQL enums does it through CLR enums (SqlHydra
generates them and a `MapEnum` registration helper since 4.1). None maps an F#
union to a PostgreSQL enum.

Plasm now implements the recommended design ([pg-enums.feature](../features/pg-enums.feature)). It sidesteps the driver entirely: send the union
as text with a cast to the enum type (`@p::"order_status"`,
`ANY(@p::"order_status"[])`), and read the column as `::text`. It needs no
`MapEnum`, is unaffected by when the type was created, works on PGlite as well,
and still uses indexes.

```fsharp
Scalar.enum [ Pending, "pending"; Shipped, "shipped"; Cancelled, "cancelled" ]
|> Scalar.asPgEnum "order_status"
```

The schema check verifies that the type exists and that its labels match the
union's cases in order, so ordering in SQL agrees with F#. An unqualified type
name is looked up in `current_schema()`, as tables are.

Plasm does not create or change the type; a migration tool does:

```sql
CREATE TYPE order_status AS ENUM ('pending', 'shipped', 'cancelled');
-- A new label can be added later (PostgreSQL 12+ allows this inside a
-- transaction, but the new label cannot be used until that transaction commits):
ALTER TYPE order_status ADD VALUE 'returned' AFTER 'shipped';
```

Add the case to the union in the same position.

The research also found a defect, since fixed: an ordering comparison
(`o.Status < Shipped`), `orderBy` or `Agg.min`/`Agg.max` on a union stored with
`Scalar.enum` compared the stored text alphabetically, while F# compares union
cases in declaration order. Plasm now compares each value's position in the
declaration (`CASE "status" WHEN 'pending' THEN 0 ... END`), so the result is
F#'s; the cost is that an index on the text column does not serve that
ordering. A PostgreSQL enum type orders by declaration natively.

## Gaps to specify next

Each becomes a feature file, written red first:

1. ~~Ordering on a text-stored union follows F# (the defect above).~~ Done.
2. ~~Fieldless unions stored as PostgreSQL enum types (`Scalar.asPgEnum`).~~ Done.
3. Raw SQL: a typed parameter override for `jsonb` values (enum-typed holes are already cast), and several
   result sets.
4. The larger EF Core gaps above, in the order the project chooses.
