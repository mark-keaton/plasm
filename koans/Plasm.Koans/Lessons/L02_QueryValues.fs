/// Lesson 02: About query values.
module Plasm.Koans.Lessons.L02_QueryValues

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About query values"
        [ Koan.Sync(
              "Building a query runs nothing",
              fun k ->
                  let _published = plasm { for o in Db.orders do where (o.Status = Published) }
                  // How many commands has this koan sent?
                  k.Commands.Length |> shouldBe __
          )

          Koan.Task(
              "Extending makes a new query",
              fun k ->
                  task {
                      let all = Query.from Db.orders
                      let published = plasm { for o in all do where (o.Status = Published) }
                      let! everyOrder = k.Repo.Count all
                      let! publishedOrders = k.Repo.Count published
                      ok everyOrder |> shouldBe __
                      ok publishedOrders |> shouldBe __
                  }
          )

          Koan.Task(
              "Running does not change a query",
              fun k ->
                  task {
                      let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                      let! first = k.Repo.All drafts
                      let! second = k.Repo.All drafts
                      (ok first = ok second) |> shouldBe __
                      (ok first).Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "A query can explain itself",
              fun k ->
                  let published = plasm { for o in Db.orders do where (o.Status = Published) }
                  let sql = k.Sql published
                  // Is the status sent as a parameter?
                  sql.Contains "\"status\" = @p0" |> shouldBe __
                  k.Commands.Length |> shouldBe __
          )

          Koan.Task(
              "Reusable filters are functions",
              fun k ->
                  task {
                      let published (q: Query<Order>) = plasm { for o in q do where (o.Status = Published) }
                      let over (amount: decimal) (q: Query<Order>) = plasm { for o in q do where (o.Total > amount) }
                      let! n = k.Repo.Count(Query.from Db.orders |> published |> over 50m)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Captured values become parameters",
              fun k ->
                  task {
                      let email = "bob@example.com"
                      let byEmail = plasm { for c in Db.customers do where (c.Email = email) }
                      // Is the email written into the SQL text?
                      (k.Sql byEmail).Contains "bob@example.com" |> shouldBe __
                      let! bob = k.Repo.One byEmail
                      (ok bob).Value.Name |> shouldBe __
                  }
          ) ]
