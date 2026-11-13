/// Lesson 13: About bulk writes.
module Plasm.Koans.Lessons.L13_BulkWrites

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About bulk writes"
        [ Koan.Task(
              "Updating what a query matches",
              fun k ->
                  task {
                      let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                      let! changed = k.Repo.Execute(Bulk.update (drafts, fun o -> { o with Status = Published }))
                      ok changed |> shouldBe __
                      let! published = k.Repo.Count(plasm { for o in Db.orders do where (o.Status = Published) })
                      ok published |> shouldBe __
                  }
          )

          Koan.Task(
              "Updating from the current row",
              fun k ->
                  task {
                      let! _ = k.Repo.Execute(Bulk.update (Query.from Db.orders, fun o -> { o with Total = o.Total * 2m }))
                      let! total = k.Repo.One(plasm { for o in Db.orders do select (Agg.sum o.Total) })
                      (ok total).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting what a query matches",
              fun k ->
                  task {
                      let failed = plasm { for p in Db.payments do where (p.Status = Failed) }
                      let! removed = k.Repo.Execute(Bulk.delete failed)
                      ok removed |> shouldBe __
                      let! left = k.Repo.Count(Query.from Db.payments)
                      ok left |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting many records",
              fun k ->
                  task {
                      let parts = [ New.product "BOLT" "Bolt"; New.product "NUT" "Nut"; New.product "WASHER" "Washer" ]
                      let! inserted = k.Repo.Execute(Bulk.insert parts)
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting only what is missing",
              fun k ->
                  task {
                      let catalogue = [ New.product "WIDGET" "Widget"; New.product "SPROCKET" "Sprocket" ]
                      let! inserted = k.Repo.Execute(Bulk.insertMissing (catalogue, fun p -> p.Sku))
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "Bulk writes can be inspected",
              fun k ->
                  task {
                      let plan = k.Repo.Explain(Bulk.delete (plasm { for p in Db.payments do where (p.Status = Failed) }))
                      (ok plan).Sql.StartsWith "DELETE FROM \"payments\"" |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          ) ]
