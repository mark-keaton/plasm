# Plasm

The F# domain core of a C# application, over PostgreSQL. Keep ASP.NET, your
host and your team's C#; put the domain and its persistence in F#, where Plasm
stores immutable records, checks every query and makes every write a typed
transaction. It is native AOT first: every commit is compiled ahead of time
and checked natively. Its design comes from [Ecto](https://github.com/elixir-ecto/ecto).

> **Status: experimental, not production-ready.** PostgreSQL only.
> [docs/status.md](https://github.com/mark-keaton/plasm/blob/main/docs/status.md)
> says what is verified and what is missing.

```sh
dotnet add package Plasm.Postgres
```

- **Plasm.Postgres**: the PostgreSQL provider (Npgsql), with Plasm itself. The package an application references.
- **Plasm**: records stored as the domain declares them, compiler-checked queries and typed transactions, with no provider.
- **Plasm.Annotations**: the attributes a record uses, with their F# sources for Fable.
- **Plasm.HotChocolate**: GraphQL filter and sort types from F# fields, applied in the database.
- **Plasm.Templates**: `dotnet new plasm-api -n Name`, an F# domain on Plasm.Postgres with a C# minimal API that publishes with native AOT, and C# tests.

The guide, the examples and the koans in the browser:
<https://mark-keaton.github.io/plasm/>. The source, the roadmap and the
issues: <https://github.com/mark-keaton/plasm>. MIT licence.
