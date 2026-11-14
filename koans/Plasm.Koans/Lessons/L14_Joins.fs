/// Lesson 14: About joins.
module Plasm.Koans.Lessons.L14_Joins

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About joins"
        [ Koan.Task(
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
