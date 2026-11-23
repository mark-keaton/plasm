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
                      // It runs queries and writes. There is one repo for the whole
                      // database and all its tables, not one for each table. Every
                      // koan is given one, and whatever a koan changes is undone when
                      // it ends. Count asks how many rows a query matches;
                      // Query.from (tableOf<Customer>) is every row of the customers
                      // table. A count is an int64, a whole number written with an L
                      // after it, such as 7L.
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
                      // made from a model and a database connection. The model is the
                      // whole set: every record type Plasm stores, checked together.
                      // One of its parts is a stored record, such as Customer. Db.model
                      // (under Given) is built from a list of them, where
                      // typeof<Customer> is the Customer record's type. repo.Model is
                      // the model this repo was made from, and its Types are the
                      // record types it stores.
                      // List.contains x xs is true when the list xs holds x.
                      repo.Model.Types |> List.contains typeof<Product> |> shouldBe __

                      // CheckSchema compares the model with the database's tables. It
                      // answers with a list: one SchemaIssue for each difference it
                      // finds (lesson 22 reads one). The koans database was made to
                      // fit Db.model, so count the differences.
                      let! issues = repo.CheckSchema()
                      (ok issues).Length |> shouldBe __
                  }
          )

          Koan.Task(
              "In your app, you start the repo",
              fun repo ->
                  task {
                      // Every koan is given its repo. An app starts its own, once, as
                      // the app starts: one repo for the whole database. This koan
                      // starts a second repo the way an app would, to show how.
                      // Repo.Start is given three things:
                      // - the model, Db.model (from Shop.fs);
                      // - a provider, which is a value: the object a repo talks to
                      //   one database through. In your app you get one by calling a
                      //   function with your connection string:
                      //   Postgres.connect connectionString. Here repo.Provider is the
                      //   provider the koan's repo was started with, a value of the
                      //   same kind;
                      // - queriesIn, the compiled project (an assembly) whose queries
                      //   it checks. typeof<Order>.Assembly is the one Order is
                      //   defined in, the shop's; any of the shop's types names the
                      //   same one, and it does not make this a repo for orders.
                      // Repo.Start checks the new repo as it makes it: every query in
                      // that assembly can be turned into SQL and the database fits
                      // the model, or start-up fails. The shop holds a query written
                      // wrong on purpose (lesson 22), so this repo writes its query
                      // check as Ignore: a repo that is not checked always says so.
                      let! appRepo =
                          Repo.Start(
                              Db.model,
                              repo.Provider,
                              queriesIn = typeof<Order>.Assembly,
                              options = { RepoOptions.Default with Queries = StartupCheck.Ignore }
                          )

                      // The new repo reads any table of the same database.
                      let! customers = appRepo.Count(Query.from (tableOf<Customer>))
                      ok customers |> shouldBe __
                  }
          )

          Koan.Task(
              "sqlSent () is the SQL a koan has sent",
              fun repo ->
                  task {
                      // sqlSent is a function, one of the koans' test helpers. It needs
                      // no input, and F# writes that as one argument, (), called unit:
                      // sqlSent alone names the function, and sqlSent () calls it. Its
                      // answer is the SQL this koan has sent to the database so far, in
                      // order, as a list of strings. It is called each time because
                      // that list grows. Every koan starts with none, and a list with
                      // nothing in it is written [].
                      // Written with no space, sqlSent().Length is the same call
                      // followed by .Length, how many the list holds. Counting them
                      // shows when Plasm talks to the database.
                      // `let! _ =` waits for the Count and gives its result no name:
                      // _ stands for a value the koan does not read.
                      // (In your app there is no sqlSent: Plasm reports each command
                      // it sends to a function you give it, RepoOptions.OnCommand.)
                      sqlSent () |> shouldBe __
                      let! _ = repo.Count(Query.from (tableOf<Customer>))
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query is a description",
              fun repo ->
                  // A query is a value that describes what to read. Building it
                  // sends nothing.
                  let _customers = Query.from (tableOf<Customer>)
                  sqlSent().Length |> shouldBe __
          )

          Koan.Sync(
              "The repo shows its SQL",
              fun repo ->
                  // A repo turns a query into SQL. repo.Explain shows its plan
                  // without sending anything; the plan's Sql is the SQL it would send.
                  let products = Query.from (tableOf<Product>)
                  let sql = (ok (repo.Explain products)).Sql
                  sql.Contains "FROM \"products\"" |> shouldBe __
                  sqlSent().Length |> shouldBe __
          )

          Koan.Task(
              "Running sends that SQL",
              fun repo ->
                  task {
                      // The repo sends a query when you run it, with All, One or Count.
                      let products = Query.from (tableOf<Product>)
                      let sql = (ok (repo.Explain products)).Sql
                      let! _ = repo.All products
                      sqlSent().Length |> shouldBe __
                      (sqlSent().Head = sql) |> shouldBe __
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
                      sqlSent().Length |> shouldBe __
                      (ok first = ok second) |> shouldBe __
                  }
          )

          Koan.Task(
              "Every call returns a Result",
              fun repo ->
                  task {
                      // Every repo call returns a Result: Ok with what it read, or
                      // Error with what went wrong. One returns an option: the row,
                      // or None. `plasm { for c in tableOf<Customer> do where (...) }` is
                      // a query written out: c stands for a row of the customers table,
                      // and where keeps only the rows its condition holds for (lesson 06
                      // goes deeper).
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
                      // productsOnly is a model built from a list of one stored record,
                      // Product (the empty list before it is for settings the shop does
                      // not need). narrow is a repo over that model, on the same database.
                      // Repo.Start checks two things as it starts a repo: the shop's
                      // queries, and the database against the model. Checked, narrow
                      // would refuse every shop query that reads customers, so it is
                      // started unchecked: RepoOptions.Unchecked writes both of those
                      // checks as Ignore. queriesIn = ... and options = ... are named
                      // arguments: each says which of Repo.Start's inputs it is.
                      // sent is a ResizeArray, a list that can grow. narrow is given
                      // sent.Add as its OnCommand, so every command it sends is added to
                      // sent: sqlSent () sees only what the koan's own repo sends.
                      let sent = ResizeArray<CommandEvent>()
                      let productsOnly = Model.buildOrFail [] [ typeof<Product> ]
                      let! narrow = Repo.Start(productsOnly, repo.Provider, queriesIn = typeof<Order>.Assembly, options = { RepoOptions.Unchecked with OnCommand = Some sent.Add })
                      let! customers = narrow.All(Query.from (tableOf<Customer>))

                      // It cannot read customers: it returns an Error, it does not throw.
                      // A read's Error is a DbError; DbError.Translation is a query Plasm
                      // could not turn into SQL, and UnmappedType names the record by its
                      // type's short name.
                      // match looks at a value and takes the first line, each starting
                      // with |, whose shape fits it, naming its parts: here name. The
                      // last line fits anything else, and failwithf fails the koan with
                      // a message (%A prints the value).
                      match err customers with
                      | DbError.Translation(TranslationError.UnmappedType name) -> name |> shouldBe __
                      | other -> failwithf "expected an unmapped type, got %A" other

                      sent.Count |> shouldBe __
                  }
          ) ]
