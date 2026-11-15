# Plasm

Persistence for F# that follows F#'s semantics: immutable records, typed
composable queries, explicit changesets, batched relationship loading, and
explicit effects. Its design comes from [Ecto](https://github.com/elixir-ecto/ecto).

> **Status: experimental first delivery, not production-ready.** PostgreSQL only.
> See [`docs/status.md`](docs/status.md) for what is verified and what is missing.

## How to build an application with Plasm

Store your domain's own records. Write each fact about the data once, in the
type if F# can say it there:

- **Types.** A single-case wrapper is stored as its value. A `create` function
  checks every value that is read. A union with fields is spread over columns.
  A typed id is a reference.
- **The schema.** It states only what the types cannot: the key, `unique`
  fields, and what a record `owns` (its own child rows) or `links` (shared rows
  through a join table). Owned and linked lists are plain lists, always loaded
  and written with their owner.
- **Business transactions.** Write them as `Tx<'T, 'E>` values with your own
  error type.
- **Start-up or a test.** Check queries with `repo.CheckQueries` and the
  schema with `repo.CheckSchema`. `repo.Ddl()` prints the tables the model
  expects.

```fsharp
type Stage =
    | Backlog
    | Doing of assignee: Person * since: DateTimeOffset
    | Done of resolution: Resolution * at: DateTimeOffset

type Card =
    { Id: CardId; Board: BoardId; Title: Title; Stage: Stage
      Comments: Comment list; Labels: Tag list; Created: DateTimeOffset; Version: int }

let cards =
    schema<Card> "cards" {
        key (fun c -> c.Id)
        version (fun c -> c.Version)
        ownsOrderedBy (fun c -> c.Comments) (fun m -> m.Card) (fun m -> m.At)
        linksOrderedBy (fun c -> c.Labels) "card_labels" "card_id" "label_id" (fun l -> l.Name)
    }

let addCard slug (create: Board -> Card) : Tx<Card, Refusal> =
    transaction {
        let! board = Tx.one (Queries.boardBySlug slug) |> Tx.orReject (Missing "board")
        return! Tx.insert (create board)
    }
```

[`examples/Kanban`](examples/Kanban) is the reference application in this
style. Its domain (`src/Shared/Domain.fs`) references no package and also
compiles to JavaScript with Fable. Its storage (`src/Server/Db.fs`) is five
short schemas. The shape it sends over the wire (`src/Shared/Api.fs`) is the
application's own: Plasm does not serialize.

Plasm also supports an older row-record style: `Assoc<'T>` fields loaded with
`preload`, and changesets. It is fully supported and specified, but it is not the
recommended way to start an application. Use it when the stored rows really
are a different shape from the domain, or when relationships should load only
on request. The overview below and the koans are written mostly in this
style.

```fsharp
open Plasm

// Records stay ordinary immutable F# records. Relationships say whether they are loaded.
type Order =
    { Id: OrderId; CustomerId: CustomerId option; Status: OrderStatus; Total: decimal
      CancellationReason: string option; PlacedAt: DateTime; Version: int
      Customer: Assoc<Customer option>; Lines: Assoc<OrderLine list>; Payments: Assoc<Payment list> }

let orders =
    schema<Order> "orders" {
        identityKey (fun o -> o.Id)
        version (fun o -> o.Version)
        belongsTo (fun o -> o.Customer) (fun o -> o.CustomerId)
        hasMany (fun o -> o.Lines) (fun l -> l.OrderId)
        hasMany (fun o -> o.Payments) (fun p -> p.OrderId)
    }

// Queries are values. Reusable parts are ordinary functions.
let successfulPayments = plasm { for p in payments do where (p.Status = Succeeded); orderBy p.CreatedAt }
let linesWithProducts = plasm { for l in orderLines do preload l.Product }

let page customer =
    plasm {
        for o in orders do
            where (o.CustomerId = Some customer)
            orderByDescending o.PlacedAt
            take 20
            preload o.Lines linesWithProducts
            preload o.Payments successfulPayments
    }

// Execution is explicit: four batched commands, assembled into immutable values.
let! result = repo.All(page customerId)

// Joins are explicit: one command, typed rows. A left-joined record is an option.
let unpaidWithCustomer =
    plasm {
        for o in orders do
            leftJoin c in customers on (o.CustomerId = Some c.Id)
            where (not (o.Payments |> Rel.any (fun p -> p.Status = Succeeded)))
            orderBy o.PlacedAt
            select (o.Id, o.Total, c |> Option.map (fun c -> c.Email))
    }

// Changes are values. Plasm validates nothing: a constraint the database
// enforces comes back as the field's error.
let cs = order |> Changeset.edit (fun o -> { o with Status = Cancelled })

match! repo.Update cs with
| Ok updated -> ...
| Error(WriteError.Invalid cs) -> cs.Errors      // structured, per field
| Error(WriteError.Stale _) -> ...               // optimistic concurrency
| Error(WriteError.Failed f) -> f.Kind           // classified infrastructure failure
```

Counts, aggregates, set-based writes, hand-written SQL and prepared queries
use the same values:

```fsharp
// Grouped summaries, computed by the database
plasm {
    for o in Db.orders do
        join c in Db.customers on (o.CustomerId = Some c.Id)
        groupBy c.Name
        having (Agg.count () > 1L)
        select (c.Name, Agg.count (), Agg.sum o.Total)
}

let! n = repo.Count published                                     // int64
let! _ = repo.Execute(Bulk.update (drafts, fun o -> { o with Status = Cancelled }))
let! _ = repo.Execute(Bulk.upsert (products, (fun p -> p.Sku), fun current incoming -> { current with Name = incoming.Name }))
let! rows = repo.Sql<string * int>($"SELECT name, count(*) FROM customers WHERE name <> {excluded} GROUP BY name")

// Built and planned once; each call binds parameters
let ordersOf = Query.prepare (fun (c: Arg<CustomerId>) ->
    plasm { for o in Db.orders do where (o.CustomerId = Some c.Value) })
let! orders = repo.All(ordersOf customerId)

// Many-to-many through a join table; links are written, tags are not
let! _ = repo.Update(Changeset.update article |> Changeset.putLinks ((fun a -> a.Tags), [ fsharp; sql ]))

// At start-up: does the model match the database?
let! issues = repo.CheckSchema()

// In tests: everything inside is rolled back
do! repo.Sandbox(fun repo -> task { ... })
```

### In F# Interactive

```fsharp
#r "nuget: Plasm.Postgres"          // once published; until then #r the built DLLs
open Plasm
open Plasm.Postgres
open Plasm.Interactive               // wait: show a result, not a task

let repo = Repo(model, Postgres.connect connectionString)

repo.Scaffold "customers" |> wait    // prints a record and schema for the table
let q = plasm { for c in customers do where (c.Name.StartsWith "A") }
q                                    // displays its SQL and parameter values; no database access
repo.All q |> wait                   // Ok [ ... ]

let pg = repo.Playground()           // changes last across submissions, never committed
pg.Insert(Changeset.insert ada) |> wait
pg.Reset()                           // roll everything back; pg.Close() ends it
```

No setup call is needed: queries display themselves. A query built before you
redefined one of its types explains that it must be re-evaluated instead of
failing with an unmapped type.

[`examples/Plasm.Examples`](examples/Plasm.Examples) has a runnable tour:
`dotnet run --project examples/Plasm.Examples`.

## Learn Plasm: the koans

`koans/` is an interactive course in the style of Ruby Koans: 22 lessons and
217 koans, each a small exercise with a blank (`__`) to fill in, run against a
real PostgreSQL database inside a sandbox, so every koan starts from the same
data. It starts with no Plasm at all (the F# the course uses), then the shop's
data, what a repo is and the promises Plasm keeps, before the library itself;
[`koans/CURRICULUM.md`](koans/CURRICULUM.md) has the plan. In the browser
version, each koan opens with what it is given: the tables it reads, with their
starting rows, and the definitions it uses.

```sh
dotnet run --project koans/Plasm.Koans                 # walks the path; stops at the next koan to answer
dotnet run --project koans/Plasm.Koans -- --lesson 5   # one lesson
dotnet run --project koans/Plasm.Koans -- --list       # every lesson and its progress
```

It starts a throwaway PostgreSQL container, or uses the server named by
`PLASM_KOANS_PG`.

The same koans come as a notebook, `koans/notebook/plasm-koans.ipynb`
(Polyglot Notebooks, or Jupyter with the .NET kernel): build the koans
(`dotnet build koans/Plasm.Koans`), run the setup cell, then edit and run each
koan cell until it shows ✔. The notebook is generated from the lessons
(`dotnet run --project koans/Plasm.Koans -- --notebook koans/notebook/plasm-koans.ipynb`),
and it is checked cell by cell both in a hosted F# Interactive session and in
a real .NET Interactive kernel (`dotnet-repl`, a local tool: `dotnet tool
restore`), as shipped and with the reference answers. What each koan teaches is specified in
`koans/features/lessons/*.feature`, each tracing to an acceptance scenario of
Plasm; `koans/Plasm.Koans.Specs` checks that every koan passes with its
reference answer (`koans/answers/`) and stops at its blank as shipped.

## What it guarantees

- **No hidden database access.** `Assoc` is `NotLoaded | Loaded of 'T`, and
  reading it never queries.
- **Nothing runs client-side by accident.** A predicate that cannot be
  translated fails with the expression named.
- **No N+1 queries and no cartesian products.** Each relationship is one command
  per batch of parent keys (`= ANY(@keys)`). Parent pages are selected before
  children are loaded.
- **Limits mean what they say.** `limitPerParent` is per parent, and `take` in a
  load query is rejected as ambiguous.
- **Typed throughout.** Comparing `o.Total` with a string, loading a field that
  isn't a relationship, or mixing `CustomerId` with `OrderId` are compile errors.
- **Partial data cannot destroy data.** Unmentioned fields are never written,
  updates from projections touch only the stated fields, and child replacement
  needs an explicit scope and removal policy.
- **Transactions are values,** with named, typed steps, explicit retries, and
  errors that name the failed step.

## Repository

| Path | Contents |
|---|---|
| `src/Plasm` | Core: schemas, model, query CE and translation, planner, changesets, transactions, repo |
| `src/Plasm.Postgres` | PostgreSQL provider (Npgsql) |
| `features/` | The acceptance specification (Gherkin), executed by Reqnroll |
| `tests/Plasm.Specs`, `tests/Plasm.Specs.Steps` | Reqnroll host (C#, generated) and F# step definitions |
| `tests/Plasm.Tests` | Translation, compile-time, property-based, Ecto-compatibility and behaviour tests |
| `tests/Plasm.TestDomain` | Shared test domain, PostgreSQL fixture (Testcontainers) and compile checker |
| `examples/Plasm.Examples` | Runnable API tour |
| `examples/Kanban` | A SAFE-style application (ASP.NET Core, Fable, Elmish) with a plain F# domain at its centre and Plasm behind it; its own solution and scenarios, see its README |
| `koans/` | The Plasm Koans: lessons, runner, reference answers, and their own specifications |
| `website/` | Static library website (landing page and worked examples, each with a toggle to the SQL Plasm generated for it); no build step: `python3 -m http.server -d website`. Regenerate the captured SQL with `dotnet run --project examples/Plasm.Examples -- sql` |
| `benchmarks/` | Plasm vs EF Core 11 (single and split query) |
| `docs/` | [ADR](docs/adr/0001-architecture.md), [acceptance](docs/acceptance.md), [status](docs/status.md), [benchmarks](docs/benchmarks.md), [Ecto compatibility](docs/ecto-compatibility.md) |

## Building and testing

You need the .NET 11 SDK and Docker. Testcontainers starts PostgreSQL 18; to use
an existing server, set `PLASM_TEST_PG` to an admin connection string.

```sh
dotnet build Plasm.slnx
dotnet test tests/Plasm.Specs      # the 229 acceptance scenarios
dotnet test tests/Plasm.Tests      # unit, property, compile-time and Ecto-compatibility tests
dotnet run -c Release --project benchmarks/Plasm.Benchmarks -- counts
```

## Packages and CI

`src/Plasm` and `src/Plasm.Postgres` are the only packable projects
(`Plasm` and `Plasm.Postgres`, version `0.1.0-alpha`; metadata in
`src/Directory.Build.props`):

```sh
dotnet pack Plasm.slnx -c Release -o artifacts
```

`.github/workflows/ci.yml` runs on pushes to `main`, on pull requests, and on
demand:

- builds the solution, runs every test (PostgreSQL via Testcontainers), and
  uploads the packages as a build artifact, versioned
  `0.1.0-alpha.<run number>`;
- regenerates the website's captured SQL and fails if `website/sql.js` is out
  of date (clock-derived timestamps are ignored).

Nothing is published to a package feed. No license has been chosen yet, so the
packages carry no license metadata and should not be published publicly.

Plasm adapts behavioural test cases from Ecto (Apache-2.0). See [`NOTICE`](NOTICE).
