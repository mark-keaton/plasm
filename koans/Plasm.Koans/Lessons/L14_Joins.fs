/// Lesson 14: About joins.
module Plasm.Koans.Lessons.L14_Joins

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About joins"
        [ Koan.Task(
              "The simplest inner join",
              fun k ->
                  task {
                      // join pairs each row with the rows of another table whose key matches (an inner join).
                      // Every OrderLine has a ProductId, so every line finds its Product.
                      let linesWithProducts =
                          plasm {
                              for l in Db.orderLines do
                                  join p in Db.products on (l.ProductId = p.Id)
                                  orderBy l.Id
                          }

                      let! rows = k.Repo.All linesWithProducts
                      ok rows |> List.map (fun (_, p) -> p.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting a pair's fields",
              fun k ->
                  task {
                      // select can name fields from either side of the join.
                      let orderOne =
                          plasm {
                              for l in Db.orderLines do
                                  join p in Db.products on (l.ProductId = p.Id)
                                  where (l.OrderId = OrderId 1L)
                                  orderBy l.Id
                                  select (p.Name, l.UnitPrice)
                          }

                      let! rows = k.Repo.All orderOne
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting joined rows",
              fun k ->
                  task {
                      // A join gives one row for each matching pair, so an Order appears once per Payment.
                      let paid =
                          plasm {
                              for o in Db.orders do
                                  join p in Db.payments on (o.Id = p.OrderId)
                                  orderBy p.Id
                          }

                      let! n = k.Repo.Count paid
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "A left join's missing side is None",
              fun k ->
                  task {
                      // leftJoin keeps rows with no match; the other side is an option, None when missing.
                      let fourth =
                          plasm {
                              for o in Db.orders do
                                  leftJoin c in Db.customers on (o.CustomerId = Some c.Id)
                                  where (o.Id = OrderId 4L)
                          }

                      let! row = k.Repo.One fourth
                      let (_, customer) = (ok row).Value
                      customer |> shouldBe __
                  }
          )

          Koan.Task(
              "An inner join gives pairs",
              fun k ->
                  task {
                      let pairs =
                          plasm {
                              for o in Db.orders do
                                  join c in Db.customers on (o.CustomerId = Some c.Id)
                                  orderBy o.Id
                          }

                      let! rows = k.Repo.All pairs
                      ok rows |> List.map (fun (o, c) -> o.Id, c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "A left join keeps every row",
              fun k ->
                  task {
                      let all =
                          plasm {
                              for o in Db.orders do
                                  leftJoin c in Db.customers on (o.CustomerId = Some c.Id)
                                  orderBy o.Id
                          }

                      let! rows = k.Repo.All all
                      ok rows |> List.map (fun (o, c) -> o.Id, c |> Option.map (fun c -> c.Name)) |> List.last |> shouldBe __
                  }
          )

          Koan.Task(
              "Join keys are typed",
              fun k ->
                  task {
                      // The keys must have the same type: an optional CustomerId on the Order side.
                      let pairs =
                          plasm {
                              for o in Db.orders do
                                  join c in Db.customers on (o.CustomerId = __)
                                  orderBy o.Id
                          }

                      let! rows = k.Repo.Count pairs
                      ok rows |> shouldBe 3L
                  }
          )

          Koan.Task(
              "Filters on joined columns run in SQL",
              fun k ->
                  task {
                      let adas =
                          plasm {
                              for o in Db.orders do
                                  join c in Db.customers on (o.CustomerId = Some c.Id)
                                  where (c.Name = "Ada")
                          }

                      (k.Sql adas).Contains "WHERE" |> shouldBe __
                      let! n = k.Repo.Count adas
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Joined queries compose",
              fun k ->
                  task {
                      let withCustomers = plasm { for o in Db.orders do join c in Db.customers on (o.CustomerId = Some c.Id); orderBy o.Id }
                      let large (q: Query<Order * Customer>) = plasm { for (o, c) in q do where (o.Total > 20m) }
                      let! rows = k.Repo.All(large withCustomers)
                      ok rows |> List.map (fun (_, c) -> c.Name) |> shouldBe __
                  }
          ) ]
