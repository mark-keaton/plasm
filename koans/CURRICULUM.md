# The Plasm Koans: curriculum

The course starts simple and adds one idea at a time. A learner who knows a
little F# and has never used an ORM should be able to start at koan 1 and never
meet a word, a value or a rule the page has not shown them.

The course teaches the one way Plasm builds an application. The shop's records
are the application's own: ids, validated values (`Email`, `Quantity`) and
fieldless unions are stored by convention, with no mappings. An aggregate holds
what it owns as plain fields (records holding its key), and shared records
through a join table (`[<JoinTable>]`); they are always read and written with it.
Each stored record names its table (`[<Table>]`); everything else follows from
its types.
Other aggregates are referred to by id and reached by queries (`plasm { }`,
joins, `exists`). Plasm validates no input: the domain's constructors do, and
the database's constraints come back as field errors. Every write is a step of a
typed transaction, `Tx<'T, 'E>`, run by `repo.Transact`. `examples/Kanban` is
the reference application in the same style.

## Principles

1. **One idea per koan.** A koan has one or two blanks. If it needs three, it is
   probably two koans.
2. **Every blank is answerable from the page.** The answer comes from the koan's
   code, the data it starts from (the koans database, shown under *Given*), or a
   definition in `Shop.fs` (also shown under *Given*). Never from internals the
   learner cannot see, and never from memory of an earlier run.
3. **Simple before clever.** Within a lesson the gentlest koans come first: the
   plain form of a thing before its edge cases.
4. **Comments name new words.** The first time a koan uses a word (repo,
   model, aggregate, transaction, sandbox), its comment says what it is in one line.
5. **Real data, real database.** Koans run against the koans database in a
   sandbox; nothing is mocked. The starting data is in
   `Plasm.Koans.Core/KoansData.fs` and is the same for every koan.
6. **Koans are written as an app would be.** A koan is the body of
   `fun repo -> task { ... }`, and the page shows only the body. It uses Plasm's
   own API (`repo.All`, `repo.Explain`, `Repo.Start(model, provider, queriesIn = ...)`) and the test
   helpers any Plasm test would have (`shouldBe`, `ok`, `err`, `sqlSent ()`),
   never machinery that exists only for the koans. Under *Given*, every
   definition says whether it is *your app*'s code or a *test helper*.
7. **Every koan teaches something specified.** Each row in a lesson's feature
   names the acceptance scenario of Plasm it teaches. The orientation lesson
   (01), which teaches the F# the course relies on, names an F# topic instead.

## The path

### Part 1: first steps (new)

| # | Lesson | What it is for |
|---|---|---|
| 01 | Getting started | How a koan works, and the F# the course uses: values, records and `with`, options, results, tasks. No database. |
| 02 | What a repo is | What every koan is given: `repo`, a Plasm `Repo` whose changes are undone when the koan ends; `ok` to open a `Result`; and how an app makes its own repo, `Repo(Db.model, provider)`. A repo runs queries and writes against one database with one model. A query is a description; the repo turns it into SQL and runs it; every call returns a `Result`. |
| 03 | Meet the shop | The koans database: customers, orders holding their lines, products. Reading it in the simplest way, and seeing that each koan's changes vanish when it ends. |
| 04 | Plasm's promises | The invariants Plasm keeps, one koan each: records stay plain F#; nothing runs until you ask; no hidden queries; nothing is evaluated in memory behind your back; failures are values; only what you change is written; the model is checked before any query; queries mean what F# means; what an aggregate owns loads in one batch; tests roll back. Each points to the lesson that goes deeper. |

### Part 2: reading (lessons 05 to 09)

| # | Lesson | What it is for |
|---|---|---|
| 05 | Stored records | A stored record is still plain F#; it names its table, and its key is its field named Id; ids, validated values and fieldless unions are stored by convention; a stored value the domain refuses is a read error; a model reports every mistake at once, and a list of records that are not stored is refused. |
| 06 | Query values | A query is a value: `Query.from`, `plasm { }`, composing, captured values as parameters. |
| 07 | Filters and ordering | `where` and `orderBy` with F#'s meaning, including options and negation. |
| 08 | Reading | `All`, `One`, `Count`, `Exists`, and where the database work ends. |
| 09 | Aggregates and references | An order is read with its lines, in one more command for any number of orders; filtering by the lines with `List.exists` and `List.forall`; a page of orders reads only its own lines; a customer referred to by id and reached with a query or a join; `exists` over another aggregate. |

### Part 3: writing (lessons 10 to 13)

| # | Lesson | What it is for |
|---|---|---|
| 10 | Writes | Every write is a transaction step: `Tx.insert`, `Tx.update (original, changed)`, `Tx.delete`, run by `repo.Transact`. Only what differs is written; a stale copy is a `TxFailure.Conflict`, checked by version or, without one, by every column; `Tx.oneAsSeen` for a caller's copy. |
| 11 | Constructors and constraints | Plasm validates no input: the domain's constructors (`Email.create`) do, and stored values are read back through them. A duplicate, a missing reference or a broken check comes back as the field's error, by the constraint's default name; any other constraint is a database failure. |
| 12 | Writing an aggregate | Inserting an order inserts its lines; an update writes the difference by key; a line left out is deleted; a line's error is the order's; deleting an order deletes its lines. |
| 13 | Transactions | `Tx<'T, 'E>` as a value; steps compose with `let!`; reading inside with `Tx.one` and `Tx.count`; the transaction's own rejections (`Tx.fail`, `Tx.orReject`, `Tx.ofResult`); read, decide, write, reading the record as the caller saw it (`Tx.oneAsSeen`), so a stale copy is refused before anything is decided; where a failed step is written; retries; custom steps. |

