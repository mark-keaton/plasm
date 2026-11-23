# Starter

An example to replace with your own service. As it comes, it books shared equipment: an F# domain and its data access on Plasm (`src/Starter.Data`), called by a C# minimal API (`src/Starter.Api`) through one type, `BookingData`, and tested in a sandbox whose changes are rolled back (`tests/Starter.Tests`).

<!--#if (STARTER_SWITCH) -->
This copy builds for PostgreSQL. Add `-p:StarterDatabase=sqlite` to a `dotnet` command to build it for SQLite, a file with no server. A project made by `dotnet new plasm-api` keeps one of the two, PostgreSQL unless it is given `--database sqlite`, and not this switch.

For PostgreSQL:

<!--#endif -->
<!--#if (!STARTER_SQLITE) -->
- **Packages:** Plasm.Postgres comes from nuget.org at the version `Directory.Packages.props` names; `dotnet restore` fetches it with Plasm and Plasm.Annotations.
- **Run:** create a database and its tables, `createdb starter && dotnet run --project src/Starter.Api -- ddl | psql -d starter`, then `ConnectionStrings__Starter="Host=localhost;Database=starter;Username=...;Password=..." dotnet run --project src/Starter.Api --urls http://localhost:5080`. Without PostgreSQL on the machine, run it in Docker: `docker run -d --name starter-db -e POSTGRES_PASSWORD=starter -e POSTGRES_DB=starter -p 5432:5432 postgres:18` (if port 5432 is taken, publish another, `-p 5433:5432`, and add `Port=5433` to the connection string), then `dotnet run --project src/Starter.Api -- ddl | docker exec -i starter-db psql -U postgres -d starter`, and the connection string `Host=localhost;Database=starter;Username=postgres;Password=starter`. Add `LogSql=true` to print each command Plasm sends, and `ClockStart=2030-01-02T09:00:00Z` to start the API's clock at that instant (it runs on from there; unset, it is the system's), to see a rule about time over HTTP.
- **Test:** `dotnet test` (Docker starts a PostgreSQL for the run).
- **Publish natively:** `dotnet publish src/Starter.Api -r linux-arm64` (or your RID), then `ConnectionStrings__Starter="..." ClockStart=2030-01-02T09:00:00Z src/Starter.Api/bin/Release/net11.0/linux-arm64/publish/Starter.Api --urls http://localhost:5080`. The tests publish and run the same way: `dotnet publish tests/Starter.Tests -r linux-arm64`, then `tests/Starter.Tests/bin/Release/net11.0/linux-arm64/publish/Starter.Tests`. Two IL warnings are expected, FSharp.Core's IL2104 and IL3053; any other `warning IL` is a problem. On macOS the linker's `dsymutil` also prints warnings about duplicate debug map objects and a missing `.pcm`: they concern the `.dSYM` debug symbols only.
- **Configure the database:** `src/Starter.Data/Store.fs` opens it from the connection string, and its `dataSource` function is where its connections are configured, on `NpgsqlDataSourceBuilder`: a password provider, SSL, a pooler's settings, logging. Nothing else in the project names the database.
<!--#endif -->
<!--#if (STARTER_SWITCH) -->

For SQLite (`-p:StarterDatabase=sqlite`):

<!--#endif -->
<!--#if (STARTER_SWITCH || STARTER_SQLITE) -->
- **Packages:** Plasm.Sqlite comes from nuget.org at the version `Directory.Packages.props` names; `dotnet restore` fetches it with Plasm, Plasm.Annotations and SQLite's own native library, so there is no database server to install.
- **Run:** `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Starter.Api --urls http://localhost:5080`. The database is the file `starter.db` in the working directory, unless `ConnectionStrings__Starter` names another (`Data Source=/path/to/file.db`). In Development a missing database file is made at start, with the tables `BookingData.Ddl` creates, and the output names its full path; in any other environment a missing file stops the API at start, naming the path it looked for, so that a wrong path is never a new, empty database. `dotnet run --project src/Starter.Api -- create-database` makes the file, and refuses one that already exists. A changed model means a new file: there are no migrations. Add `LogSql=true` to print each command Plasm sends, and `ClockStart=2030-01-02T09:00:00Z` to start the API's clock at that instant (it runs on from there; unset, it is the system's), to see a rule about time over HTTP.
- **Test:** `dotnet test` (a SQLite file is made for the run, in a temporary folder, and deleted after it; no Docker).
- **Publish natively:** `dotnet publish src/Starter.Api -r linux-arm64` (or your RID), then, in the folder that is to hold the database, `src/Starter.Api/bin/Release/net11.0/linux-arm64/publish/Starter.Api create-database` once, and `ClockStart=2030-01-02T09:00:00Z src/Starter.Api/bin/Release/net11.0/linux-arm64/publish/Starter.Api --urls http://localhost:5080`. SQLite's native library is published beside the binary. The tests publish and run the same way: `dotnet publish tests/Starter.Tests -r linux-arm64`, then `tests/Starter.Tests/bin/Release/net11.0/linux-arm64/publish/Starter.Tests`. Two IL warnings are expected, FSharp.Core's IL2104 and IL3053; any other `warning IL` is a problem. On macOS the linker's `dsymutil` also prints warnings about duplicate debug map objects and a missing `.pcm`: they concern the `.dSYM` debug symbols only.
- **Configure the database:** `src/Starter.Data/Store.fs` opens it from the connection string, and its `provider` function is where its connections are configured: `SqliteOptions` (how long a writer waits for another, the journal mode) and the connection string's own keywords. Nothing else in the project names the database.
<!--#endif -->
<!--#if (STARTER_SWITCH) -->

For both:

<!--#endif -->
- **Make it yours:** replace `Domain.fs` with your records and rules, and `BookingData.fs`'s methods (and its `BookingUnavailableException`) with what your API calls; the C# side changes only where those do. The names that say Starter are the solution (`Starter.slnx`), the projects and their namespaces (`Starter.Data`, `Starter.Api`, `Starter.Tests`), the JSON context `StarterJson` and the connection string `Starter`; `dotnet new plasm-api -n YourName` gives them your name, and in a copy you rename them yourself. The Booking names are this example's domain, which `-n` leaves as they are: rename `BookingData` (its type and `BookingData.fs`), `BookingUnavailableException` and `BookingTests.cs` for yours.
- **Answers:** a value its `create` refuses (`EquipmentName`) answers 400 with the field; a request whose JSON has a property null or missing answers 400 with an empty body, from ASP.NET's binding, before BookingData is called; a refusal of the rules answers 422; a stale copy 409.
