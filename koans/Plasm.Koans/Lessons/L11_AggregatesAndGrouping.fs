/// Lesson 11: About aggregates and grouping.
module Plasm.Koans.Lessons.L11_AggregatesAndGrouping

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About aggregates and grouping"
        [ Koan.Task(
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
