/// Lesson 25: About windows and aggregates over rows.
module Plasm.Koans.Lessons.L25_WindowsAndRowAggregates

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private ada = Some(CustomerId 1L)

let lesson =
    Lesson.make
        "About windows and aggregates over rows"
        [ Koan.Task(
              "Numbering each customer's orders",
              fun repo ->
                  task {
                      // A window function is read in select, beside the row. Its window is
                      // Win.partition key (the rows sharing key) or Win.whole, ordered with
                      // |> Win.orderBy. Win.rowNumber is the row's place in it, from 1.
                      let numbered =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.CustomerId <> None)
                                  orderBy o.Id
                                  select (o.Id, Win.rowNumber (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt))
                          }

                      let! rows = repo.All numbered
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A running total and the row before",
              fun repo ->
                  task {
                      // Win.runningSum adds up to this row, in the window's order; Win.lag is
                      // a value of the row before it, None for the first.
                      let running =
                          plasm {
                              for o in tableOf<Order> do
                                  orderBy o.PlacedAt
                                  select (Win.runningSum o.Total (Win.whole |> Win.orderBy o.PlacedAt), Win.lag o.Total (Win.whole |> Win.orderBy o.PlacedAt))
                          }

                      let! rows = repo.All running
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A whole window beside each row",
              fun repo ->
                  task {
                      // Win.sum, Win.avg and Win.count are over every row of the window,
                      // whatever its order: over Win.whole, every row the query reads.
                      let totals =
                          plasm {
                              for o in tableOf<Order> do
                                  orderBy o.Id
                                  select (Win.sum o.Total Win.whole, Win.count Win.whole)
                          }

                      let! rows = repo.All totals
                      ok rows |> List.head |> shouldBe __
                  }
          )

          Koan.Task(
              "The window is over the rows kept",
              fun repo ->
                  task {
                      // The window is over the rows the where keeps, before the order and
                      // the page.
                      let numbered =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Total > 20m)
                                  orderBy o.PlacedAt
                                  select (Win.rowNumber (Win.whole |> Win.orderBy o.PlacedAt))
                          }

                      let! rows = repo.All numbered
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A rank needs an order",
              fun repo ->
                  task {
                      // Win.rowNumber, rank, denseRank, lag, lead and runningSum depend on an
                      // order. Over a window with none, the query is refused when it is
                      // translated, a DbError.Translation saying "Win.rank needs an order".
                      let unordered = plasm { for o in tableOf<Order> do select (Win.rank (Win.partition o.CustomerId)) }
                      let! r = repo.All unordered

                      let refused =
                          match r with
                          | Error(DbError.Translation t) -> (string t).Contains "Win.rank needs an order"
                          | _ -> false

                      refused |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting and adding the rows that meet a test",
              fun repo ->
                  task {
                      // Agg.countWhere counts the rows a condition keeps, and Agg.sumWhere adds
                      // their values: FILTER (WHERE ...) in SQL, beside the group's others.
                      let published =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.CustomerId <> None)
                                  groupBy o.CustomerId
                                  orderBy o.CustomerId
                                  select (o.CustomerId, Agg.countWhere (o.Status = Published), Agg.sumWhere (o.Status = Published) o.Total)
                          }

                      let! rows = repo.All published
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Whether any, whether all",
              fun repo ->
                  task {
                      // Agg.any says whether some row meets a condition, Agg.all whether every
                      // row does, as List.exists and List.forall would.
                      let tests =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.CustomerId <> None)
                                  groupBy o.CustomerId
                                  orderBy o.CustomerId
                                  select (o.CustomerId, Agg.any (o.Total > 50m), Agg.all (o.Status = Published))
                          }

                      let! rows = repo.All tests
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A list in a stated order",
              fun repo ->
                  task {
                      // Agg.listBy value key collects the rows' values, in key's order: a
                      // list always says its order. Agg.listByDescending reverses it.
                      let! byDay = repo.One(plasm { for o in tableOf<Order> do where (o.CustomerId = ada); select (Agg.listBy o.Total o.PlacedAt) })
                      (ok byDay).Value |> shouldBe __
                      let! latestFirst = repo.One(plasm { for o in tableOf<Order> do where (o.CustomerId = ada); select (Agg.listByDescending o.Total o.PlacedAt) })
                      (ok latestFirst).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Over no rows",
              fun repo ->
                  task {
                      // Over no rows each gives what the F# function it is named after gives
                      // for no items: a count of 0, List.exists false, List.forall true, [].
                      let none =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Total > 1000m)
                                  select (Agg.countWhere (o.Status = Published), Agg.any (o.Total > 50m), Agg.all (o.Status = Published), Agg.listBy o.Total o.PlacedAt)
                          }

                      let! row = repo.One none
                      (ok row).Value |> shouldBe __
                  }
          ) ]
