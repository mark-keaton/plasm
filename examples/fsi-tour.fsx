// Plasm in F# Interactive: send this file to `dotnet fsi` a submission at a
// time (each ends with ;;), or run `dotnet fsi < fsi-tour.fsx` from this folder.
// Needs `dotnet build -c Release` and PLASM_PG pointing at a database built
// from the DDL the first submission prints (the tables of Plasm.Examples/Model.fs).
// Once the packages are published, the two #r lines become #r "nuget: Plasm.Postgres".
#r "../src/Plasm.Postgres/bin/Release/net11.0/Plasm.dll"
#r "../src/Plasm.Postgres/bin/Release/net11.0/Plasm.Postgres.dll"
#r "nuget: Npgsql, 10.0.3"
#load "Plasm.Examples/Model.fs"
open Plasm
open Plasm.Postgres
open Plasm.Interactive
open Shop.Model

let repo = Repo(Db.model, Postgres.connect (System.Environment.GetEnvironmentVariable "PLASM_PG"));;

// The tables the model expects. Plasm prints them; it never runs them.
printfn "%s" (repo.Ddl());;

// A query displays the SQL it would run, with the command that reads the
// orders' lines. Nothing is executed.
let recent =
    plasm {
        for o in Db.orders do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            take 5
    };;

// A playground keeps changes across submissions and never commits them.
let pg = repo.Playground();;
pg.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }: Tx<Customer, unit>) |> wait;;
pg.All(plasm { for c in Db.customers do orderBy c.Name }) |> wait;;
pg.Reset();;
pg.All(plasm { for c in Db.customers do orderBy c.Name }) |> wait;;

// Generate a record and schema from a table.
repo.Scaffold "payments" |> wait;;

// Redefine the model (re-send Model.fs) and the earlier query explains itself.
#load "Plasm.Examples/Model.fs"
open Shop.Model
let repo = Repo(Db.model, Postgres.connect (System.Environment.GetEnvironmentVariable "PLASM_PG"));;
recent;;
