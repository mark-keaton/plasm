# Starting a repository

The repository is the only place anything touches the database. It is
started once, and checked as it starts.

## Start

`Repo.Start` takes the model, a provider and the assembly whose query values
it should check. It translates every query value that assembly defines and
compares the model with the database. By default either problem fails
start-up, naming every one.

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let! repo =
            Repo.Start(
                Db.model,
                Postgres.connect connectionString,
                queriesIn = typeof<Order>.Assembly,
                options =
                    { RepoOptions.Default with
                        OnCommand = Some(fun e -> printfn "   [sql %4.1fms, %d rows] %s" e.Duration.TotalMilliseconds e.Rows (e.Sql.Substring(0, min 100 e.Sql.Length))) }
            )
```

It then asks the database to describe the SQL of every statement those
values make, their loads, bulk writes and hand-written statements included.
The database parses each and runs none, so a misspelled column in your own
SQL fails start-up, not a request. Statements are described only when the
schema check found the tables as the model expects them.

`OnCommand` is called with each command sent: its SQL, its duration and its
row count. A transaction's own commands are reported too, BEGIN with its
isolation level, COMMIT, ROLLBACK and a sandbox's savepoints, each with a
`Kind` of `Begin`, `Commit` or `Rollback`; a statement's `Kind` is `Statement`,
so count statements by that, not by their SQL. The same events are
OpenTelemetry activities of the ActivitySource "Plasm", named by the
operation and table ("SELECT customers") and tagged by the database
conventions, whenever something listens to it.

## Turning a check down

`RepoOptions.Queries` and `RepoOptions.Schema` turn each check down to `Warn`
or `Ignore`. The tour does so once, to print the DDL before the tables exist:

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let! printer =
            Repo.Start(
                Db.model,
                Postgres.connect db.ConnectionString,
                queriesIn = typeof<Order>.Assembly,
                options = { RepoOptions.Default with Queries = StartupCheck.Ignore; Schema = StartupCheck.Ignore }
            )

        let ddl = printer.Ddl()
```

`RepoOptions.Unchecked` skips both. Kanban's Lambda function chooses, since
a function may prefer a shorter init once its scenarios have run the check.
Plasm.CSharp's build writes both openings from the data project's Store,
`OpenAsync` and `OpenUncheckedAsync`:

<!-- from: examples/Kanban/src/Lambda/Program.cs -->
```csharp
var kanban = check
    ? await Boards.OpenAsync(connectionString, TimeProvider.System, CancellationToken.None)
    : await Boards.OpenUncheckedAsync(connectionString, TimeProvider.System, CancellationToken.None);
```

## The tables

Plasm has no migrations. `repo.Ddl()` prints the tables, constraints and
indexes the model expects; Plasm never runs them. Keep your migration tool,
and let the start-up check tell you when the database and the model differ.

A database can change under a running application, so the check can be asked
again:

<!-- from: examples/Kanban/src/Server/Database.fs -->
```fsharp
let differences (repo: Repo) : Task<string list> =
    task {
        match! repo.CheckSchema() with
        | Ok issues -> return issues |> List.map string
        | Error e -> return [ string e ]
    }
```

## From C#

A C# host does not see the repository. It opens the application's F# facade,
which starts the repository inside. [From C#](../csharp.md) shows the facade,
and [Native AOT and Lambda](../aot.md) what starting costs in a native binary.
