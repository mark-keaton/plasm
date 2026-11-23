# Upgrading

What changed in each version that can break an application written against
the one before, the newest first and, within a version, the most serious
first. [`docs/status.md`](../status.md) has every entry in full, under
"Changed".

## From 0.3.0 to the next version

An application has two things to look at: a field's name, where it reads or
builds one of four error types, and the text of four messages, where it
matches on it. Two things more change in the template, and in
`examples/Starter`, which the template is made from.

### The field naming a problem's record type is Record

On `ConfigError`, `MaterializationError`, `SchemaIssue` and `ModelReference`
the field that names the record type a problem is in is `Record`, where it
was `Model`. Code that reads the field or builds one of the four no longer
compiles, and the compiler names each place: write `Record` for `Model`. The
types keep their names, `ModelReference` among them, and no message changes.

### Four error messages say "record type" where they said "schema"

"Schema" now means only the database's tables. Code that matches on the old
text must match on the new:

- `TranslationError.UnmappedType`: "Type T is not a record type this model
  stores". It was "Type T has no schema in this model".
- `Model.build`: "relationship target T is not a record type this model
  stores". It was "relationship target T has no schema in this model".
- `Model.build`: "record type registered more than once". It was "schema
  registered more than once".
- `Model.build`, for a key type that two record types share: "a typed id must
  be the key of one record type" and "Give each record type its own key
  type". It said "the key of one schema" and "Give each schema its own key
  type".

The type `SchemaDef` is no longer public. No public member took or returned
it, so code that compiled still compiles.

### The template: the outcomes come from Plasm.CSharp

