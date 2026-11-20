# Roadmap

What Plasm does today, what is being built, and what it will not do. The
repository's `ROADMAP.md` and the site's roadmap page are this one list.

## Now: going public

- Plasm and Plasm.Postgres on NuGet
- A `dotnet new` template

## Next: in the library

- Batched writes for a list of transaction steps
- Selection inside a GraphQL card's comments and labels
- Kanban's own F# server as a native binary
- Kanban's C# host and Lambda on the FSharp.Core a new project gets (they run on 10.1 today)

## Later: not started

- A second database provider
- Measurements behind a connection proxy on AWS
- A typed GraphQL client checked against the outcome unions

## Shipped

- Stored records by convention, owned and linked lists, unions with fields, array columns, composite keys
- Typed queries: joins, grouping, exists, set operations, window functions, filtered aggregates, prepared queries, hand-written SQL
- Typed transactions with optimistic concurrency: a record read as the caller saw it, a stale copy refused at the read; row locks, and a step that may fail under a savepoint
- Start-up checks of every query and of the schema, every statement described by the database before any runs
- Each command as an OpenTelemetry activity
- Native AOT, with a Lambda function in F# and one in C# over F#
- Plasm.HotChocolate: filters, order, paging, selection, a page per parent
- The Kanban reference application and its C# host
- The koans: terminal and browser
- The public repository, the site with docs and browser koans, llms.txt

[`docs/status.md`](docs/status.md) lists each of these with the acceptance
scenarios that check it.

## Not planned

- Migrations. Plasm prints the tables the model expects and checks the database against them.
- Lazy loading and change tracking.
- GraphQL inside a native binary. Hot Chocolate hosts run on the runtime.
- A second way to write anything. There is one style.
