/// Lesson 11: About writes.
module Plasm.Koans.Lessons.L11_Writes

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
              "Inserting adds a row",
              fun k ->
                  task {
                      // repo.Insert takes an insert changeset, writes it, and returns the stored record.
                      let! stored = k.Repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                      (ok stored).Name |> shouldBe __
                      let! n = k.Repo.Count(Query.from Db.customers)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "An insert assigns the key",
              fun k ->
                  task {
                      let! stored = k.Repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                      // The database chose Dan's key; the stored record carries it, so it finds the row again.
                      let danId = (ok stored).Id
                      let! found = k.Repo.One(plasm { for c in Db.customers do where (c.Id = danId) })
                      (ok found |> Option.map (fun c -> c.Email)) |> shouldBe __
                  }
          )

          Koan.Task(
              "An update returns the new record",
              fun k ->
                  task {
                      let! draft = order k 2L
                      // repo.Update takes an update changeset and returns the record as stored.
                      let! updated = k.Repo.Update(draft |> Changeset.edit (fun o -> { o with Total = 15m }))
                      (ok updated).Total |> shouldBe __
                      // Records are values: the one you read is not changed.
                      draft.Total |> shouldBe __
                  }
          )

          Koan.Task(
              "The version goes up on update",
              fun k ->
                  task {
                      // Db.orders declares a version field: Version counts the updates of the row.
                      let! draft = order k 2L
                      let! updated = k.Repo.Update(draft |> Changeset.edit (fun o -> { o with Status = Published }))
                      draft.Version |> shouldBe __
                      (ok updated).Version |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting a record",
              fun k ->
                  task {
                      let customers = Query.from Db.customers
                      let! cy = k.Repo.One(plasm { for c in Db.customers do where (c.Name = "Cy") })
                      // Changeset.delete describes removing a record; only repo.Delete removes it.
                      let goodbye = Changeset.delete (ok cy).Value
                      let! before = k.Repo.Count customers
                      ok before |> shouldBe __
                      let! _ = k.Repo.Delete goodbye
                      let! after = k.Repo.Count customers
                      ok after |> shouldBe __
                  }
          )

          Koan.Task(
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
