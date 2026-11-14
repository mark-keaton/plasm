/// Lesson 15: About aggregates and grouping.
module Plasm.Koans.Lessons.L15_AggregatesAndGrouping

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About aggregates and grouping"
        [ Koan.Task(
              "Counting rows with Agg.count",
              fun k ->
                  task {
                      // An aggregate summarises many rows as one value, computed by the database.
                      // Agg.count () is the number of rows; One reads the single row it gives.
                      let! n = k.Repo.One(plasm { for o in Db.orders do where (o.Status = Published); select (Agg.count ()) })
                      (ok n).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding up with Agg.sum",
              fun k ->
                  task {
                      // Agg.sum adds up a column over the rows the query matches.
                      let! total = k.Repo.One(plasm { for o in Db.orders do where (o.Status = Published); select (Agg.sum o.Total) })
                      (ok total).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "The largest with Agg.max",
              fun k ->
                  task {
                      // Agg.max is the largest value: an option, because there might be no rows.
                      let! largest = k.Repo.One(plasm { for o in Db.orders do select (Agg.max o.Total) })
                      (ok largest).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Grouping by one key",
              fun k ->
                  task {
                      // groupBy puts rows with the same key in one group; select gives one row per group.
                      let linesPerOrder =
                          plasm {
                              for l in Db.orderLines do
                                  groupBy l.OrderId
                                  orderBy l.OrderId
                                  select (l.OrderId, Agg.count ())
                          }

                      let! rows = k.Repo.All linesPerOrder
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting a page counts the page",
              fun k ->
                  task {
                      let! n = k.Repo.Count(plasm { for o in Db.orders do orderBy o.Id; take 2 })
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Sum and average are typed",
              fun k ->
                  task {
                      let! totals = k.Repo.One(plasm { for o in Db.orders do select (Agg.sum o.Total, Agg.avg o.Total) })
                      // The four totals are 30.00, 12.50, 99.00 and 5.00.
                      (ok totals).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "No rows is explicit",
              fun k ->
                  task {
                      let! r = k.Repo.One(plasm { for o in Db.orders do where (o.Total > 1000m); select (Agg.sum o.Total, Agg.max o.Total) })
                      (ok r).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Grouping by a key",
              fun k ->
                  task {
                      let perCustomer =
                          plasm {
                              for o in Db.orders do
                                  groupBy o.CustomerId
                                  orderBy o.CustomerId
                                  select (o.CustomerId, Agg.count ())
                          }

                      let! rows = k.Repo.All perCustomer
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering groups",
              fun k ->
                  task {
                      let repeat =
                          plasm {
                              for o in Db.orders do
                                  groupBy o.CustomerId
                                  having (Agg.count () > 1L)
                                  select (o.CustomerId, Agg.count ())
                          }

                      let! rows = k.Repo.All repeat
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Grouping mistakes are refused",
              fun k ->
                  task {
                      // Status is neither grouped nor aggregated.
                      let wrong = plasm { for o in Db.orders do groupBy o.CustomerId; select (o.Status, Agg.count ()) }
                      let! r = k.Repo.All wrong
                      Result.isError r |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          ) ]