This version adds a package, `Plasm.CSharp`, that holds what an F# data layer
hands a C# host: the cases `Missing`, `Refused`, `Stale` and `Invalid`, the
unions `Found<'T>`, `Added<'T>`, `Answered<'T>` and `Changed<'T>`, and
`UnavailableException`. [From C#](../csharp.md) describes them.
The template's `Starter.Data` references it, at the version of
`Plasm.Postgres`, and `BookingData` no longer declares its own.

A project made from 0.3.0's template keeps working as it is, since its
outcome types are its own. One that follows the template changes in three
places that its clients or its C# can see:

- A stale change's JSON is `{"what":"Equipment","key":["<id>"]}`, where it
  was `{"equipment":"<id>"}`. Any client that reads the body of a 409 reads
  the new shape.
- A change can be `Invalid`, so the switch over a change in `Program.cs`
  gains an arm, `Invalid invalid => Results.BadRequest(invalid)`. A change
  the database refuses field by field is then a 400 with its fields, where
  it was a 500.
- `BookingUnavailableException` is `UnavailableException`, the package's.

### The template: BookingData opens from the connection string

`BookingData.OpenAsync` takes the connection string where it took an
`NpgsqlDataSource`: `BookingData.OpenAsync(dataSource, time, ct)` is now
`BookingData.OpenAsync(connectionString, time, ct)`, and the overload that
takes a function for the SQL changed the same way. The data source is built
in a new file, `src/Starter.Data/Store.fs`, in `dataSource`, on
`NpgsqlDataSourceBuilder`. That is the place to configure the connection: a
password provider, SSL, a pooler's settings, logging.

A project made from 0.3.0's template keeps working as it is, since its code
is its own. To follow the template, add `Store.fs` to `Starter.Data` before
`BookingData.fs`, have `BookingData` start its repository from
`Store.provider connectionString`, pass the connection string in
`Program.cs`, and remove the line there that built the `NpgsqlDataSource`.
Nothing else in a project on PostgreSQL changes.

## From 0.2.0 to 0.3.0

0.3.0 adds a second provider, SQLite, as the package `Plasm.Sqlite`:
[SQLite](../sqlite.md) says how to use it. Nothing changes for PostgreSQL
beyond what follows.

An application that uses `Plasm.Postgres` as it comes has two things to look
at; the rest is for a dialect or provider of your own.

### A transaction at a level the database cannot run at

`Repo.Transact` at the isolation level Chaos is now refused before anything
is sent, as `TxFailure.Failed` holding a `DbError.Translation` that names
the level. In 0.2.0 it raised the driver's exception. Code that caught that
exception now reads the failure value.

### DbFailure has two fields more

`DbFailure` gained `Table` and `Columns`: the table a violated constraint is
on, and the columns a violated unique constraint or foreign key holds, where
the database gives them. PostgreSQL gives the table and the constraint's
name, and `Columns` is empty. Code that reads a `DbFailure` is unchanged.
Code that builds one, a test double or a provider's `Classify`, adds
`Table = None; Columns = []`.

### A dialect or provider of your own

The interfaces grew again, so a dialect or provider written against 0.2.0
gives the new members before it compiles:

- `ISqlDialect.Division(write, op, dividend, divisor)`: how a division or
  remainder of numbers is written, so that a zero divisor fails the
  statement.
- `ISqlDialect.Cycle`, a `CycleCheck`: how a recursive query leaves out the
  rows that close a cycle.
- `ICatalogueDialect.Checks`: SQL giving each table and the text of its
  named check constraints, which the schema check reads.
- `ProviderCapabilities.IsolationLevels`: the levels a transaction can run
  at.

The answers that keep what 0.2.0 did on PostgreSQL: `Division` writes
`dividend (); write $" {op} "; divisor ()`; `Cycle` is
`CycleCheck.Clause (fun key flag path -> "CYCLE " + String.concat ", " key + " SET " + flag + " USING " + path)`;
`Checks` gives no rows, `SELECT NULL::text, NULL::text WHERE false`; and
`IsolationLevels` lists the levels the database runs. A dialect, catalogue
or provider that wraps another passes each on, and a capabilities record
copied from another's keeps its levels.

## From 0.1.0 to 0.2.0

### Times are stored only in UTC

Plasm now refuses a time that is not an instant in UTC, before anything is
sent: a `DateTime` whose Kind is not Utc, or a `DateTimeOffset` whose offset
is not zero. This holds for a time in a column and for one anywhere inside a
`[<Json>]` document. A write is refused as `TxFailure.InvalidChanges` naming
the field; a query, a stream, a bulk write, a prepared query's argument or a
value in hand-written SQL is refused as `DbError.Translation`.

So `DateTime.Now`, `DateTime.Parse` without
`DateTimeStyles.AdjustToUniversal`, and `DateTime(y, m, d)` are refused
where 0.1.0 took them. Give a time Kind Utc with `DateTime.UtcNow` or
`DateTime.SpecifyKind`, or convert a local one with `ToUniversalTime()`; give
a `DateTimeOffset` the offset zero the same way.

#### What 0.1.0 stored

0.1.0 did not refuse these values. It stored them wrongly:

- In a column, a `DateTime` of Kind Unspecified or Local was stored shifted
  by the database server's time zone: 08:00 was stored as 07:00Z on a server
  in Paris.
- Inside a `[<Json>]` document, such a `DateTime` was read as the local time
  of the machine that wrote it and stored shifted by that machine's zone, and
  a `DateTimeOffset` kept its offset.
- A `DateTimeOffset` in a column with an offset other than zero was not
  stored: the write threw an exception.

#### Rows already written

Nothing rewrites stored data. If an application on 0.1.0 wrote times that
were not UTC, those rows are still off, a column's by the server zone's
offset at that instant. `SHOW timezone` says the server's zone, and
`now() - now() AT TIME ZONE 'UTC'` its offset now. Find the rows by when and
where they were written, and correct them by that offset.

Read back, a document's `DateTime` is now of Kind Utc and its
`DateTimeOffset` at the offset zero, as a column's are.

### Fewer exceptions

Nothing a provider throws while sending a value leaves `Repo`, `Transact`,
`Stream` or a prepared query as an exception. A value the driver cannot send
is `DbError.Database`, and a value in hand-written SQL of a type Plasm does
not map is `DbError.Translation`; both were exceptions in 0.1.0. Code that
caught those exceptions now reads the failure value.

### A dialect or provider of your own

This part matters only to an application that implements `ISqlDialect` or
`IProvider` itself, or builds a `ProviderCapabilities` record. One that uses
`Plasm.Postgres` as it comes has nothing to do.

These interfaces are still changing while a second provider is built, so a
dialect or provider of your own should expect further changes in the next
versions. Application code that uses Plasm with `Plasm.Postgres` is not
affected by those.

Every piece of database-specific SQL now sits behind the dialect, so the
interfaces grew, and a dialect written against 0.1.0 no longer compiles
until it gives the new members:

- `ISqlDialect` gained members for lists (`ListValue`, `InList`, `NotInList`,
  `ListRows`, `RowsIn`, `ListHas`, `ListIsEmpty`, `EmptyArray`), for casts
  (`StoreCast`, `TextCast`), for a limit before an offset (`UnboundedLimit`),
  for exact decimals (`Exact`), for what the database cannot hold
  (`Refusal`), and two groups of their own, `Ddl` and `Catalogue`.
- In `Ddl`, a column's check is one member, `ColumnCheck(column, holds)`,
  told what the column holds as a `ColumnHolds`: a scalar, a list, a
  document or an enum with its labels.
- `ISqlDialect.LikeEscape` is gone. `TextMatch` writes `String.StartsWith`,
  `EndsWith` and `Contains` in a query: told which of the three, it writes
  the operand and its own pattern, matching the text with its case and every
  character as itself.
- `ISqlDialect.ColumnTypes` is now three members, one for each use:
  `ColumnType`, `CatalogueTypes` and `CastType`.
- `IJsonDialect` gained `StoreType`.
- `IProvider` gained `Describe`, asked only of a provider whose capabilities
  say it can describe a statement. A provider that wraps another passes it
  on.
- `ProviderCapabilities` gained `EnumTypes`, `ArrayColumns` and
  `WritersTakeTurns`. A provider that copies another's record keeps theirs.

The answers that keep what 0.1.0 did on PostgreSQL: `Refusal` answers `None`
for every value; `Exact` and `UnboundedLimit` are `None`; `ColumnType` and
`CastType` are the first name the old `ColumnTypes` list gave, and
`CatalogueTypes` is the whole list; `TextMatch` writes the operand, `LIKE`
and the text escaped (`\`, `%`, `_`), with `%` where the match is open, and
the old escape clause after; `ColumnCheck` answers `None` for every
column; `WritersTakeTurns` is false. Aggregate
and window function names now pass through `FunctionName` as other
functions' do, so a dialect that renames functions sees `COUNT`, `SUM` and
the rest.

The entries under "Changed" in [`docs/status.md`](../status.md) say what
each member is asked and what PostgreSQL answers.

### The template

A machine that installed the template at 0.1.0 keeps that one:
`dotnet new install Plasm.Templates` leaves it as it is. Run
`dotnet new uninstall Plasm.Templates` first, then install it again.
