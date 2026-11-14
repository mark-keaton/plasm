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
              fun repo ->
                  task {
                      // repo.Sql<'R> runs SQL you write ($"..." is an interpolated string) and reads
                      // each row into an 'R. Columns are matched to fields by name.
                      let! products = repo.Sql<Product>($"SELECT * FROM products ORDER BY id")
                      ok products |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "No matching rows is an empty list",
              fun repo ->
                  task {
                      let! products = repo.Sql<Product>($"SELECT * FROM products WHERE sku = 'SPROCKET'")
                      ok products |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return a single number",
              fun repo ->
                  task {
                      // A row with one column can be read as that column's value.
                      let! counts = repo.Sql<int64>($"SELECT count(*) FROM products")
                      ok counts |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return pairs",
              fun repo ->
                  task {
                      // A row with two columns can be read as a tuple: the first column, then the second.
                      let! pairs = repo.Sql<string * string>($"SELECT sku, name FROM products ORDER BY id")
                      ok pairs |> List.head |> shouldBe __
                  }
          )

          Koan.Task(
              "An interpolated value becomes a parameter",
              fun repo ->
                  task {
                      // A parameter is a value sent beside the SQL text, never pasted into it.
                      let sku = "GADGET"
                      let! found = repo.Sql<Product>($"SELECT * FROM products WHERE sku = {sku}")
                      ok found |> List.map (fun p -> p.Name) |> shouldBe __
                      // commands () is the SQL text this koan has sent, in order.
                      (List.last (commands ())).Contains "GADGET" |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return mapped records",
              fun repo ->
                  task {
                      let minimum = 50m
                      let! big = repo.Sql<Order>($"SELECT * FROM orders WHERE total >= {minimum} ORDER BY id")
                      ok big |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return tuples and scalars",
              fun repo ->
                  task {
                      let! names = repo.Sql<string>($"SELECT name FROM customers ORDER BY name")
                      ok names |> shouldBe __
                      let! perStatus = repo.Sql<string * int64>($"SELECT status, count(*) FROM orders GROUP BY status ORDER BY status")
                      ok perStatus |> List.head |> shouldBe __
                  }
          )

          Koan.Task(
              "Interpolation is parameters",
              fun repo ->
                  task {
                      let name = "Ada'; drop table customers; --"
                      let! found = repo.Sql<Customer>($"SELECT * FROM customers WHERE name = {name}")
                      ok found |> shouldBe __
                      (List.last (commands ())).Contains "@p0" |> shouldBe __
                  }
          )

          Koan.Task(
              "A fragment inside a typed query",
              fun repo ->
                  task {
                      let startingWithA = plasm { for c in Db.customers do where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, "a%")) }
                      let! rows = repo.All startingWithA
                      ok rows |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Commands return affected rows",
              fun repo ->
                  task {
                      let! n = repo.ExecuteSql $"update orders set status = 'cancelled', cancellation_reason = 'archived' where status = 'published'"
                      ok n |> shouldBe __
                  }
          ) ]
