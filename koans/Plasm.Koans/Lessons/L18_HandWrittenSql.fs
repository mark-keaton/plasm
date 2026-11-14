/// Lesson 18: About hand-written SQL.
module Plasm.Koans.Lessons.L18_HandWrittenSql

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About hand-written SQL"
        [ Koan.Task(
              "A hand-written SELECT returns records",
              fun k ->
                  task {
                      // repo.Sql<'R> runs SQL you write ($"..." is an interpolated string) and reads
                      // each row into an 'R. Columns are matched to fields by name.
                      let! products = k.Repo.Sql<Product>($"SELECT * FROM products ORDER BY id")
                      ok products |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "No matching rows is an empty list",
              fun k ->
                  task {
                      let! products = k.Repo.Sql<Product>($"SELECT * FROM products WHERE sku = 'SPROCKET'")
                      ok products |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return a single number",
              fun k ->
                  task {
                      // A row with one column can be read as that column's value.
                      let! counts = k.Repo.Sql<int64>($"SELECT count(*) FROM products")
                      ok counts |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return pairs",
              fun k ->
                  task {
                      // A row with two columns can be read as a tuple: the first column, then the second.
                      let! pairs = k.Repo.Sql<string * string>($"SELECT sku, name FROM products ORDER BY id")
                      ok pairs |> List.head |> shouldBe __
                  }
          )

          Koan.Task(
              "An interpolated value becomes a parameter",
              fun k ->
                  task {
                      // A parameter is a value sent beside the SQL text, never pasted into it.
                      let sku = "GADGET"
                      let! found = k.Repo.Sql<Product>($"SELECT * FROM products WHERE sku = {sku}")
                      ok found |> List.map (fun p -> p.Name) |> shouldBe __
                      // k.Commands is the SQL text this koan has sent, in order.
                      (List.last k.Commands).Contains "GADGET" |> shouldBe __
                  }
          )

          Koan.Task(
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
