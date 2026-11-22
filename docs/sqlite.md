# SQLite

Plasm.Sqlite is Plasm's second provider, over Microsoft.Data.Sqlite. It is not
packaged yet (`IsPackable` false): what it still owes is in
`tests/Plasm.Providers/owed.txt`, and this page describes it as it stands. Every
feature file runs on it (`tests/Plasm.Providers/sqlite-run.sh`); the scenarios
that are PostgreSQL's alone, and why, are in `postgres-only.txt`.

## Connecting

```fsharp
open System
open Plasm.Sqlite

let provider = Sqlite.connect "Data Source=shop.db"

let tuned =
    Sqlite.connectWith
        { SqliteOptions.Default with BusyTimeout = TimeSpan.FromSeconds 5.0 }
        "Data Source=shop.db"
```

`SqliteOptions` has three settings:

- `BusyTimeout` (30 seconds): how long a writer waits for another's write lock
  before it fails as `DbFailureKind.Timeout`.
- `JournalMode` (`WalWhenCreated`): `Wal` sets WAL on any file database,
  `WalWhenCreated` only on a file the provider's first open creates, and `Keep`
  leaves the mode the database has.
- `MaxKeysPerCommand` (30,000): the keys a load sends in one command. SQLite
  allows 32,766 parameters, and a key is one each.

Each connection the provider opens has foreign keys on, read back to be sure,
and Plasm.Sqlite's decimal functions and collation registered. The provider
refuses a SQLite older than 3.44, the oldest that has what Plasm sends:
RETURNING (3.35), `IS NOT DISTINCT FROM` (3.39), and `string_agg` with an
ORDER BY inside the call (3.44).

## The caller's thread

Microsoft.Data.Sqlite runs each statement on the thread that calls it, and
hears no cancellation while it does. Plasm.Sqlite makes the token count: while a
statement is the caller's, a cancelled token interrupts it (`sqlite3_interrupt`)
and the call ends as `OperationCanceledException`, as a cancelled Npgsql
command does. A wait for the database's write lock is tried a second at a time,
hearing the token between tries, until `BusyTimeout` has passed. So a busy
timeout is kept to within about a second.

## Transactions

A transaction begins `BEGIN IMMEDIATE`, which takes the database's one write
lock. A second writer waits at its own BEGIN until the first ends, then writes,
or fails as a Timeout at its BEGIN once `BusyTimeout` has passed. Every
transaction is serializable. Transact takes `Unspecified`, `ReadUncommitted`,
`ReadCommitted`, `RepeatableRead` and `Serializable`, and runs each so; it
refuses `Snapshot` and `Chaos` as `UnsupportedByProvider`, with nothing sent.

Because a transaction holds the write lock from its BEGIN, nothing lands
between its read and its write. A copy read in an earlier transaction is still
stale once the row has moved on, which `Tx.oneAsSeen` reports as a Conflict,
as on PostgreSQL. No transaction meets a serialization failure or a deadlock,
so `RetryPolicy.OnSerializationFailure` runs nothing again. A busy wait past
`BusyTimeout` is a Timeout, which no policy retries: the wait is the one you
chose.

A read in one snapshot (`ReadConsistency.Snapshot`) begins with a deferred
`BEGIN`, which takes no lock. In WAL mode a writer goes on beside it.

Row locks (`locking`) are refused as unsupported. Writers take turns for the
whole database, so a locked read inside a write transaction would hold anyway;
honouring it there is a possible later change.

## Journal modes and their waits

In WAL mode, which a database the provider creates has, readers and the one
writer go on together. In a rollback-journal database, a reader waits for a
writer to commit and a commit waits for readers to finish. Each wait is a busy
wait, kept to `BusyTimeout` and ended by a cancelled token.

## How values are stored

SQLite has no decimal, time, array, document or enum type, so Plasm.Sqlite
stores each as text and holds the column to it with a check that Ddl writes:

- A decimal is text keeping its scale (`2.50`). It is compared, ordered, added
  and aggregated by value, through Plasm.Sqlite's functions
  (`plasm_decimal_compare`, `plasm_decimal_add`, `plasm_decimal_sum`, ...) and
  its collation. An index on the column does not serve a comparison by value.
  A model keyed by a decimal is refused.
