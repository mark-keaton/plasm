# Starter

A booking service for shared equipment: an F# domain and its data access on Plasm (`src/Starter.Data`), called by a C# minimal API (`src/Starter.Api`) through one type, `BookingData`, and tested in a sandbox whose changes are rolled back (`tests/Starter.Tests`).

- **Packages:** until Plasm is on nuget.org, put its packages in `packages/`: `dotnet pack src/Plasm.Postgres -o examples/Starter/packages` (and `src/Plasm`, `src/Plasm.Annotations`) from the repository.
- **Run:** create the tables with `dotnet run --project src/Starter.Api -- ddl | psql "$DATABASE"`, then `ConnectionStrings__Starter="Host=...;Database=...;Username=...;Password=..." dotnet run --project src/Starter.Api`.
- **Test:** `dotnet test` (Docker starts a PostgreSQL for the run).
- **Publish natively:** `dotnet publish src/Starter.Api -r linux-arm64` (or your RID); the tests publish the same way and run as a native binary.
- **Make it yours:** replace `Domain.fs` with your records and rules, and `BookingData.fs`'s methods with what your API calls; the C# side changes only where those do.
