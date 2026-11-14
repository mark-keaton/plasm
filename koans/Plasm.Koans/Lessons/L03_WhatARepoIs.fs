/// Lesson 03: What a repo is.
module Plasm.Koans.Lessons.L03_WhatARepoIs

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "What a repo is"
        [ Koan.Task(
              "A repo pairs a model with a database",
              fun repo ->
                  task {
                      // A repo (repository) is where Plasm meets the database. It is
                      // made from a model, the record types Plasm can store (Db.model,
                      // built from the schemas in Shop.fs), and a database connection.
                      repo.Model.Types.Length |> shouldBe __

                      // CheckSchema compares the model with the database's tables
                      // and lists every difference it finds.
                      let! issues = repo.CheckSchema()
                      ok issues |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query is a description",
              fun repo ->
                  // A query is a value that describes what to read. Building it
                  // sends nothing. commands () lists the SQL this koan has sent.
                  let _customers = Query.from Db.customers
                  (commands ()).Length |> shouldBe __
          )

          Koan.Sync(
              "The repo shows its SQL",
              fun repo ->
                  // A repo turns a query into SQL. repo.Explain shows its plan
                  // without sending anything; the plan's Sql is the SQL it would send.
                  let products = Query.from Db.products
                  let sql = (ok (repo.Explain products)).Sql
                  sql.Contains "FROM \"products\"" |> shouldBe __
                  (commands ()).Length |> shouldBe __
          )

          Koan.Task(
              "Running sends that SQL",
              fun repo ->
                  task {
                      // The repo sends a query when you run it, with All, One or Count.
                      let products = Query.from Db.products
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
                      let products = Query.from Db.products
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
                      let! orders = repo.Count(Query.from Db.orders)
                      let! zed = repo.One(plasm { for c in Db.customers do where (c.Name = "Zed") })
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
                      let productsOnly = Model.buildOrFail Db.scalars [ Db.products ]
                      let! customers = Repo(productsOnly, repo.Provider).All(Query.from Db.customers)

                      // It cannot read customers: it returns an Error, it does not throw.
                      match err customers with
                      | DbError.Translation(TranslationError.UnmappedType name) -> name |> shouldBe __
                      | other -> failwithf "expected an unmapped type, got %A" other

                      (commands ()).Length |> shouldBe __
                  }
          ) ]
