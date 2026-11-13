/// Lesson 14: About hand-written SQL.
module Plasm.Koans.Lessons.L14_HandWrittenSql

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About hand-written SQL"
        [ Koan.Task(
              "SQL can return mapped records",
              fun k ->
                  task {
                      let minimum = 50m
                      let! big = k.Repo.Sql<Order>($"SELECT * FROM orders WHERE total >= {minimum} ORDER BY id")
                      ok big |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return tuples and scalars",
              fun k ->
                  task {
                      let! names = k.Repo.Sql<string>($"SELECT name FROM customers ORDER BY name")
                      ok names |> shouldBe __
                      let! perStatus = k.Repo.Sql<string * int64>($"SELECT status, count(*) FROM orders GROUP BY status ORDER BY status")
                      ok perStatus |> List.head |> shouldBe __
                  }
          )

          Koan.Task(
              "Interpolation is parameters",
              fun k ->
                  task {
                      let name = "Ada'; drop table customers; --"
                      let! found = k.Repo.Sql<Customer>($"SELECT * FROM customers WHERE name = {name}")
                      ok found |> shouldBe __
                      (List.last k.Commands).Contains "@p0" |> shouldBe __
                  }
          )

          Koan.Task(
              "A fragment inside a typed query",
              fun k ->
                  task {
                      let startingWithA = plasm { for c in Db.customers do where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, "a%")) }
                      let! rows = k.Repo.All startingWithA
                      ok rows |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Commands return affected rows",
              fun k ->
                  task {
                      let! n = k.Repo.ExecuteSql $"update orders set status = 'cancelled' where status = 'published'"
                      ok n |> shouldBe __
                  }
          ) ]
