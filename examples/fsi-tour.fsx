// Plasm in F# Interactive: send this file to `dotnet fsi` a submission at a
// time (each ends with ;;), or run `dotnet fsi < fsi-tour.fsx` from this folder.
// Needs `dotnet build -c Release` and PLASM_PG pointing at a database with the
// example schema (the one the tour creates; see tests/Plasm.TestDomain/Database.fs).
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

// A query displays the SQL it would run. Nothing is executed.
let recent =
    plasm {
        for o in Db.orders do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            take 5
            preload o.Lines (plasm { for l in Db.orderLines do preload l.Product })
    };;

// A playground keeps changes across submissions and never commits them.
let pg = repo.Playground();;
pg.Insert(Changeset.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com"; Orders = NotLoaded }) |> wait;;
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
