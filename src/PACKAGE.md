# Plasm

The F# domain core of a C# application, over PostgreSQL or SQLite. Keep ASP.NET, your
host and your team's C#; put the domain and its persistence in F#, where Plasm
stores immutable records, checks every query and makes every write a typed
transaction. It is native AOT first: every commit is compiled ahead of time
and checked natively. Its design comes from [Ecto](https://github.com/elixir-ecto/ecto).

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
