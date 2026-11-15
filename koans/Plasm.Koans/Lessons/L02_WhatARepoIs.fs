/// Lesson 02: What a repo is.
module Plasm.Koans.Lessons.L02_WhatARepoIs

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "What a repo is"
        [ Koan.Task(
              "Every koan is given a repo",
              fun repo ->
                  task {
                      // repo is a Repo (a repository): Plasm's way in to a database.
                      // It runs queries and writes. Every koan is given one, and
                      // whatever a koan changes is undone when it ends. Count asks how
                      // many rows a query matches; Query.from (tableOf<Customer>) is every
                      // row of the customers table.
                      let! customers = repo.Count(Query.from (tableOf<Customer>))
                      customers |> shouldBe (Ok __)
                  }
          )

          Koan.Task(
              "ok opens a Result",
              fun repo ->
                  task {
                      // A repo answers with a Result: Ok with the answer, or Error
                      // saying what went wrong. ok (under Given) takes the value out
                      // of an Ok; given an Error it fails the koan and shows the error.
                      let! orders = repo.Count(Query.from (tableOf<Order>))
                      orders |> shouldBe (Ok 4L)
                      ok orders |> shouldBe __
                  }
          )

          Koan.Task(
              "A repo pairs a model with a database",
              fun repo ->
                  task {
                      // A repo (repository) is where Plasm meets the database. It is
                      // made from a model, the record types Plasm can store (Db.model,
                      // built from the records in Shop.fs), and a database connection.
                      repo.Model.Types.Length |> shouldBe __

                      // CheckSchema compares the model with the database's tables
                      // and lists every difference it finds.
                      let! issues = repo.CheckSchema()
                      ok issues |> shouldBe __
                  }
          )

          Koan.Task(
              "In your app, you make the repo",
              fun repo ->
                  task {
                      // A Repo is made from a model, the record types it can store
                      // (Db.model, from Shop.fs), and a provider, the connection to
                      // the database. In your app the provider is
                      // Postgres.connect connectionString; here it is the koans' own,
                      // repo.Provider. Both repos read the same database.
                      let mine = Repo(Db.model, repo.Provider)
                      let! customers = mine.Count(Query.from (tableOf<Customer>))
                      ok customers |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query is a description",
              fun repo ->
                  // A query is a value that describes what to read. Building it
                  // sends nothing. commands () lists the SQL this koan has sent.
                  let _customers = Query.from (tableOf<Customer>)
                  (commands ()).Length |> shouldBe __
          )

          Koan.Sync(
              "The repo shows its SQL",
              fun repo ->
                  // A repo turns a query into SQL. repo.Explain shows its plan
                  // without sending anything; the plan's Sql is the SQL it would send.
                  let products = Query.from (tableOf<Product>)
                  let sql = (ok (repo.Explain products)).Sql
                  sql.Contains "FROM \"products\"" |> shouldBe __
                  (commands ()).Length |> shouldBe __
          )

          Koan.Task(
              "Running sends that SQL",
              fun repo ->
                  task {
                      // The repo sends a query when you run it, with All, One or Count.
                      let products = Query.from (tableOf<Product>)
                      let sql = (ok (repo.Explain products)).Sql
                      let! _ = repo.All products
                      (commands ()).Length |> shouldBe __
                      ((commands ()).Head = sql) |> shouldBe __
                  }
          )

          Koan.Task(
              "A query runs each time you ask",
              fun repo ->
                  task {
                      // A repo does not remember results: each run reads the
                      // database again. The query itself is unchanged by running it.
                      let products = Query.from (tableOf<Product>)
                      let! first = repo.All products
                      let! second = repo.All products
                      (commands ()).Length |> shouldBe __
                      (ok first = ok second) |> shouldBe __
                  }
          )

          Koan.Task(
              "Every call returns a Result",
              fun repo ->
                  task {
                      // Every repo call returns a Result: Ok with what it read, or
                      // Error with what went wrong. One returns an option: the row,
                      // or None. `plasm { ... where ... }` keeps only matching rows.
                      let! orders = repo.Count(Query.from (tableOf<Order>))
                      let! zed = repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Zed") })
                      orders |> shouldBe __
                      zed |> shouldBe __
                  }
          )

          Koan.Task(
              "A repo knows only its model",
              fun repo ->
                  task {
                      // A model that knows only products, and a repo over it on the
                      // same database: Repo(model, provider), as in your app.
                      let productsOnly = Model.buildOrFail [] [ typeof<Product> ]
                      let! customers = Repo(productsOnly, repo.Provider).All(Query.from (tableOf<Customer>))

                      // It cannot read customers: it returns an Error, it does not throw.
                      match err customers with
                      | DbError.Translation(TranslationError.UnmappedType name) -> name |> shouldBe __
                      | other -> failwithf "expected an unmapped type, got %A" other

                      (commands ()).Length |> shouldBe __
                  }
          ) ]
