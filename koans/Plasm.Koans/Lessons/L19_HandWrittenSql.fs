/// Lesson 19: About hand-written SQL.
module Plasm.Koans.Lessons.L19_HandWrittenSql

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
                      (List.last (sqlSent ())).Contains "GADGET" |> shouldBe __
                  }
          )

          Koan.Task(
              "SQL can return mapped records",
              fun repo ->
                  task {
                      // Rows are read into a record of the model by column name. (Not a
                      // whole Order: an Order is read with its lines, which a query of
                      // the orders table alone does not read.)
                      let minimum = 50m
                      let! big = repo.Sql<Payment>($"SELECT * FROM payments WHERE amount >= {minimum} ORDER BY id")
                      ok big |> List.map (fun p -> p.Id) |> shouldBe __
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
                      (List.last (sqlSent ())).Contains "@p0" |> shouldBe __
                  }
          )

          Koan.Task(
              "A fragment inside a typed query",
              fun repo ->
                  task {
                      // Raw.fragment writes SQL of its own into a typed query, {0} and {1}
                      // standing for its arguments. ILIKE is PostgreSQL's LIKE ignoring
                      // case, and % matches any text.
                      let startingWithA = plasm { for c in tableOf<Customer> do where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, "a%")) }
                      let! rows = repo.All startingWithA
                      ok rows |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Commands return affected rows",
              fun repo ->
                  task {
                      // A hand-written command is a write, so it is a transaction step:
                      // Tx.executeSql gives how many rows it changed.
                      let archive: Tx<int, unit> =
                          Tx.executeSql $"update orders set status = 'cancelled', cancellation_reason = 'archived' where status = 'published'"

                      let! n = repo.Transact archive
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Hand-written SQL inside a transaction",
              fun repo ->
                  task {
                      // Tx.sql reads with hand-written SQL as a step, so it sees what the
                      // steps before it wrote, before anything is committed.
                      let tx =
                          transaction {
                              let! _ = Tx.executeSql $"delete from payments where status = 'failed'"
                              let! left = Tx.sql<int64, unit> $"select count(*) from payments"
                              return List.head left
                          }

                      let! r = repo.Transact tx
                      ok r |> shouldBe __
                  }
          ) ]
