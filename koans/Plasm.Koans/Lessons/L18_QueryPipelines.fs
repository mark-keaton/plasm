/// Lesson 18: About query pipelines.
module Plasm.Koans.Lessons.L18_QueryPipelines

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About query pipelines"
        [ Koan.Sync(
              "Pipelines and expressions agree",
              fun k ->
                  let expression = plasm { for o in Db.orders do where (o.Status = Published); orderBy o.Id }

                  let pipeline =
                      Query.from Db.orders
                      |> Query.where (fun o -> o.Status = Published)
                      |> Query.orderBy (fun o -> o.Id)

                  (k.Sql pipeline = k.Sql expression) |> shouldBe __
          )

          Koan.Task(
              "Pipelines join as often as you like",
              fun k ->
                  task {
                      let gadgetOrders =
                          Query.from Db.orders
                          |> Query.join (Db.orderLines, (fun o -> o.Id), (fun l -> l.OrderId))
                          |> Query.join (Db.products, (fun (_, l) -> l.ProductId), (fun p -> p.Id))
                          |> Query.where (fun ((_, _), p) -> p.Sku = "GADGET")
                          |> Query.orderBy (fun ((o, _), _) -> o.Id)

                      let! rows = k.Repo.All gadgetOrders
                      ok rows |> List.map (fun ((o, l), _) -> o.Id, l.Quantity.Value) |> shouldBe __
                  }
          ) ]
