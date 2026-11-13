/// Lesson 07: About writes.
module Plasm.Koans.Lessons.L07_Writes

open System
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private order (k: KoanContext) (id: int64) =
    task {
        let! o = k.Repo.One(plasm { for o in Db.orders do where (o.Id = OrderId id) })
        return (ok o).Value
    }

let lesson =
    Lesson.make
        "About writes"
        [ Koan.Task(
              "Inserting returns the stored record",
              fun k ->
                  task {
                      let sprocket = New.product "SPROCKET" "Sprocket"
                      let! stored = k.Repo.Insert(Changeset.insert sprocket)
                      // The database generated a key (its value depends on earlier inserts).
                      ((ok stored).Id <> ProductId 0L) |> shouldBe __
                      sprocket.Id |> shouldBe __
                  }
          )

          Koan.Task(
              "Updates write only the changes",
              fun k ->
                  task {
                      let! draft = order k 2L
                      let! _ = k.Repo.Update(draft |> Changeset.edit (fun o -> { o with Status = Published }))
                      let update = List.last k.Commands
                      // Which columns does the UPDATE set?
                      update.Contains "\"status\" = " |> shouldBe __
                      update.Contains "\"total\" = " |> shouldBe __
                  }
          )

          Koan.Task(
              "Stale updates are refused",
              fun k ->
                  task {
                      let! first = order k 1L
                      let! _ = k.Repo.Update(first |> Changeset.edit (fun o -> { o with Total = 31m }))
                      // `first` still has the old version.
                      let! again = k.Repo.Update(first |> Changeset.edit (fun o -> { o with Total = 32m }))

                      let stale =
                          match again with
                          | Error(WriteError.Stale _) -> true
                          | _ -> false

                      stale |> shouldBe __
                  }
          )

          Koan.Task(
              "Infrastructure is not a domain error",
              fun k ->
                  task {
                      // orders_total_check is not declared on the changeset, so it stays a database failure.
                      let! r = k.Repo.Insert(Changeset.insert (New.order None Draft -1m))

                      match r with
                      | Error(WriteError.Failed f) -> f.Kind |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          )

          Koan.Sync(
              "Each call takes its own kind of changeset",
              fun k ->
                  let refusal =
                      try
                          k.Repo.Update(Changeset.insert (New.product "X" "X")) |> ignore
                          ""
                      with :? ArgumentException as e ->
                          e.Message

                  refusal.StartsWith "repo.Update was given a changeset for insert" |> shouldBe __
          ) ]
