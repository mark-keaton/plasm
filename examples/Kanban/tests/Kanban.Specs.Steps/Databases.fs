/// Real PostgreSQL for the scenarios: one server per test run (a container,
/// or the server PLASM_TEST_PG names), and databases created on it as needed.
/// Each is named with this machine and this test process's id, and dropped
/// when its scenario, or for the shared one the run, ends. One a killed run
/// left is dropped by the next run on the same machine, whose first database
/// finds that process gone; a database another machine made is never dropped
/// here, and a process id since reused only delays a drop.
module Kanban.Specs.Databases

open System
open System.Diagnostics
open System.Text.RegularExpressions
open System.Threading.Tasks
open Npgsql
open Plasm
open Kanban.Server

let execute (connectionString: string) (sql: string) : Task =
    task {
        use connection = new NpgsqlConnection(connectionString)
        do! connection.OpenAsync()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        let! _ = command.ExecuteNonQueryAsync()
        return ()
    }

/// This machine, as a database name may hold it: the first 8 hex digits of a
/// hash of its name.
let private machine =
    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant()))).[..7]

let private ours = Regex @"^kanban_([0-9a-f]{8})_(\d+)_[0-9a-f]{32}$"

let private running (pid: int) =
    try
        use p = Process.GetProcessById pid
        not p.HasExited
    with :? ArgumentException ->
        false

/// The databases of test runs on this machine whose process is gone. Alive
/// means kept, so a reused process id never causes a wrong drop.
let private dropOrphans (admin: string) : Task =
    task {
        let names = ResizeArray()

        do!
            task {
                use connection = new NpgsqlConnection(admin)
                do! connection.OpenAsync()
                use read = connection.CreateCommand()
                read.CommandText <- "select datname from pg_database where datname like 'kanban\\_%'"
                use! rows = read.ExecuteReaderAsync()

                while rows.Read() do
                    names.Add(rows.GetString 0)
            }

        for name in names do
            let m = ours.Match name

            if m.Success && m.Groups.[1].Value = machine && not (running (int m.Groups.[2].Value)) then
                do! execute admin $"drop database if exists {name} with (force)"
    }

let private admin: Lazy<Task<string>> =
    lazy
        (task {
            match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
            | null
            | "" ->
                let container = Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4").Build()
                do! container.StartAsync()
                return container.GetConnectionString()
            | connectionString ->
                do! dropOrphans connectionString
                return connectionString
        })

/// Drops a database `empty` made, closing whatever connection is still open to it.
let drop (connectionString: string) : Task =
    task {
        let name = NpgsqlConnectionStringBuilder(connectionString).Database

        if not (ours.IsMatch name) then
            invalidArg "connectionString" $"{name} is not a scenario's database"

        let! admin = admin.Value
        do! execute admin $"drop database if exists {name} with (force)"
    }

/// A new, empty database; its connection string.
let empty () : Task<string> =
    task {
        let! admin = admin.Value
        let name = $"kanban_{machine}_{Environment.ProcessId}_{Guid.NewGuid():N}"
        do! execute admin $"create database {name}"
        let builder = NpgsqlConnectionStringBuilder admin
        builder.Database <- name
        return builder.ConnectionString
    }

/// A new database with the application's migrations applied.
let migrated () : Task<string> =
    task {
        let! connectionString = empty ()

        match Database.migrate connectionString with
        | Ok() -> return connectionString
        | Error message -> return failwith $"migrating a test database failed: {message}"
    }

/// The SQL of every command the scenarios' repositories have sent, in order.
let commands = System.Collections.Concurrent.ConcurrentQueue<string>()

/// A repository started as the application starts its own: every query the
/// server declares is translated and the tables are compared with the model,
/// and a problem with either stops the scenario here.
let repoFor (connectionString: string) : Task<Repo> =
    Repo.Start(
        Db.model,
        Plasm.Postgres.PostgresProvider(NpgsqlDataSource.Create connectionString),
        queriesIn = typeof<Kanban.Server.Workflows.Refusal>.Assembly,
        options = { RepoOptions.Default with OnCommand = Some(fun command -> if command.Kind = CommandKind.Statement then commands.Enqueue command.Sql) }
    )

/// The database most scenarios share, and its connection string. Each works
/// inside a sandbox, so nothing any of them writes is ever committed to it.
let shared: Lazy<Task<string * Repo>> =
    lazy
        (task {
            let! connectionString = migrated ()
            let! repo = repoFor connectionString
            return connectionString, repo
        })
