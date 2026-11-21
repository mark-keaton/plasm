/// Lesson 27: About bulk writes that return their rows.
module Plasm.Koans.Lessons.L27_WritesThatReturnRows

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private drafts = plasm { for o in tableOf<Order> do where (o.Status = Draft) }

let lesson =
    Lesson.make
        "About bulk writes that return their rows"
        [ Koan.Task(
              "A bulk update gives back the rows it changed",
              fun repo ->
                  task {
                      // Bulk.updateReturning is Bulk.update giving the rows instead of their
                      // count: as they are after it, read whole, what they own with them, in
                      // the same transaction.
                      let cancel = Bulk.updateReturning (drafts, fun o -> { o with Status = Cancelled; CancellationReason = Some "stale draft" })
                      let! r = repo.Transact(Tx.execute cancel: Tx<Order list, unit>)
                      ok r |> List.map (fun o -> o.Id, o.Status, o.Lines.Length) |> shouldBe __
                  }
          )

          Koan.Task(
              "A bulk delete gives back the rows it removed",
              fun repo ->
                  task {
                      // Bulk.deleteReturning gives the rows it deleted, as they were.
                      let failed = plasm { for p in tableOf<Payment> do where (p.Status = Failed) }
                      let! r = repo.Transact(Tx.execute (Bulk.deleteReturning failed): Tx<Payment list, unit>)
                      ok r |> List.map (fun p -> p.Amount) |> shouldBe __
                      let! left = repo.Count(Query.from (tableOf<Payment>))
                      ok left |> shouldBe __
                  }
          )

          Koan.Task(
              "A delete cannot return what records own",
              fun repo ->
                  task {
                      // A delete cannot read what the rows it removes owned. So it returns
                      // rows only of records that own or link nothing; an Order owns its
                      // lines, and the transaction fails before anything is sent, with
                      // TxFailure.Failed(DbError.Translation ...) saying "a bulk delete
                      // returns only records that own or link nothing".
                      let! r = repo.Transact(Tx.execute (Bulk.deleteReturning drafts): Tx<Order list, unit>)

                      let refused =
                          match r with
                          | Error { Failure = TxFailure.Failed(DbError.Translation t) } -> (string t).Contains "a bulk delete returns only records that own or link nothing"
                          | _ -> false

                      refused |> shouldBe __
                      let! orders = repo.Count(Query.from (tableOf<Order>))
                      ok orders |> shouldBe __
                  }
          ) ]
