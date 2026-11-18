/// The database's side of start-up: migrations, and the repository, which
/// Plasm checks against the database and the application's queries as it starts.
module Kanban.Server.Database

open System.Threading.Tasks
open DbUp
open Microsoft.Extensions.Logging
open Npgsql
open Plasm
open Plasm.Postgres

type private Marker = class end

/// Applies the SQL scripts in migrations/ that this database has not seen
/// (DbUp records them in its own table). Plasm creates and alters nothing.
let migrate (connectionString: string) : Result<unit, string> =
    let upgrader =
        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof<Marker>.Assembly)
            .LogToNowhere()
            .Build()

    let result = upgrader.PerformUpgrade()
    if result.Successful then Ok() else Error result.Error.Message

/// The application's one repository. Each call takes a pooled connection from
/// the data source for as long as the call lasts.
///
/// Starting it is the start-up check: Plasm translates every query value in
/// Kanban.Data, the library that holds them (`Workflows.Queries`; all of them
/// are values), and compares
/// `Db.model` with the tables. Both are left at their default, which is to
/// raise `PlasmStartupException` listing every problem, so the application
/// does not serve with a query or a table that would fail on first use.
let start (dataSource: NpgsqlDataSource) (log: ILogger) : Task<Repo> =
    let options =
        { RepoOptions.Default with
            OnCommand =
                Some(fun command ->
                    log.LogDebug("{Milliseconds:0.0} ms, {Rows} rows: {Sql}", command.Duration.TotalMilliseconds, command.Rows, command.Sql)) }

    Repo.Start(Db.model, PostgresProvider dataSource, queriesIn = typeof<Workflows.Refusal>.Assembly, options = options)

/// Every difference between the tables and `Db.model` that would make a read
/// or a write fail; empty when they fit. Start-up has already checked this
/// once; `/health` asks again, since a database can change under a running
/// application.
let differences (repo: Repo) : Task<string list> =
    task {
        match! repo.CheckSchema() with
        | Ok issues -> return issues |> List.map string
        | Error e -> return [ string e ]
    }