Writes come before constructors and constraints, since a constraint's error is
met through a write. Each error case is named where a koan first meets it
(`TransactionError` and `TxFailure.InvalidChanges` in lesson 04,
`DbError.Translation` in 02, `DbError.Materialization` in 05,
`TxFailure.Conflict` in 10, `TxFailure.Failed` and `DbError.Database` in 11,
`RolledBack` and `TxFailure.Rejected` in 13); lesson 21 gathers them.

### Part 4: more queries (lessons 14 to 20)

| # | Lesson | What it is for |
|---|---|---|
| 14 | Joins | Inner and left joins, selecting fields or whole records (a whole Order comes with its lines), typed keys, filters and composition. |
| 15 | Loads | `let!` binds what another query finds for each record, `and!` one more beside it, and `return` builds the row: each load one more command for every record, a load's query with loads of its own, `referredBy` for the one record a key finds, and a relationship that says more written once as a function. |
| 16 | Aggregates and grouping | `Agg.count`, `sum`, `max`, grouping and `having`. |
| 17 | Links and owned records | An article's tags, shared through a join table: read with it, filtered with `List.exists`, written as links only. An account's optional profile: read as an option, inserted and deleted by changing it. Its last koan counts a join table with `repo.Sql`, which lesson 19 teaches. |
| 18 | Bulk writes | `Bulk.update`, `delete`, `insert`, `insertMissing` as `Tx.execute` steps, and inspecting them. |
| 19 | Hand-written SQL | `repo.Sql<'R>`, interpolation as parameters, fragments, `Tx.executeSql` and `Tx.sql` inside a transaction. |
| 20 | Prepared queries | `Query.prepare`: built once, typed arguments, paging, composing, and arguments inside an `exists`. |

### Part 5: living with it (lessons 21 to 23)

| # | Lesson | What it is for |
|---|---|---|
| 21 | Failures | Results, `TxFailure`, the database's failures and the transaction's own rejections, read errors, qualified cases. |
| 22 | Testing with Plasm | Sandboxes; `Repo.Start` checking the app's query values, `repo.CheckSchema()`, and `repo.Ddl()`. |
| 23 | Unions with fields | A union whose cases carry facts, stored across columns, and the domain's own members in queries. |

### Part 6: what came after (lessons 24 on)

Lessons for what Plasm gained after the course was first written, each after
the lessons it builds on.

| # | Lesson | What it is for |
|---|---|---|
| 24 | Set operations | `Query.union`, `unionAll`, `intersect` and `except` of two queries of the same rows, ordered and paged as a whole; an operand that orders itself refused; projections combined; a query as a join's source. |
| 25 | Windows and aggregates over rows | `Win.rowNumber`, `runningSum`, `lag`, `sum` and `count` over `Win.partition` or `Win.whole`, beside each row; a window over the rows kept; a rank over no order refused; `Agg.countWhere`, `sumWhere`, `any`, `all`, `listBy` and its Descending form; what each gives over no rows. |
| 26 | Locks and tries | `locking RowLock.ForUpdate` read by `Tx.all` in a transaction, its lock on the query's own rows and not on what they own; `Tx.attempt`, a step's failure (a duplicate, a stale copy's Conflict) as a value the transaction goes on from, and a tried step that succeeds kept. NOWAIT and SKIP LOCKED are not taught: what they do needs a second transaction at once, which the browser course's database cannot have (`kept-out.txt`). |
| 27 | Bulk writes that return their rows | `Bulk.updateReturning`, the rows as they are after it with what they own; `Bulk.deleteReturning`, the rows as they were; a delete of records that own others refused by name. |
| 28 | Recursive queries | `Query.recursive` over the shop's categories: everything under a category and everything above one, with `Query.depth`, in one statement; the query made from it filtered and ordered as a whole; a cycle in the data ending; a step that does not join `found` refused. |
| 29 | Streaming reads | `repo.Stream`, an `IAsyncEnumerable` of Results asked for one row at a time; nothing sent before the first row, the command reported when the stream ends; a projection streamed; a query of records that own a list one Error naming it. |

## Writing a koan

- The lesson file is `Plasm.Koans/Lessons/LNN_Name.fs`; its reference answers
  are in `answers/NN-name.answers` (`# Koan name`, then one answer per blank, in
  order); its specification is `features/lessons/NN-name.feature`, one row per
  koan: name, what the learner discovers, what it teaches, and its hint.
- A hint is one sentence that says where on the page the answer comes from,
  without the answer. The runner shows it after a wrong answer; the browser
  course behind a button the learner chooses. If no such sentence can be written, the page does not hold the
  answer: reword the koan, or leave the cell empty and say what is missing in
  `no-hint-yet.txt`.
- Write the row first, run the koans' specs and see it fail, then write the
  koan and its answer.
- Koan names are unique across the course; they are the keys of the answers.
- `dotnet test koans/Plasm.Koans.Specs` runs every koan with and without its
  answers, and checks the rules in `features/curriculum.feature`.
