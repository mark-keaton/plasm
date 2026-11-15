/// The database's side of start-up: migrations, the repository, and the check
/// that the two agree.
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
let repo (dataSource: NpgsqlDataSource) (log: ILogger) : Repo =
    let options =
        { RepoOptions.Default with
            OnCommand =
                Some(fun command ->
                    log.LogDebug("{Milliseconds:0.0} ms, {Rows} rows: {Sql}", command.Duration.TotalMilliseconds, command.Rows, command.Sql)) }

    Repo(Db.model, PostgresProvider dataSource, options)

/// Every difference between the tables and `Db.model` that would make a read
/// or a write fail; empty when they fit.
let differences (repo: Repo) : Task<string list> =
    task {
        match! repo.CheckSchema() with
        | Ok issues -> return issues |> List.map string
        | Error e -> return [ string e ]
    }

/// Every query the application declares (`Workflows.Queries`; all of them are
/// values) that Plasm cannot translate, as "name: reason"; empty when all can
/// be. Nothing is sent to the database.
let untranslatable (repo: Repo) : string list =
    repo.CheckQueries(typeof<Marker>.Assembly, "Kanban.Server")
    |> List.map (fun problem -> $"{problem.Query}: {problem.Error}")
