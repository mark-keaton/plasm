/// Lesson 17: About bulk writes.
module Plasm.Koans.Lessons.L17_BulkWrites

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About bulk writes"
        [ Koan.Task(
              "A bulk update returns a count",
              fun repo ->
                  task {
                      // A bulk update changes every row a query matches, in one SQL command.
                      // The function says what each matching row becomes.
                      let gizmos = plasm { for p in Db.products do where (p.Sku = "GIZMO") }
                      let! changed = repo.Execute(Bulk.update (gizmos, fun p -> { p with Name = "Gizmo Mark II" }))
                      // The result is how many rows were changed.
                      ok changed |> shouldBe __
                  }
          )

          Koan.Task(
              "A bulk update can match nothing",
              fun repo ->
                  task {
                      let sprockets = plasm { for p in Db.products do where (p.Sku = "SPROCKET") }
                      let! changed = repo.Execute(Bulk.update (sprockets, fun p -> { p with Name = "Sprocket" }))
                      ok changed |> shouldBe __
                  }
          )

          Koan.Task(
              "A bulk delete returns a count",
              fun repo ->
                  task {
                      // A bulk delete removes every row a query matches, and returns how many.
                      let ofOrder1 = plasm { for p in Db.payments do where (p.OrderId = OrderId 1L) }
                      let! removed = repo.Execute(Bulk.delete ofOrder1)
                      ok removed |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting every payment",
              fun repo ->
                  task {
                      // Query.from with no filter matches every row of the table.
                      let! removed = repo.Execute(Bulk.delete (Query.from Db.payments))
                      ok removed |> shouldBe __
                  }
          )

          Koan.Task(
              "Updating what a query matches",
              fun repo ->
                  task {
                      let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                      let! changed = repo.Execute(Bulk.update (drafts, fun o -> { o with Status = Published }))
                      ok changed |> shouldBe __
                      let! published = repo.Count(plasm { for o in Db.orders do where (o.Status = Published) })
                      ok published |> shouldBe __
                  }
          )

          Koan.Task(
              "Updating from the current row",
              fun repo ->
                  task {
                      let! _ = repo.Execute(Bulk.update (Query.from Db.orders, fun o -> { o with Total = o.Total * 2m }))
                      let! total = repo.One(plasm { for o in Db.orders do select (Agg.sum o.Total) })
                      (ok total).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting what a query matches",
              fun repo ->
                  task {
                      let failed = plasm { for p in Db.payments do where (p.Status = Failed) }
                      let! removed = repo.Execute(Bulk.delete failed)
                      ok removed |> shouldBe __
                      let! left = repo.Count(Query.from Db.payments)
                      ok left |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting many records",
              fun repo ->
                  task {
                      let parts = [ New.product "BOLT" "Bolt"; New.product "NUT" "Nut"; New.product "WASHER" "Washer" ]
                      let! inserted = repo.Execute(Bulk.insert parts)
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                      (commands ()).Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting only what is missing",
              fun repo ->
                  task {
                      let catalogue = [ New.product "WIDGET" "Widget"; New.product "SPROCKET" "Sprocket" ]
                      let! inserted = repo.Execute(Bulk.insertMissing (catalogue, fun p -> p.Sku))
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "Bulk writes can be inspected",
              fun repo ->
                  task {
                      let plan = repo.Explain(Bulk.delete (plasm { for p in Db.payments do where (p.Status = Failed) }))
                      (ok plan).Sql.StartsWith "DELETE FROM \"payments\"" |> shouldBe __
                      (commands ()).Length |> shouldBe __
                  }
          ) ]
