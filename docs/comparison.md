# Plasm and the F# data-access libraries

Plasm's goal is to be an EF Core alternative that fits F#: plain immutable
records, options, unions and Results, with relationship loading as easy as
EF Core's `AsSplitQuery`. This page compares it with the libraries F# teams use
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

Plasm's place is the intersection: an ORM's relationship loading, with F#
types and semantics throughout.

## What Plasm does that none of the others do

- **Relationships with plain F# types.** Records without attributes or
  `CLIMutable`, F# lists, options, single-case wrappers; the mapping lives in a
  schema value. Of the others, only EF Core loads relationships, and not into
  these types.
- **Split loading as the only mode.** `preload` loads each relationship in one
  command per batch of parents (`= ANY(@keys)`), nested to any depth, sharing
  each related row's value across parents. There is no cartesian explosion to
  avoid and no `AsSplitQuery` to remember. Plasm's own benchmark reports it
  2.1× faster than EF Core's split query at 1,000 parents
  ([benchmarks.md](benchmarks.md)).
- **No hidden queries.** A relationship is `NotLoaded | Loaded`; reading one
  that was not loaded is an error, never a lazy query (SQLProvider's lazy
  navigation gives N+1; EF uses `null` for both "none" and "not loaded").
- **F# meaning in queries.** `= None` is `IS NULL`; `not` keeps F#'s meaning;
  `List.contains`, `.IsSome`, `.Value` and `Option.map` translate; anything that
  cannot run in the database is refused, never evaluated in memory. EF fails to
  translate several of these from F#.
- **Failures as values.** Writes return `Result`s with classified failures
  (unique violation naming the constraint, stale version, validation). Every
  other library throws.
- **Changesets.** Validation and permitted fields before anything is written;
  fields a changeset does not mention are never written (EF's `Update` on a
  detached record writes every column).
- **Transactions as values** of named steps, with failures that name the step.
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
3. **PostgreSQL enum types.** Plasm stores a fieldless union as text. Against a
   native `CREATE TYPE ... AS ENUM` column it fails on both providers (filters
   with 42883, inserts with 42804, `= ANY` with 42883), and the schema check
   rejects the column. Verified on Npgsql 10.0.3 and PGlite 0.5.16
   ([enums.md](comparisons/enums.md)).
4. **Modelling.** No embedded or owned types, JSON columns, composite keys,
   array columns, inheritance, or keyless and view entities.
5. **Querying.** No set operations or `distinct`, no nested collections
   projected into DTOs, no preloading on joined queries, no streaming, a fixed
   set of translated functions, and raw SQL that cannot be composed further.
6. **Writing.** Only `hasMany` children are written with their parent (no
   belongs-to or has-one writes, no cascades), and bulk updates have no
   `RETURNING`.
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

The recommended design for Plasm sidesteps the driver entirely: send the union
as text with a cast to the enum type (`@p::"order_status"`,
`ANY(@p::"order_status"[])`), and read the column as `::text`. It needs no
`MapEnum`, is unaffected by when the type was created, works on PGlite as well,
and still uses indexes.

```fsharp
Scalar.enum [ Pending, "pending"; Shipped, "shipped"; Cancelled, "cancelled" ]
|> Scalar.asPgEnum "order_status"
```

The schema check would verify the type exists and that its labels match the
union's cases in order, so ordering in SQL agrees with F#.

The research also found a defect in Plasm today: an ordering comparison
(`o.Status < Shipped`) or `orderBy` on a union stored with `Scalar.enum`
compares the stored text alphabetically, while F# compares union cases in
declaration order. Plasm already refuses ordering comparisons on options for
the same reason; it does not yet check unions.

## Gaps to specify next

Each becomes a feature file, written red first:

1. Ordering on a text-stored union follows F# (the defect above).
2. Fieldless unions stored as PostgreSQL enum types (`Scalar.asPgEnum`); a
   draft feature is in [enums.md](comparisons/enums.md).
3. Raw SQL: a typed parameter override for enum and `jsonb` values, and several
   result sets.
4. The larger EF Core gaps above, in the order the project chooses.
