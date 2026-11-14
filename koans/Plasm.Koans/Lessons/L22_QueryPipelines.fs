/// Lesson 22: About query pipelines.
module Plasm.Koans.Lessons.L22_QueryPipelines

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About query pipelines"
        [ Koan.Task(
              "Filtering in a pipeline",
              fun repo ->
                  task {
                      // A pipeline builds a query step by step with |>, the pipe operator: `x |> f` is `f x`.
                      // Query.where is the pipeline form of plasm's where.
                      let bobs = Query.from Db.customers |> Query.where (fun c -> c.Name = "Bob")
                      let! rows = repo.All bobs
                      ok rows |> List.map (fun c -> c.Email) |> shouldBe __
                  }
          )

          Koan.Task(
              "Ordering in a pipeline",
              fun repo ->
                  task {
                      // Query.orderBy is the pipeline form of orderBy.
                      let bySku = Query.from Db.products |> Query.orderBy (fun p -> p.Sku)
                      let! rows = repo.All bySku
                      ok rows |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "Taking in a pipeline",
              fun repo ->
                  task {
                      // Query.take keeps only the first rows, in the order the pipeline gives.
                      let firstTwo = Query.from Db.customers |> Query.orderBy (fun c -> c.Id) |> Query.take 2
                      let! rows = repo.All firstTwo
                      ok rows |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting in a pipeline",
              fun repo ->
                  task {
                      // Query.select comes last: the rows become the values it picks out.
                      let names = Query.from Db.products |> Query.orderBy (fun p -> p.Id) |> Query.select (fun p -> p.Name)
                      let! rows = repo.All names
                      ok rows |> shouldBe __
                  }
          )

          Koan.Sync(
              "A pipeline filter is a plasm filter",
              fun repo ->
                  // repo.Explain gives the SQL a query would send, without running it.
                  let expression = plasm { for c in Db.customers do where (c.Name = "Bob") }
                  let pipeline = Query.from Db.customers |> Query.where (fun c -> c.Name = "Bob")
                  ((ok (repo.Explain pipeline)).Sql = (ok (repo.Explain expression)).Sql) |> shouldBe __
          )

          Koan.Task(
              "A pipeline select is a plasm select",
              fun repo ->
                  task {
                      let expression = plasm { for p in Db.products do orderBy p.Id; select p.Sku }
                      let pipeline = Query.from Db.products |> Query.orderBy (fun p -> p.Id) |> Query.select (fun p -> p.Sku)
                      let! fromExpression = repo.All expression
                      let! fromPipeline = repo.All pipeline
                      (ok fromPipeline = ok fromExpression) |> shouldBe __
                  }
          )

          Koan.Sync(
              "Pipelines and expressions agree",
              fun repo ->
                  let expression = plasm { for o in Db.orders do where (o.Status = Published); orderBy o.Id }

                  let pipeline =
                      Query.from Db.orders
                      |> Query.where (fun o -> o.Status = Published)
                      |> Query.orderBy (fun o -> o.Id)

                  ((ok (repo.Explain pipeline)).Sql = (ok (repo.Explain expression)).Sql) |> shouldBe __
          )

          Koan.Task(
              "Pipelines join as often as you like",
              fun repo ->
                  task {
                      let gadgetOrders =
                          Query.from Db.orders
                          |> Query.join (Db.orderLines, (fun o -> o.Id), (fun l -> l.OrderId))
                          |> Query.join (Db.products, (fun (_, l) -> l.ProductId), (fun p -> p.Id))
                          |> Query.where (fun ((_, _), p) -> p.Sku = "GADGET")
                          |> Query.orderBy (fun ((o, _), _) -> o.Id)

                      let! rows = repo.All gadgetOrders
                      ok rows |> List.map (fun ((o, l), _) -> o.Id, l.Quantity.Value) |> shouldBe __
                  }
          ) ]
