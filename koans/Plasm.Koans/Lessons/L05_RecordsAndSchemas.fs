/// Lesson 05: About records and schemas.
module Plasm.Koans.Lessons.L05_RecordsAndSchemas

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About records and schemas"
        [ Koan.Sync(
              "A record needs nothing from Plasm",
              fun _ ->
                  // Product is a plain F# record: no base class, no attributes.
                  // Its table and key are described separately, in Db.products.
                  let widget = { Id = ProductId 1L; Sku = "WIDGET"; Name = "Widget" }
                  let renamed = { widget with Name = "Better widget" }
                  widget.Name |> shouldBe __
                  renamed.Name |> shouldBe __
          )

          Koan.Sync(
              "A schema names the table",
              fun repo ->
                  // A schema is a value that says how a record is stored. In Shop.fs,
                  // Db.products is: schema<Product> "products" { identityKey (fun p -> p.Id) }
                  // Query.from makes a query for every row of a schema's table.
                  // repo.Explain shows the SQL a query would send, without sending it.
                  let sql = (ok (repo.Explain(Query.from Db.products))).Sql
                  // Which table does the query read from?
                  sql.Contains "FROM \"products\"" |> shouldBe __
          )

          Koan.Sync(
              "Columns are named in snake_case",
              fun repo ->
                  // A column is named after its field, in snake_case: lower case words
                  // joined by underscores. The schema could name a column itself, but
                  // Db.orders does not.
                  let sql = (ok (repo.Explain(Query.from Db.orders))).Sql
                  // Order has a field PlacedAt. Which name does the SQL use?
                  sql.Contains "\"placed_at\"" |> shouldBe __
                  sql.Contains "\"PlacedAt\"" |> shouldBe __
          )

          Koan.Task(
              "A key finds one record",
              fun repo ->
                  task {
                      // The key is the field that identifies a record: identityKey (fun p -> p.Id).
                      // The database numbers new rows 1, 2, 3, in the order they were inserted.
                      // repo runs queries against the database; One reads one record, if any.
                      let! product = repo.One(plasm { for p in Db.products do where (p.Id = ProductId 2L) })
                      // ok takes the value out of an Ok result; .Value takes it out of Some.
                      (ok product).Value.Name |> shouldBe __
                  }
          )

          Koan.Task(
              "A union is stored as text",
              fun repo ->
                  task {
                      // OrderStatus is a union with no fields. Scalar.enum in Db.scalars stores
                      // each case as text: Draft as "draft", Published as "published", and so on.
                      // Here the status column of Order 2 is changed with SQL, as text.
                      // (The change is made in this koan's sandbox and vanishes when it ends.)
                      let! _ = repo.ExecuteSql $"update orders set status = 'cancelled' where id = 2"
                      let! order = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId 2L) })
                      // Is Status the text, or a case of OrderStatus?
                      (ok order).Value.Status |> shouldBe __
                  }
          )

          Koan.Task(
              "Wrapped keys keep their meaning",
              fun repo ->
                  task {
                      // CustomerId wraps the bigint key (Scalar.wrap in Db.scalars).
                      let! ada = repo.One(plasm { for c in Db.customers do where (c.Email = "ada@example.com") })
                      let ada = (ok ada).Value
                      ada.Id |> shouldBe __
                  }
          )

          Koan.Task(
              "Stored values can be rejected",
              fun repo ->
                  task {
                      // Quantity can only be built by Quantity.create, which refuses zero.
                      Quantity.create 0 |> Result.isOk |> shouldBe __

                      // A zero stored in the database is refused on the way in, with its field named.
                      let! _ = repo.ExecuteSql $"update order_lines set quantity = 0 where id = 1"
                      let! lines = repo.All(Query.from Db.orderLines)

                      match lines with
                      | Error(DbError.Materialization m) -> m.Property |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "Absent values are options",
              fun repo ->
                  task {
                      // Order 4 has no customer (a NULL column) but has a cancellation reason.
                      let! order = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId 4L) })
                      let order = (ok order).Value
                      order.CustomerId |> shouldBe __
                      order.CancellationReason |> shouldBe __
                  }
          )

          Koan.Sync(
              "A model reports every mistake at once",
              fun _ ->
                  // Building a model with no scalar mappings: CustomerId, OrderId,
                  // OrderStatus and the rest cannot be stored.
                  match Model.build [] [ Db.customers; Db.orders ] with
                  | Ok _ -> failwith "expected configuration errors"
                  | Error errors ->
                      (errors.Length > 1) |> shouldBe __
                      errors |> List.exists (fun e -> e.Model = "Order") |> shouldBe __
          ) ]
