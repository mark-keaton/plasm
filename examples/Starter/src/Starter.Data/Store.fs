/// Where BookingData's database is opened: the one file that knows which
/// database it is, and the place to configure its connections.
module internal Starter.Data.Store

#if STARTER_SQLITE
open System.IO
open Microsoft.Data.Sqlite
open Plasm
open Plasm.Sqlite

/// The provider BookingData opens, from the connection string. Configure it
/// here: SqliteOptions (how long a writer waits for another, the journal mode)
/// and the connection string's own keywords (Microsoft.Data.Sqlite's).
let provider (connectionString: string) : IProvider =
    Sqlite.connectWith SqliteOptions.Default connectionString

/// A provider that reaches no file, for printing the DDL.
let printing () : IProvider = Sqlite.connect "Data Source=:memory:"

/// The database's file, its full path: a relative Data Source is the working
/// directory's, as SQLite opens it.
let file (connectionString: string) : string =
    Path.GetFullPath(SqliteConnectionStringBuilder(connectionString).DataSource)
#else
open Npgsql
open Plasm
open Plasm.Postgres

/// The data source BookingData opens, from the connection string. Configure
/// it here, on NpgsqlDataSourceBuilder: a password provider, SSL, a pooler's
/// settings, logging.
let dataSource (connectionString: string) : NpgsqlDataSource =
    NpgsqlDataSourceBuilder(connectionString).Build()

let provider (connectionString: string) : IProvider =
    PostgresProvider(dataSource connectionString)

/// A provider that reaches no database, for printing the DDL.
let printing () : IProvider = Postgres.connect "Host=localhost"
#endif
