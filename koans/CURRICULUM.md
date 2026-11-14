# The Plasm Koans: curriculum

The course starts simple and adds one idea at a time. A learner who knows a
little F# and has never used an ORM should be able to start at koan 1 and never
meet a word, a value or a rule the page has not shown them.

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
   schema, changeset, sandbox), its comment says what it is in one line.
5. **Real data, real database.** Koans run against the koans database in a
   sandbox; nothing is mocked. The starting data is in
   `Plasm.Koans.Core/KoansData.fs` and is the same for every koan.
6. **Every koan teaches something specified.** Each row in a lesson's feature
   names the acceptance scenario of Plasm it teaches. The orientation lesson
   (01), which teaches the F# the course relies on, names an F# topic instead.

## The path

### Part 1: first steps (new)

| # | Lesson | What it is for |
|---|---|---|
| 01 | Getting started | How a koan works, and the F# the course uses: values, records and `with`, options, results, tasks. No database. |
| 02 | Meet the shop | What every koan is given: `k`, whose `Repo` reaches the database; `ok` to open a `Result`; `k.Commands` to see what was sent. Then the koans database: customers, orders, order lines, products. Reading it in the simplest way, and seeing that each koan's changes vanish when it ends. |
| 03 | What a repo is | A repo runs queries and writes against one database with one model. A query is a description; the repo turns it into SQL and runs it; every call returns a `Result`. |
| 04 | Plasm's promises | The invariants Plasm keeps, one koan each: records stay plain F#; nothing runs until you ask; no hidden queries; nothing is evaluated in memory behind your back; failures are values; only what you change is written; the model is checked before any query; queries mean what F# means; relationships load in batches; tests roll back. Each points to the lesson that goes deeper. |

### Part 2: reading (lessons 05 to 09)

05 Records and schemas, 06 Query values, 07 Filters and ordering, 08 Reading,
09 Relationships. Each gains gentler koans at its start: the plain forms
(`Query.from`, one `where`, one `orderBy`, `All`/`One`/`Count`, one preload)
before composition, edge cases and batching.

### Part 3: writing (lessons 10 to 13)

10 Changesets, 11 Writes, 12 Child writes, 13 Transactions. New first koans:
building a changeset from a record, seeing its changes before saving, an insert
assigning the key, a transaction as a value that does nothing until run.

### Part 4: more queries (lessons 14 to 19)

14 Joins, 15 Aggregates and grouping, 16 Many-to-many and has-one, 17 Bulk
writes, 18 Hand-written SQL, 19 Prepared queries. New first koans show the
single simplest use of each feature.

### Part 5: living with it (lessons 20 to 22)

20 Failures, 21 Testing with Plasm, 22 Query pipelines.

## Writing a koan

- The lesson file is `Plasm.Koans/Lessons/LNN_Name.fs`; its reference answers
  are in `answers/NN-name.answers` (`# Koan name`, then one answer per blank, in
  order); its specification is `features/lessons/NN-name.feature`, one row per
  koan: name, what the learner discovers, and what it teaches.
- Write the row first, run the koans' specs and see it fail, then write the
  koan and its answer.
- Koan names are unique across the course; they are the keys of the answers.
- `dotnet test koans/Plasm.Koans.Specs` runs every koan with and without its
  answers, and checks the rules in `features/curriculum.feature`.