- A time is UTC text to the microsecond, `yyyy-MM-dd HH:mm:ss.FFFFFF`, with
  `+00:00` for a DateTimeOffset, as PostgreSQL keeps it. A time written past
  Plasm in another form is a read error that says what to write.
- A list of scalars is a JSON array, read with `json_each`.
- A `[<Json>]` document is JSON text.
- A union stored as its cases' names is text, held to the names by a check.
  An enum type of the database's own (`Scalar.asPgEnum`) does not exist, and a
  model storing one is refused when the repository is made.

The schema check reads those checks back. One that refuses a case's name, or
allows a name no case has, or holds a column to something else, is reported.
So is a column without its check, where PostgreSQL's type would hold the value.

## Where SQLite answers differently without failing

Nowhere a query could tell. A division or remainder by zero, which SQLite
answers with NULL, fails on SQLite as on PostgreSQL: its dialect writes each
division and remainder so that a zero divisor fails the statement, in the
same words, "division by zero". It costs some 40% over SQLite's own division.

## What the statement check cannot tell

The statement check has SQLite prepare each statement. SQLite gives a parameter
no type, so a statement whose parameter PostgreSQL cannot type alone is
described, not noted. The schema check cannot tell a string field over a
decimal's column, because SQLite stores a decimal as TEXT as it stores a
string. A field over an INTEGER column that needs TEXT is still reported.

## When a constraint fails

SQLite names no unique constraint or foreign key in a violation. Plasm.Sqlite
gives a unique violation's table and columns (`DbFailure.Table`, `Columns`).
It finds a foreign key's by running the failed statement again under a
savepoint with the check deferred, and reading `pragma_foreign_key_check`
before and after. It reads only the tables the statement can break: an
INSERT's own table, an UPDATE's and those referring to it, a DELETE's referring
tables. It then rolls back to as the failure left things. A violation is then
the field's error, by its table and columns, under any constraint name. In a
database with triggers the statement is not run again, and the failure stays a
database failure naming no constraint.

## The escape hatch

A step of your own (`Tx.run`) can reach Microsoft.Data.Sqlite's own connection
with `Sqlite.connectionOf cmd.Connection`. A command made on it joins the
transaction and has Plasm's decimal functions and collation. It gives up three
things: its token does not interrupt it, Plasm does not refuse a COMMIT or
BEGIN sent through it, and OnCommand does not report it.

## Where SQLite differs

Generated from `tests/Plasm.Providers/differences.txt`, which names where each
difference is stated; the tests' own differences are left out.

