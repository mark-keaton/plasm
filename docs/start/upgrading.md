# Upgrading from 0.1.0

What changed after 0.1.0 that can break an application written against it,
the most serious first. [`docs/status.md`](../status.md) has every entry in
full, under "Changed".

## Times are stored only in UTC

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

### What 0.1.0 stored

0.1.0 did not refuse these values. It stored them wrongly:

- In a column, a `DateTime` of Kind Unspecified or Local was stored shifted
  by the database server's time zone: 08:00 was stored as 07:00Z on a server
  in Paris.
- Inside a `[<Json>]` document, such a `DateTime` was read as the local time
  of the machine that wrote it and stored shifted by that machine's zone, and
  a `DateTimeOffset` kept its offset.
- A `DateTimeOffset` in a column with an offset other than zero was not
  stored: the write threw an exception.

### Rows already written

Nothing rewrites stored data. If an application on 0.1.0 wrote times that
were not UTC, those rows are still off, a column's by the server zone's
offset at that instant. `SHOW timezone` says the server's zone, and
`now() - now() AT TIME ZONE 'UTC'` its offset now. Find the rows by when and
where they were written, and correct them by that offset.

Read back, a document's `DateTime` is now of Kind Utc and its
`DateTimeOffset` at the offset zero, as a column's are.

## Fewer exceptions

Nothing a provider throws while sending a value leaves `Repo`, `Transact`,
`Stream` or a prepared query as an exception. A value the driver cannot send
is `DbError.Database`, and a value in hand-written SQL of a type Plasm does
not map is `DbError.Translation`; both were exceptions in 0.1.0. Code that
caught those exceptions now reads the failure value.

## A dialect or provider of your own

This part matters only to an application that implements `ISqlDialect` or
`IProvider` itself, or builds a `ProviderCapabilities` record. One that uses
`Plasm.Postgres` as it comes has nothing to do.

Every piece of database-specific SQL now sits behind the dialect, so the
interfaces grew, and a dialect written against 0.1.0 no longer compiles
until it gives the new members:

- `ISqlDialect` gained members for lists (`ListValue`, `InList`, `NotInList`,
  `ListRows`, `RowsIn`, `ListHas`, `ListIsEmpty`, `EmptyArray`), for casts
  (`StoreCast`, `TextCast`), for a limit before an offset (`UnboundedLimit`),
  for exact decimals (`Exact`), for what the database cannot hold
  (`Refusal`), and two groups of their own, `Ddl` and `Catalogue`.
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
`CatalogueTypes` is the whole list; `WritersTakeTurns` is false. Aggregate
and window function names now pass through `FunctionName` as other
functions' do, so a dialect that renames functions sees `COUNT`, `SUM` and
the rest.

The entries under "Changed" in [`docs/status.md`](../status.md) say what
each member is asked and what PostgreSQL answers.
