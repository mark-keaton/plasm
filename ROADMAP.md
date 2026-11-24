# Roadmap

What Plasm does today, what is being built, and what it will not do. The
repository's `ROADMAP.md` and the site's roadmap page are this one list.

## Now: after 0.4.0

- Plasm.CSharp: a read of many, as an array of views
- Each package's XML documentation describing only what a caller can reach

## Next: in the library

- Selection inside a GraphQL card's comments and labels
- Kanban's own F# server as a native binary

## Later: not started

- Measurements behind a connection proxy on AWS
- A typed GraphQL client checked against the outcome unions

## Shipped

- Plasm, Plasm.Postgres, Plasm.Sqlite, Plasm.HotChocolate and Plasm.CSharp on NuGet: 0.4.0, published from a GitHub release
- Plasm.CSharp: what an F# data layer hands a C# host, the outcome unions and their standard cases
- A SQLite choice in the template, `--database sqlite`, for a new project that needs no database server
- Stored records by convention, owned and linked lists, unions with fields, array columns, composite keys
- Typed queries: joins, grouping, exists, set operations, window functions, filtered aggregates, prepared queries, hand-written SQL
- Recursive queries: a tree read in one statement, with each row's depth; a cycle in the data ends
- Streaming reads: a query's rows as they are read, for records without lists and for projections; records that own or link lists are refused, not batched
- Typed transactions with optimistic concurrency: a record read as the caller saw it, a stale copy refused at the read; row locks, and a step that may fail under a savepoint; a list's writes sent together
- A `dotnet new` template, `Plasm.Templates`: `plasm-api` makes an F# domain, a C# API and tests
- Kanban's C# host and Lambda on the FSharp.Core a new project gets, 11.0.101
- The provider boundary: every piece of database-specific SQL behind the dialect
- A second database provider: SQLite, as the package Plasm.Sqlite; every acceptance feature runs on it
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
