# Roadmap

What Plasm does today, what is being built, and what it will not do. The
repository's `ROADMAP.md` and the site's roadmap page are this one list.

## Now: going public

- A licence (MIT), then a public repository
- The site, with the docs and the koans in the browser
- Plasm and Plasm.Postgres on NuGet
- llms.txt: the docs in plain text for coding assistants

## Next: in the library

- Composite keys
- Batched writes for a list of transaction steps
- Selection inside a GraphQL card's comments and labels
- Kanban's own F# server as a native binary

## Later: not started

- A second database provider
- Measurements behind a connection proxy on AWS
- A typed GraphQL client checked against the outcome unions

## Shipped

- Stored records by convention, owned and linked lists, unions with fields, array columns
- Typed queries: joins, grouping, exists, prepared queries, hand-written SQL
- Typed transactions with optimistic concurrency
- Start-up checks of every query and of the schema
- Native AOT, with a Lambda function in F# and one in C# over F#
- Plasm.HotChocolate: filters, order, paging, selection, a page per parent
- The Kanban reference application and its C# host
- The koans: terminal and browser

[`docs/status.md`](docs/status.md) lists each of these with the acceptance
scenarios that check it.

## Not planned

- Migrations. Plasm prints the tables the model expects and checks the database against them.
- Lazy loading and change tracking.
- GraphQL inside a native binary. Hot Chocolate hosts run on the runtime.
- A second way to write anything. There is one style.
