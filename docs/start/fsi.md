# In F# Interactive

Plasm works a submission at a time. No setup call is needed: a query displays
the SQL it would run, and a playground keeps changes across submissions
without ever committing them.

[`examples/fsi-tour.fsx`](../../examples/fsi-tour.fsx) is the session this
page is taken from. Send it to `dotnet fsi` a submission at a time. It needs
`dotnet build -c Release` and `PLASM_PG` pointing at a database built from the
DDL its first submission prints. It references the assemblies this
repository builds; in a script of your own,
`#r "nuget: Plasm.Postgres, 0.1.0-alpha.2"` takes their place.

## Start unchecked

F# Interactive defines its queries as it goes, so at start-up there is
nothing to check.

<!-- from: examples/fsi-tour.fsx -->
```fsharp
open Plasm
open Plasm.Postgres
open Plasm.Interactive
open Shop.Model
// ...
let repo =
    Repo.Start(Db.model, Postgres.connect (System.Environment.GetEnvironmentVariable "PLASM_PG"), queriesIn = typeof<Customer>.Assembly, options = RepoOptions.Unchecked)
    |> wait;;
```

`wait` shows a result, not a task.

## Look before you run

<!-- from: examples/fsi-tour.fsx -->
```fsharp
// The tables the model expects. Plasm prints them; it never runs them.
printfn "%s" (repo.Ddl());;

// A query displays the SQL it would run, with the command that reads the
// orders' lines. Nothing is executed.
let recent =
    plasm {
        for o in tableOf<Order> do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            take 5
    };;
```

## A playground

<!-- from: examples/fsi-tour.fsx -->
```fsharp
// A playground keeps changes across submissions and never commits them.
let pg = repo.Playground();;
pg.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }: Tx<Customer, unit>) |> wait;;
pg.All(plasm { for c in tableOf<Customer> do orderBy c.Name }) |> wait;;
pg.Reset();;
```

## A record for an existing table

<!-- from: examples/fsi-tour.fsx -->
```fsharp
// Generate a record that says how an existing table stores it.
repo.Scaffold "payments" |> wait;;
```

A query built before you redefined one of its types explains that it must be
re-evaluated, instead of failing with an unmapped type.
