/// How an app starts the repo every koan is given. The browser course shows
/// the lines of `start` from the comment above `let! repo` to that line as the
/// Given of repo (Given.repo), so what it shows is code that compiles.
module Plasm.Koans.Starting

open Plasm
open Plasm.Postgres
open Plasm.Koans.Shop

let start (connectionString: string) =
    task {
        // Every koan is given repo: a Plasm Repo over the koans database,
        // inside a sandbox, so whatever the koan changes is undone when it ends.
        // In your app you start one yourself, from your model and a provider;
        // it checks every query in the assembly and the database as it starts:
        let! repo = Repo.Start(Db.model, Postgres.connect connectionString, queriesIn = typeof<Order>.Assembly)
        return repo
    }
