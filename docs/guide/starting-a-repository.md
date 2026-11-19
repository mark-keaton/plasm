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

`OnCommand` is called with each command sent: its SQL, its duration and its
row count.

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

`RepoOptions.Unchecked` skips both. Kanban's facade lets its host choose,
because a Lambda function may prefer a shorter init once its scenarios have
run the check:

<!-- from: examples/Kanban/src/Data/Facade.fs -->
```fsharp
            let checking =
                match checks with
                | StartupChecks.Run -> RepoOptions.Default
                | StartupChecks.Skip -> RepoOptions.Unchecked
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
