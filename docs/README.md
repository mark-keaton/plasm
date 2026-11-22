# Plasm documentation

The site's docs menu is generated from this list, in this order. The pages
under Start and Guide are written for a reader; the pages under Hosting and
Reference are the engineering records the library is built against.

## Start

- [Plasm in five minutes](start/five-minutes.md)
- [The one way](start/one-way.md)
- [In F# Interactive](start/fsi.md)
- [Upgrading](start/upgrading.md)

## Guide

- [Types and records](guide/types-and-records.md)
- [Queries](guide/queries.md)
- [Transactions](guide/transactions.md)
- [Starting a repository](guide/starting-a-repository.md)
- [Testing](guide/testing.md)

## Hosting

- [From C#](csharp.md)
- [Hot Chocolate](hotchocolate.md)
- [Native AOT and Lambda](aot.md)
- [SQLite](sqlite.md)
- [The Kanban example](kanban-host.md)

## Reference

- [Status](status.md)
- [Acceptance specification](acceptance.md)
- [Benchmarks](benchmarks.md)
- [Compared with other libraries](comparison.md)
- [Ecto compatibility](ecto-compatibility.md)
- [Architecture decisions](adr/0001-architecture.md)

## Writing these pages

A code block under Start or Guide is preceded by a comment naming the file it
is taken from, `<!-- from: examples/Plasm.Examples/Model.fs -->`. The site
build (`website/build.fsx`) fails when a line of such a block is no longer in
that file, in that order, so the pages cannot drift from code that compiles.
A line reading `// ...` marks lines left out.
