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
              fun repo ->
                  task {
                      // join pairs each row with the rows of another table whose key matches (an inner join).
                      // Every OrderLine has a ProductId, so every line finds its Product.
                      let linesWithProducts =
                          plasm {
                              for l in tableOf<OrderLine> do
                                  join p in tableOf<Product> on (l.ProductId = p.Id)
                                  orderBy l.Id
                          }

                      let! rows = repo.All linesWithProducts
                      ok rows |> List.map (fun (_, p) -> p.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting a pair's fields",
              fun repo ->
                  task {
                      // select can name fields from either side of the join.
                      let orderOne =
                          plasm {
                              for l in tableOf<OrderLine> do
                                  join p in tableOf<Product> on (l.ProductId = p.Id)
                                  where (l.OrderId = OrderId 1L)
                                  orderBy l.Id
                                  select (p.Name, l.UnitPrice)
                          }

                      let! rows = repo.All orderOne
                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting joined rows",
              fun repo ->
                  task {
                      // A join gives one row for each matching pair, so an Order appears once per Payment.
                      let paid =
                          plasm {
                              for o in tableOf<Order> do
                                  join p in tableOf<Payment> on (o.Id = p.OrderId)
                                  orderBy p.Id
                          }

                      let! n = repo.Count paid
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "A left join's missing side is None",
              fun repo ->
                  task {
                      // leftJoin keeps rows with no match; the other side is an option, None when missing.
                      let fourth =
                          plasm {
                              for o in tableOf<Order> do
                                  leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                  where (o.Id = OrderId 4L)
                                  select (o.Id, c)
                          }

                      let! row = repo.One fourth
                      let (_, customer) = (ok row).Value
                      customer |> shouldBe __
                  }
          )

          Koan.Task(
              "An inner join gives pairs",
              fun repo ->
                  task {
                      let pairs =
                          plasm {
                              for o in tableOf<Order> do
                                  join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                  orderBy o.Id
                                  select (o.Id, c)
                          }

                      // A whole Customer can be selected. So can a whole Order: it comes
                      // with its lines, read in one more command (lesson 09). This query
                      // selects only the order's id.
                      let! rows = repo.All pairs
                      ok rows |> List.map (fun (id, c) -> id, c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "A left join keeps every row",
              fun repo ->
                  task {
                      let all =
                          plasm {
                              for o in tableOf<Order> do
                                  leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                  orderBy o.Id
                                  select (o.Id, c)
                          }

                      let! rows = repo.All all
                      ok rows |> List.map (fun (id, c) -> id, c |> Option.map (fun c -> c.Name)) |> List.last |> shouldBe __
                  }
          )

          Koan.Task(
              "Join keys are typed",
              fun repo ->
                  task {
                      // The keys must have the same type: an optional CustomerId on the Order side.
                      let pairs =
                          plasm {
                              for o in tableOf<Order> do
                                  join c in tableOf<Customer> on (o.CustomerId = __)
                                  orderBy o.Id
                          }

                      let! rows = repo.Count pairs
                      ok rows |> shouldBe 3L
                  }
          )

          Koan.Task(
              "Filters on joined columns run in SQL",
              fun repo ->
                  task {
                      let adas =
                          plasm {
                              for o in tableOf<Order> do
                                  join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                  where (c.Name = "Ada")
                                  orderBy o.Id
                                  select o.Id
                          }

                      (ok (repo.Explain adas)).Sql.Contains "WHERE" |> shouldBe __
                      let! ids = repo.All adas
                      ok ids |> shouldBe __
                  }
          )

          Koan.Task(
              "Joined queries compose",
              fun repo ->
                  task {
                      let withCustomers = plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.CustomerId = Some c.Id); orderBy o.Id }
                      let large (q: Query<Order * Customer>) = plasm { for (o, c) in q do where (o.Total > 20m); select c.Name }
                      let! rows = repo.All(large withCustomers)
                      ok rows |> shouldBe __
                  }
          ) ]
