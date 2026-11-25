/// Where a data layer's data lives, given once in the data project: what only
/// it knows (its model, how its database is reached, where its queries are,
/// and how its data type is made over a repository). Plasm does the rest the
/// same in every project: open with the start-up check, print the DDL, make a
/// database where one is a file, run a test in a sandbox. The seam finds the
/// project's Store and gives C# these as the data type's own members, and an
/// AddXxx registration for ASP.NET.
namespace Plasm.CSharp

open System
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Plasm

/// A data type opened over its repository: both, for the sandbox.
type Opened<'Data> = { Repo: Repo; Data: 'Data }

/// A data layer's database, as its project reaches it.
type Store<'Data> =
    { /// The model: every stored record type.
      Model: Model
      /// The provider for a connection string, configured as the project wants.
      Provider: string -> IProvider
      /// A provider that reaches no database, for the DDL.
      Printing: unit -> IProvider
      /// The assembly that holds the project's queries, which opening checks.
      Queries: Assembly
      /// The data type over a repository, and the application's one clock.
      Make: Repo -> TimeProvider -> 'Data }

    /// Starts Plasm's repository over the database `connectionString` names.
    /// It checks every query and the tables first, so one that would fail
    /// throws here, before anything is served. `onSql`, when given, is given
    /// the SQL of each command sent, BEGIN and COMMIT included.
    member store.OpenAsync(connectionString: string, time: TimeProvider, onSql: Action<string> | null, cancellationToken: CancellationToken) : Task<Opened<'Data>> =
        task {
            let options =
                match onSql with
                | null -> RepoOptions.Default
                | onSql -> { RepoOptions.Default with OnCommand = Some(fun e -> onSql.Invoke e.Sql) }

            let! repo = Repo.Start(store.Model, store.Provider connectionString, store.Queries, options, cancellationToken)
            return { Repo = repo; Data = store.Make repo time }
        }

    /// Starts Plasm's repository without the start-up check: a query or table
    /// that would fail fails when it is used. For a host whose tests open it
    /// with the check over the same migrations, and that cannot spend the
    /// check at each start (a Lambda function's cold start).
    member store.OpenUncheckedAsync(connectionString: string, time: TimeProvider, cancellationToken: CancellationToken) : Task<Opened<'Data>> =
        task {
            let! repo = Repo.Start(store.Model, store.Provider connectionString, store.Queries, RepoOptions.Unchecked, cancellationToken)
            return { Repo = repo; Data = store.Make repo time }
        }

    /// The SQL that creates the tables.
    member store.Ddl() : string =
        Repo.Start(store.Model, store.Printing(), store.Queries, RepoOptions.Unchecked).Result.Ddl()

    /// `body` over the data type on a repository whose every change is rolled
    /// back when it ends: one connection, one transaction, never committed.
    member store.SandboxAsync(repo: Repo, time: TimeProvider, body: Func<Opened<'Data>, Task<'T>>, cancellationToken: CancellationToken) : Task<'T> =
        repo.Sandbox((fun sandboxed -> body.Invoke { Repo = sandboxed; Data = store.Make sandboxed time }), cancellationToken)

/// A data layer whose database is a file (SQLite): its Store, and the file a
/// connection string names, which can be made with the tables.
type FileStore<'Data> =
    { Store: Store<'Data>
      /// The database's file, its full path, for a connection string.
      File: string -> string }

    /// The file `connectionString` names, its full path.
    member store.DatabaseFile(connectionString: string) : string = store.File connectionString

    /// Makes the database: the file `connectionString` names, with the tables
    /// the DDL creates. A file already there is never changed: IOException.
    member store.CreateDatabaseAsync(connectionString: string, cancellationToken: CancellationToken) : Task =
        task {
            let file = store.File connectionString

            if File.Exists file then
                raise (IOException $"{file} exists: a database is made only where there is none")

            use! connection = (store.Store.Provider connectionString).OpenConnection cancellationToken
            use command = connection.CreateCommand()
            command.CommandText <- store.Store.Ddl()
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            ()
        }
        :> Task
