# Plasm

A stricter data layer for a C# application, over PostgreSQL or SQLite. You
write the domain and its data code in one small F# project and call it from
C#; ASP.NET, the host and your team's C# stay as they are. In that project
Plasm stores immutable records, checks every query and makes every write a
typed transaction, and C# gets each outcome as a union the compiler makes it
handle case by case. It is native AOT first: every commit is compiled ahead of
time and checked natively. Its design comes from [Ecto](https://github.com/elixir-ecto/ecto).

> **Plasm is new, and its API may change before 1.0.**
> [docs/status.md](https://github.com/mark-keaton/plasm/blob/main/docs/status.md)
> says what is verified and what is missing, and
> [docs/sqlite.md](https://github.com/mark-keaton/plasm/blob/main/docs/sqlite.md)
> where SQLite answers otherwise than PostgreSQL.

```sh
dotnet add package Plasm.Postgres   # or Plasm.Sqlite
```

- **Plasm.Postgres**: the PostgreSQL provider (Npgsql), with Plasm itself. The package an application references.
- **Plasm.Sqlite**: the SQLite provider (Microsoft.Data.Sqlite, which brings SQLite itself), with Plasm itself.
- **Plasm**: records stored as the domain declares them, compiler-checked queries and typed transactions, with no provider.
- **Plasm.Annotations**: the attributes a record uses, with their F# sources for Fable.
- **Plasm.HotChocolate**: GraphQL filter and sort types from F# fields, applied in the database.
- **Plasm.CSharp**: what a C# host is given by an F# data layer: each outcome a union C# switches over exhaustively (`Found`, `Added`, `Answered`, `Changed`) of plain cases (`Missing`, `Refused`, `Stale`, `Invalid`), read from Plasm's results, with their JSON. It depends on Plasm alone.
- **Plasm.Templates**: `dotnet new plasm-api -n Name`, an F# domain with a C# minimal API that publishes with native AOT, and C# tests, on Plasm.Postgres; with `--database sqlite`, on Plasm.Sqlite, a database file and no server. The C# side takes Plasm.CSharp.

The guide, the examples and the koans in the browser:
<https://mark-keaton.github.io/plasm/>. The source, the roadmap and the
issues: <https://github.com/mark-keaton/plasm>. MIT licence.
