/// Real PostgreSQL for the scenarios: one server per test run (a container,
/// or the server PLASM_TEST_PG names), and databases created on it as needed.
module Kanban.Specs.Databases

open System
open System.Threading.Tasks
open Npgsql
open Plasm
open Kanban.Server

let private admin: Lazy<Task<string>> =
    lazy
        (task {
            match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
            | null
            | "" ->
                let container = Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4").Build()
                do! container.StartAsync()
                return container.GetConnectionString()
            | connectionString -> return connectionString
        })

let execute (connectionString: string) (sql: string) : Task =
    task {
        use connection = new NpgsqlConnection(connectionString)
        do! connection.OpenAsync()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        let! _ = command.ExecuteNonQueryAsync()
        return ()
    }

/// A new, empty database; its connection string.
let empty () : Task<string> =
    task {
        let! admin = admin.Value
        let name = "kanban_" + Guid.NewGuid().ToString "N"
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
        options = { RepoOptions.Default with OnCommand = Some(fun command -> commands.Enqueue command.Sql) }
    )

/// The database most scenarios share. Each works inside a sandbox, so
/// nothing any of them writes is ever committed to it.
let shared: Lazy<Task<Repo>> =
    lazy
        (task {
            let! connectionString = migrated ()
            return! repoFor connectionString
        })
