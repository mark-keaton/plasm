# Plasm

The F# domain core of a C# application, over PostgreSQL. Keep ASP.NET, your
host and your team's C#; put the domain and its persistence in F#, where Plasm
stores immutable records, checks every query and makes every write a typed
transaction. It is native AOT first: every commit is compiled ahead of time
and checked natively. It also serves a long-running API through Hot Chocolate,
on the runtime. Its design comes from [Ecto](https://github.com/elixir-ecto/ecto).

> **Status: experimental, not production-ready.** PostgreSQL only.
> [`docs/status.md`](docs/status.md) says what is verified and what is missing.

Site, docs and the koans in the browser: <https://mark-keaton.github.io/plasm/>

## Prompt it yourself

[`website/prompt.txt`](website/prompt.txt) is a prompt to paste into a coding
assistant: it builds a small C# application over an F# domain with Plasm,
tests it against PostgreSQL and publishes it natively. The site's home page
has it with a Copy button.

## C# outside, F# inside

C# passes plain arguments and a cancellation token to one F# type, and gets
back a union the compiler makes it handle case by case. This is
[`examples/Kanban`](examples/Kanban): an endpoint, the switch every endpoint
answers through, and the F# member they call.

<!-- from: examples/Kanban/src/Host/Rest.cs -->
```csharp
card.MapPost("/start", async (Guid id, Versioned input, ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
    Outcomes.Of(await kanban.StartAsync(id, input.Version, user.Actor(), ct)));
```

<!-- from: examples/Kanban/src/Host/Outcomes.cs -->
```csharp
public static IResult Of(ChangeOutcome outcome) => outcome switch
{
    CardView card => Results.Ok(card),
    Missing missing => Of(missing),
    Refused refused => Of(refused),
    Stale stale => Of(stale),
    Invalid invalid => Of(invalid),
};
```

<!-- from: examples/Kanban/src/Data/Facade.fs -->
```fsharp
/// `assignee` starts work on the card, if the board's limit allows it.
member _.StartAsync(card: Guid, version: int, assignee: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
    match field "assignee" Person.create assignee with
    | Error problems -> Task.FromResult(ChangeOutcome(View.invalid problems))
    | Ok who -> change card version (Card.start who (clock ())) cancellationToken
```

C# writes no query and composes no transaction. The switch has no default
arm, so a case added in F# stops every C# switch that misses it from
compiling. Nothing null crosses, either way. [`docs/csharp.md`](docs/csharp.md)
has the rules of the boundary.

## What it guarantees

- **No hidden database access.** What a record owns or links is read with it,
  as a plain field. Nothing is loaded lazily, and nothing is left unloaded.
- **Nothing runs client-side by accident.** A predicate that cannot be
  translated fails with the expression named.
- **No N+1 queries and no cartesian products.** Each owned or linked list is
  one command per batch of owner keys (`= ANY(@keys)`), however many owners
  are read. A page of owners is selected before what they own is read.
- **Typed throughout.** Comparing `o.Total` with a string, owning a field the
  record does not have, or mixing `CustomerId` with `OrderId` are compile
  errors.
- **Partial data cannot destroy data.** `Tx.update (original, changed)`
  writes only the columns that differ, and what the record owns by difference
  from what was read. A projection is not a record and cannot be written back.
  An owned item that belongs to another owner, or that is listed twice, is
  refused.
- **Concurrency is checked.** By a version field, or, for a record without
  one, by every column of the record the change was made from, in the same
  command.
- **Transactions are values,** with typed steps, explicit retries, and
  failures that say what failed and the file and line of the step.
- **Problems surface at start-up.** Starting a repository translates every
  query value and compares the model with the database, naming each problem.

## A record, a query, a transaction

From [`examples/Plasm.Examples`](examples/Plasm.Examples), a runnable tour:
`dotnet run --project examples/Plasm.Examples`.

The record says how it is stored. The lines hold the order's key, so the
order owns them: they are read with it and written with it.

<!-- from: examples/Plasm.Examples/Model.fs -->
```fsharp
[<Table "orders">]
type Order =
    { Id: OrderId
      CustomerId: CustomerId option
      Status: OrderStatus
      Total: decimal
      PlacedAt: DateTime
      [<Version>]
      Version: int
      Lines: OrderLine list }
```

Queries are values, and compose as functions.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let published = plasm { for o in tableOf<Order> do where (o.Status = Published) }
// ...
let unpaid (q: Query<Order>) =
    plasm {
        for o in q do
            where (not (exists (plasm { for p in referring<Payment> o do where (p.Status = Succeeded) })))
    }
```

Writes are transactions with your own error type: read what the decision
needs, let the domain decide, write what it decided.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let changeOrder (id: OrderId) (seen: int) (decide: Order -> Result<Order, OrderError>) : Tx<Order, Refusal> =
    transaction {
        let! order = one "order" (orderById id)
        let! changed = decide order |> Tx.ofResult Domain
        return! Tx.update (order, changed, expectVersion = seen)
    }
```

The guide starts at [`docs/start/five-minutes.md`](docs/start/five-minutes.md);
[`docs/README.md`](docs/README.md) lists every page.

## Hosting

- **A native binary, for example on AWS Lambda.** Plasm compiles with
  `PublishAot` with no trim warning naming it. `examples/Kanban/src/Lambda` is
  a C# function over the F# domain, published natively and run in the Lambda
  runtime interface emulator by its scenarios. [`docs/aot.md`](docs/aot.md)
  has how it works, the measurements and the check every commit passes.
- **A long-running API with Hot Chocolate.** `Plasm.HotChocolate` turns a
  GraphQL request into one query: filters, order and paging reach the SQL, a
  request reads only the fields it selects, and nested lists page per parent
  in one command. It runs on the runtime; GraphQL inside a native binary is
  not available. See [`docs/hotchocolate.md`](docs/hotchocolate.md).
- **The Kanban host** is C# over the F# domain: REST, GraphQL, cookie sign-in
  and a background job. [`docs/kanban-host.md`](docs/kanban-host.md) walks
  through it for a reader who knows C# and ASP.NET Core.

## Learn: the koans

`koans/` is a course in the style of Ruby Koans: small exercises, each with a
blank (`__`) to fill in, run against a real PostgreSQL inside a sandbox so
every koan starts from the same data.
[`koans/CURRICULUM.md`](koans/CURRICULUM.md) has the plan.

```sh
dotnet run --project koans/Plasm.Koans                 # walks the path; stops at the next koan to answer
dotnet run --project koans/Plasm.Koans -- --lesson 5   # one lesson
dotnet run --project koans/Plasm.Koans -- --list       # every lesson and its progress
```

It starts a throwaway PostgreSQL container, or uses the server named by
`PLASM_KOANS_PG`. The same koans run in the browser, on the site.

## Roadmap

[`ROADMAP.md`](ROADMAP.md) is the one list: now, next, later, shipped and not
planned. The site's roadmap page is generated from it.

## Build and test

You need the .NET 11 SDK and Docker. Testcontainers starts PostgreSQL 18; to
use an existing server, set `PLASM_TEST_PG` to an admin connection string.

```sh
dotnet build Plasm.slnx
dotnet test tests/Plasm.Specs      # the acceptance scenarios of features/, and prints how many
dotnet test tests/Plasm.Tests      # unit, property, compile-time and Ecto-compatibility tests
dotnet fsi website/build.fsx       # the site, into _site/; fails if a documented snippet left its source
```

| Path | Contents |
|---|---|
| `src/Plasm`, `src/Plasm.Annotations` | The library, and the attributes a record carries |
| `src/Plasm.Postgres` | The PostgreSQL provider (Npgsql) |
| `src/Plasm.HotChocolate` | GraphQL filters, order, paging and selection over Plasm |
| `features/`, `tests/` | The acceptance specification (Gherkin, run by Reqnroll) and the other tests |
| `examples/Plasm.Examples` | The runnable tour |
| `examples/Kanban` | The reference application; its own solution and scenarios, see its README |
| `koans/` | The koans: lessons, runner, browser version |
| `website/` | The site's sources |
| `benchmarks/` | Plasm against EF Core |
| `docs/` | The documentation, indexed by [`docs/README.md`](docs/README.md) |

## Packages

`Plasm` and `Plasm.Postgres` are versioned `0.1.0-alpha`. They are not on
NuGet yet. `dotnet pack Plasm.slnx -c Release -o artifacts` builds them.

## Licence

[MIT](LICENSE). Plasm adapts behavioural test cases from Ecto (Apache-2.0);
see [`NOTICE`](NOTICE).