| What | PostgreSQL | SQLite |
|---|---|---|
| A key of decimals | keyed by value: numeric keys compare by value | refused when the repository is made (2.0 and 2.00 would be two keys) |
| A decimal's scale | a numeric(p,s) column fixes it (2.0 stored as 2.00) | TEXT keeps the scale written (2.0 stays 2.0); compared, ordered and added by value |
| A writer waiting for another | waits on the row it locks | waits at BEGIN IMMEDIATE for the database, in tries of a second that hear cancellation, up to SqliteOptions.BusyTimeout |
| A window SUM or AVG of decimals | SUM/AVG over numeric | plasm_decimal_sum and plasm_decimal_avg, Plasm.Sqlite's own window functions (Windows.fs, through sqlite3_create_window_function), adding the decimals' values exactly; linear, as numeric's are |
| Every call's thread | Npgsql awaits I/O | Microsoft.Data.Sqlite runs each async call on the caller's thread for the statement's length (an ASP.NET request thread is held for its query; repo.Stream holds one while each row is stepped); only a writer's wait for BEGIN IMMEDIATE moves to the thread pool |
| A cancelled token while a statement runs | Npgsql cancels it on the server | Plasm.Sqlite interrupts it on its connection (sqlite3_interrupt), only while the statement is the caller's; through Sqlite.connectionOf it is not interrupted |
| A wait for a busy database | none in WAL beyond a writer's lock; PostgreSQL's readers never wait for writers | in WAL only a writer's BEGIN IMMEDIATE; on a rollback-journal file a read waits for a writer and a COMMIT for readers; each tried a second at a time, hearing the token, up to SqliteOptions.BusyTimeout |
| A list of doubles holding NaN or an infinity | stored (float8[] holds them) | refused on write: a list is JSON, which has neither |
| List.contains over a list column | an array column; a GIN index can serve it | a scan of each row's JSON (json_each), with nothing like a GIN index to serve it; measured on the native binary when the idle-machine measurements are taken |
| A list column's integrity | the column's array type holds only arrays of its element type | TEXT holds what any tool writes; the Ddl's check holds it to a JSON array, and a read of anything else is a materialization error naming the column and the element |
| A constraint violation's identity | the constraint's name (DbFailure.Constraint), the table, no columns | a unique violation's table and columns; a CHECK's name; no unique or foreign key name. A foreign key's columns are found by running the statement again under a savepoint with the check deferred, comparing pragma_foreign_key_check before and after over the tables the statement can break (an INSERT's or UPDATE's own, and the tables referring to an UPDATE's or DELETE's): two scans of those, on that failure only; the re-run does not hear the caller's token, and is not made once it is cancelled |
| A foreign key failure in a database with triggers | names its constraint | unnamed (Table None, Columns empty): the statement is not run again, so a trigger never fires twice; the failure stays a database failure, not the field's error |
| A recursive query's cycle | CYCLE key SET plasm_cycle USING plasm_path: the recursive table has plasm_cycle and plasm_path (an array of row values) columns | a plasm_path column, the JSON array of the keys above each row; no plasm_cycle column: a row closing a cycle is left out by the step |
| A union stored as its cases' names, as text | a text column: any name is stored, and reading one no case has fails | a text column held to the names by a check Ddl writes, which the schema check compares, label by label |
| A comparison of a decimal column | numeric compared by the column's operators: an index on it serves a range | plasm_decimal_compare(column, value): by value, as no index on the TEXT column orders; a range over decimals scans |
| A sandbox and writers outside it | an outside writer goes on beside the sandbox's transaction (row locks apart) | the sandbox's transaction holds the database's write lock: an outside writer waits for the sandbox to end, and fails after the busy timeout |
| A string field over a decimal's column, in the schema check | incompatible: numeric is no text | not told apart: a decimal is stored as TEXT, as a string is |
| Two writers at once | they go on together: a change can land between one's read and its write (stale, a Conflict), and serializable ones can fail as serialization failures | they take turns: the second waits at its BEGIN IMMEDIATE for the first to end, for SqliteOptions.BusyTimeout (tried a second at a time), then writes or fails as a Timeout; nothing lands between a transaction's read and its write |
| A stale copy across two transactions (read in one, written from in a later one) | caught by the version column: Tx.oneAsSeen at the version the caller saw is a Conflict once the row has moved on | caught the same way: only a change landing inside one transaction cannot happen, its BEGIN IMMEDIATE holding the write lock |
| A division or remainder by zero | a database failure, SQLSTATE 22012 (DbFailureKind.OtherDatabaseError) | a database failure too, in the same words, with no SQLSTATE (OtherDatabaseError): SQLite answers NULL, so its dialect writes each division and remainder as a CASE whose zero divisor reaches a function that fails, and a decimal's are Plasm.Sqlite's functions, which refuse zero; some 40% over SQLite's own division |
| Statements sent together | a write's statements, and a level's loads, travel in one round trip (a DbBatch) | one statement at a time: SQLite runs in the process, with no network to wait for; the statements and their results are the same |
| A retrying transaction (RetryPolicy.OnSerializationFailure) | run again after a serialization failure or a deadlock | never run again: a SQLite transaction meets neither, holding the write lock from its BEGIN; a busy wait that outlasts SqliteOptions.BusyTimeout is a Timeout, which no policy retries, so the wait is the one the caller chose |
| A locked read (FOR UPDATE, FOR SHARE, NOWAIT, SKIP LOCKED) | the rows locked as asked | refused as unsupported (RowLocking false). A possible later change, breaking no one: inside a write transaction, which holds the database's write lock from its BEGIN, FOR UPDATE's guarantee already holds, so a locked read there could be honoured with no SQL |
