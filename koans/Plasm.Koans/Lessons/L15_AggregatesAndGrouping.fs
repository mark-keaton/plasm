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
              fun repo ->
                  task {
                      // An aggregate summarises many rows as one value, computed by the database.
                      // Agg.count () is the number of rows; One reads the single row it gives.
                      let! n = repo.One(plasm { for o in tableOf<Order> do where (o.Status = Published); select (Agg.count ()) })
                      (ok n).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding up with Agg.sum",
              fun repo ->
                  task {
                      // Agg.sum adds up a column over the rows the query matches.
                      let! total = repo.One(plasm { for o in tableOf<Order> do where (o.Status = Published); select (Agg.sum o.Total) })
                      (ok total).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "The largest with Agg.max",
              fun repo ->
                  task {
                      // Agg.max is the largest value: an option, because there might be no rows.
                      let! largest = repo.One(plasm { for o in tableOf<Order> do select (Agg.max o.Total) })
                      (ok largest).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Grouping by one key",
              fun repo ->
                  task {
                      // groupBy puts rows with the same key in one group; select gives one row per group.
                      let linesPerOrder =
                          plasm {
                              for l in tableOf<OrderLine> do
                                  groupBy l.OrderId
                                  orderBy l.OrderId
                                  select (l.OrderId, Agg.count ())
                          }

                      let! rows = repo.All linesPerOrder
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting a page counts the page",
              fun repo ->
                  task {
                      let! n = repo.Count(plasm { for o in tableOf<Order> do orderBy o.Id; take 2 })
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Sum and average are typed",
              fun repo ->
                  task {
                      let! totals = repo.One(plasm { for o in tableOf<Order> do select (Agg.sum o.Total, Agg.avg o.Total) })
                      // The four totals are 30.00, 12.50, 99.00 and 5.00.
                      (ok totals).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "No rows is explicit",
              fun repo ->
                  task {
                      let! r = repo.One(plasm { for o in tableOf<Order> do where (o.Total > 1000m); select (Agg.sum o.Total, Agg.max o.Total) })
                      (ok r).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Grouping by a key",
              fun repo ->
                  task {
                      let perCustomer =
                          plasm {
                              for o in tableOf<Order> do
                                  groupBy o.CustomerId
                                  orderBy o.CustomerId
                                  select (o.CustomerId, Agg.count ())
                          }

                      let! rows = repo.All perCustomer
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering groups",
              fun repo ->
                  task {
                      let repeat =
                          plasm {
                              for o in tableOf<Order> do
                                  groupBy o.CustomerId
                                  having (Agg.count () > 1L)
                                  select (o.CustomerId, Agg.count ())
                          }

                      let! rows = repo.All repeat
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Each value once",
              fun repo ->
                  task {
                      // selectDistinct keeps each selected value once, as List.distinct
                      // would: the order with no customer gives None, one value of its
                      // own. Agg.countDistinct counts the distinct values that are not
                      // None, as SQL's COUNT(DISTINCT ...) does.
                      let! customers = repo.All(plasm { for o in tableOf<Order> do selectDistinct o.CustomerId })
                      (ok customers).Length |> shouldBe __

                      let! counted = repo.One(plasm { for o in tableOf<Order> do select (Agg.countDistinct o.CustomerId) })
                      ok counted |> shouldBe (Some __)
                  }
          )

          Koan.Task(
              "Grouping mistakes are refused",
              fun repo ->
                  task {
                      // Status is neither grouped nor aggregated.
                      let wrong = plasm { for o in tableOf<Order> do groupBy o.CustomerId; select (o.Status, Agg.count ()) }
                      let! r = repo.All wrong
                      Result.isError r |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          ) ]
