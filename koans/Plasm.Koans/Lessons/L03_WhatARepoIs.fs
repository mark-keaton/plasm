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
              fun k ->
                  task {
                      // A repo (repository) is where Plasm meets the database. It is
                      // made from a model, the record types Plasm can store (Db.model,
                      // built from the schemas in Shop.fs), and a database connection.
                      k.Repo.Model.Types.Length |> shouldBe __

                      // CheckSchema compares the model with the database's tables
                      // and lists every difference it finds.
                      let! issues = k.Repo.CheckSchema()
                      ok issues |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query is a description",
              fun k ->
                  // A query is a value that describes what to read. Building it
                  // sends nothing. k.Commands lists the SQL this koan has sent.
                  let _customers = Query.from Db.customers
                  k.Commands.Length |> shouldBe __
          )

          Koan.Sync(
              "The repo shows its SQL",
              fun k ->
                  // A repo turns a query into SQL. k.Sql shows the SQL it would
                  // send, without sending it.
                  let products = Query.from Db.products
                  let sql = k.Sql products
                  sql.Contains "FROM \"products\"" |> shouldBe __
                  k.Commands.Length |> shouldBe __
          )

          Koan.Task(
              "Running sends that SQL",
              fun k ->
                  task {
                      // The repo sends a query when you run it, with All, One or Count.
                      let products = Query.from Db.products
                      let sql = k.Sql products
                      let! _ = k.Repo.All products
                      k.Commands.Length |> shouldBe __
                      (k.Commands.Head = sql) |> shouldBe __
                  }
          )

          Koan.Task(
              "A query runs each time you ask",
              fun k ->
                  task {
                      // A repo does not remember results: each run reads the
                      // database again. The query itself is unchanged by running it.
                      let products = Query.from Db.products
                      let! first = k.Repo.All products
                      let! second = k.Repo.All products
                      k.Commands.Length |> shouldBe __
                      (ok first = ok second) |> shouldBe __
                  }
          )

          Koan.Task(
              "Every call returns a Result",
              fun k ->
                  task {
                      // Every repo call returns a Result: Ok with what it read, or
                      // Error with what went wrong. One returns an option: the row,
                      // or None. `plasm { ... where ... }` keeps only matching rows.
                      let! orders = k.Repo.Count(Query.from Db.orders)
                      let! zed = k.Repo.One(plasm { for c in Db.customers do where (c.Name = "Zed") })
                      orders |> shouldBe __
                      zed |> shouldBe __
                  }
          )

          Koan.Task(
              "A repo knows only its model",
              fun k ->
                  task {
                      // A model that knows only products. k.RepoWith makes a repo
                      // over it, on the same database.
                      let productsOnly = Model.buildOrFail Db.scalars [ Db.products ]
                      let! customers = (k.RepoWith productsOnly).All(Query.from Db.customers)

                      // It cannot read customers: it returns an Error, it does not throw.
                      match err customers with
                      | DbError.Translation(TranslationError.UnmappedType name) -> name |> shouldBe __
                      | other -> failwithf "expected an unmapped type, got %A" other

                      k.Commands.Length |> shouldBe __
                  }
          ) ]
