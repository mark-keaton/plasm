# Starter

A booking service for shared equipment: an F# domain and its data access on Plasm (`src/Starter.Data`), called by a C# minimal API (`src/Starter.Api`) through one type, `BookingData`, and tested in a sandbox whose changes are rolled back (`tests/Starter.Tests`).

- **Packages:** Plasm.Postgres comes from nuget.org at the version `Directory.Packages.props` names; `dotnet restore` fetches it with Plasm and Plasm.Annotations.
- **Run:** create a database and its tables, `createdb starter && dotnet run --project src/Starter.Api -- ddl | psql -d starter`, then `ConnectionStrings__Starter="Host=localhost;Database=starter;Username=...;Password=..." dotnet run --project src/Starter.Api --urls http://localhost:5080`. Add `LogSql=true` to print each command Plasm sends.
- **Test:** `dotnet test` (Docker starts a PostgreSQL for the run).
- **Publish natively:** `dotnet publish src/Starter.Api -r linux-arm64` (or your RID); the tests publish the same way and run as a native binary.
- **Make it yours:** replace `Domain.fs` with your records and rules, and `BookingData.fs`'s methods with what your API calls; the C# side changes only where those do.
- **Answers:** a value its `create` refuses (`EquipmentName`) answers 400 with the field; a refusal of the rules answers 422; a stale copy 409.
