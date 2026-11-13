/// Lesson 01: About records and schemas.
module Plasm.Koans.Lessons.L01_RecordsAndSchemas

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

          Koan.Task(
              "Wrapped keys keep their meaning",
              fun k ->
                  task {
                      // CustomerId wraps the bigint key (Scalar.wrap in Db.scalars).
                      let! ada = k.Repo.One(plasm { for c in Db.customers do where (c.Email = "ada@example.com") })
                      let ada = (ok ada).Value
                      ada.Id |> shouldBe __
                  }
          )

          Koan.Task(
              "Stored values can be rejected",
              fun k ->
                  task {
                      // Quantity can only be built by Quantity.create, which refuses zero.
                      Quantity.create 0 |> Result.isOk |> shouldBe __

                      // A zero stored in the database is refused on the way in, with its field named.
                      let! _ = k.Repo.ExecuteSql $"update order_lines set quantity = 0 where id = 1"
                      let! lines = k.Repo.All(Query.from Db.orderLines)

                      match lines with
                      | Error(DbError.Materialization m) -> m.Property |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "Absent values are options",
              fun k ->
                  task {
                      // Order 4 has no customer (a NULL column) but has a cancellation reason.
                      let! order = k.Repo.One(plasm { for o in Db.orders do where (o.Id = OrderId 4L) })
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
