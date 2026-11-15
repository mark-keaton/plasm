/// Lesson 06: About query values.
module Plasm.Koans.Lessons.L06_QueryValues

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About query values"
        [ Koan.Task(
              "Query.from reads every row",
              fun repo ->
                  task {
                      // A query describes which rows to read. Query.from is the plainest
                      // query there is: every row of a schema's table, as it is.
                      // repo.All runs a query and returns all the rows it matches.
                      let! customers = repo.All(Query.from Db.customers)
                      // How many customers does the koans database start with?
                      (ok customers).Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A where keeps the matching rows",
              fun repo ->
                  task {
                      // plasm { } writes a query step by step. `for o in Db.orders do` names
                      // each Order o; `where` keeps the rows for which its condition is true.
                      let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                      let! orders = repo.All drafts
                      // How many Orders are drafts?
                      (ok orders).Length |> shouldBe __
                  }
          )

          Koan.Task(
              "An orderBy sorts the rows",
              fun repo ->
                  task {
                      // orderBy sorts the rows by a field, smallest first (for text: A to Z).
                      // The database does the sorting.
                      let byName = plasm { for p in Db.products do orderBy p.Name }
                      let! products = repo.All byName
                      ok products |> List.map (fun p -> p.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "take keeps the first rows",
              fun repo ->
                  task {
                      // take n keeps the first n rows. "First" needs an order, so the
                      // query sorts the Customers by name before taking two.
                      let firstTwo = plasm { for c in Db.customers do orderBy c.Name; take 2 }
                      let! customers = repo.All firstTwo
                      ok customers |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Sync(
              "Building a query runs nothing",
              fun repo ->
                  let _published = plasm { for o in Db.orders do where (o.Status = Published) }
                  // How many commands has this koan sent?
                  (commands ()).Length |> shouldBe __
          )

          Koan.Task(
              "Extending makes a new query",
              fun repo ->
                  task {
                      let all = Query.from Db.orders
                      let published = plasm { for o in all do where (o.Status = Published) }
                      let! everyOrder = repo.Count all
                      let! publishedOrders = repo.Count published
                      ok everyOrder |> shouldBe __
                      ok publishedOrders |> shouldBe __
                  }
          )

          Koan.Task(
              "Running does not change a query",
              fun repo ->
                  task {
                      let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                      let! first = repo.All drafts
                      let! second = repo.All drafts
                      (ok first = ok second) |> shouldBe __
                      (ok first).Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query can explain itself",
              fun repo ->
                  let published = plasm { for o in Db.orders do where (o.Status = Published) }
                  let sql = (ok (repo.Explain published)).Sql
                  // Is the status sent as a parameter?
                  sql.Contains "\"status\" = @p0" |> shouldBe __
                  (commands ()).Length |> shouldBe __
          )

          Koan.Task(
              "Reusable filters are functions",
              fun repo ->
                  task {
                      let published (q: Query<Order>) = plasm { for o in q do where (o.Status = Published) }
                      let over (amount: decimal) (q: Query<Order>) = plasm { for o in q do where (o.Total > amount) }
                      let! n = repo.Count(Query.from Db.orders |> published |> over 50m)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Captured values become parameters",
              fun repo ->
                  task {
                      let bobs = email "bob@example.com"
                      let byEmail = plasm { for c in Db.customers do where (c.Email = bobs) }
                      // Is the email written into the SQL text?
                      (ok (repo.Explain byEmail)).Sql.Contains "bob@example.com" |> shouldBe __
                      let! bob = repo.One byEmail
                      (ok bob).Value.Name |> shouldBe __
                  }
          ) ]
