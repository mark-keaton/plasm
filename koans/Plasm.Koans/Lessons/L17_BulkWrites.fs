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
                      // The function says what each matching row becomes. Like every write,
                      // it is a step of a transaction: Tx.execute, run by repo.Transact.
                      let gizmos = plasm { for p in tableOf<Product> do where (p.Sku = "GIZMO") }
                      let! changed = repo.Transact(Tx.execute (Bulk.update (gizmos, fun p -> { p with Name = "Gizmo Mark II" })): Tx<_, unit>)
                      // The result is how many rows were changed.
                      ok changed |> shouldBe __
                  }
          )

          Koan.Task(
              "A bulk update can match nothing",
              fun repo ->
                  task {
                      let sprockets = plasm { for p in tableOf<Product> do where (p.Sku = "SPROCKET") }
                      let! changed = repo.Transact(Tx.execute (Bulk.update (sprockets, fun p -> { p with Name = "Sprocket" })): Tx<_, unit>)
                      ok changed |> shouldBe __
                  }
          )

          Koan.Task(
              "A bulk delete returns a count",
              fun repo ->
                  task {
                      // A bulk delete removes every row a query matches, and returns how many.
                      let ofOrder1 = plasm { for p in tableOf<Payment> do where (p.OrderId = OrderId 1L) }
                      let! removed = repo.Transact(Tx.execute (Bulk.delete ofOrder1): Tx<_, unit>)
                      ok removed |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting every payment",
              fun repo ->
                  task {
                      // Query.from with no filter matches every row of the table.
                      let! removed = repo.Transact(Tx.execute (Bulk.delete (Query.from (tableOf<Payment>))): Tx<_, unit>)
                      ok removed |> shouldBe __
                  }
          )

          Koan.Task(
              "Updating what a query matches",
              fun repo ->
                  task {
                      let drafts = plasm { for o in tableOf<Order> do where (o.Status = Draft) }
                      let! changed = repo.Transact(Tx.execute (Bulk.update (drafts, fun o -> { o with Status = Published })): Tx<_, unit>)
                      ok changed |> shouldBe __
                      let! published = repo.Count(plasm { for o in tableOf<Order> do where (o.Status = Published) })
                      ok published |> shouldBe __
                  }
          )

          Koan.Task(
              "Updating from the current row",
              fun repo ->
                  task {
                      let! _ = repo.Transact(Tx.execute (Bulk.update (Query.from (tableOf<Order>), fun o -> { o with Total = o.Total * 2m })): Tx<_, unit>)
                      let! total = repo.One(plasm { for o in tableOf<Order> do select (Agg.sum o.Total) })
                      (ok total).Value |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting what a query matches",
              fun repo ->
                  task {
                      let failed = plasm { for p in tableOf<Payment> do where (p.Status = Failed) }
                      let! removed = repo.Transact(Tx.execute (Bulk.delete failed): Tx<_, unit>)
                      ok removed |> shouldBe __
                      let! left = repo.Count(Query.from (tableOf<Payment>))
                      ok left |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting many records",
              fun repo ->
                  task {
                      let parts = [ New.product "BOLT" "Bolt"; New.product "NUT" "Nut"; New.product "WASHER" "Washer" ]
                      let! inserted = repo.Transact(Tx.execute (Bulk.insert parts): Tx<_, unit>)
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting only what is missing",
              fun repo ->
                  task {
                      let catalogue = [ New.product "WIDGET" "Widget"; New.product "SPROCKET" "Sprocket" ]
                      let! inserted = repo.Transact(Tx.execute (Bulk.insertMissing (catalogue, fun p -> p.Sku)): Tx<_, unit>)
                      ok inserted |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "Bulk writes are steps like any other",
              fun repo ->
                  task {
                      // A bulk write is one step of a transaction: when a later step
                      // fails, it is rolled back with the rest.
                      let tx =
                          transaction {
                              let! _ = Tx.execute (Bulk.delete (Query.from (tableOf<Payment>)))
                              return! Tx.fail "changed my mind"
                          }

                      let! r = repo.Transact tx
                      Result.isError r |> shouldBe __
                      let! left = repo.Count(Query.from (tableOf<Payment>))
                      ok left |> shouldBe __
                  }
          )

          Koan.Task(
              "Bulk writes can be inspected",
              fun repo ->
                  task {
                      let plan = repo.Explain(Bulk.delete (plasm { for p in tableOf<Payment> do where (p.Status = Failed) }))
                      (ok plan).Sql.StartsWith "DELETE FROM \"payments\"" |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          ) ]
