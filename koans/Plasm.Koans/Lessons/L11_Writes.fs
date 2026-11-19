/// Lesson 11: About writes.
module Plasm.Koans.Lessons.L11_Writes

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private order (repo: Repo) (id: int64) =
    task {
        let! o = repo.One(plasm { for o in tableOf<Order> do where (o.Id = OrderId id) })
        return (ok o).Value
    }

/// Why a transaction failed, or None if it succeeded.
let private failure (r: Result<'T, TransactionError<unit>>) =
    match r with
    | Error e -> Some e.Failure
    | Ok _ -> None

let lesson =
    Lesson.make
        "About writes"
        [ Koan.Task(
              "Inserting adds a row",
              fun repo ->
                  task {
                      // Every write goes through a transaction: a description of steps
                      // that repo.Transact runs, all or nothing. Tx.insert is a step that
                      // inserts a record and gives it back as stored. Tx<Customer, unit>
                      // says the transaction gives a Customer and has no reasons of its
                      // own to fail (unit); a single step needs to be told that.
                      let addDan: Tx<Customer, unit> = Tx.insert (New.customer "Dan" "dan@example.com")
                      let! saved = repo.Transact addDan
                      (ok saved).Name |> shouldBe __
                      let! n = repo.Count(Query.from (tableOf<Customer>))
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "An insert assigns the key",
              fun repo ->
                  task {
                      let! saved = repo.Transact(Tx.insert (New.customer "Dan" "dan@example.com"): Tx<Customer, unit>)
                      // The database chose Dan's key; the stored record carries it, so it finds the row again.
                      let danId = (ok saved).Id
                      let! found = repo.One(plasm { for c in tableOf<Customer> do where (c.Id = danId) })
                      (ok found |> Option.map (fun c -> c.Name)) |> shouldBe __
                  }
          )

          Koan.Task(
              "An update returns the new record",
              fun repo ->
                  task {
                      // Tx.update (original, changed) changes the record you read into the
                      // one you made from it with `with`, and gives it back as stored.
                      let! draft = order repo 2L
                      let! updated = repo.Transact(Tx.update (draft, { draft with Total = 15m }): Tx<Order, unit>)
                      (ok updated).Total |> shouldBe __
                      // Records are values: the one you read is not changed.
                      draft.Total |> shouldBe __
                  }
          )

          Koan.Task(
              "The version goes up on update",
              fun repo ->
                  task {
                      // tableOf<Order> declares a version field: Version counts the updates of the row.
                      let! draft = order repo 2L
                      let! updated = repo.Transact(Tx.update (draft, { draft with Status = Published }): Tx<Order, unit>)
                      draft.Version |> shouldBe __
                      (ok updated).Version |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting a record",
              fun repo ->
                  task {
                      // Tx.delete deletes the record you read.
                      let customers = Query.from (tableOf<Customer>)
                      let! cy = repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Cy") })
                      let goodbye: Tx<Customer, unit> = Tx.delete (ok cy).Value
                      let! before = repo.Count customers
                      ok before |> shouldBe __
                      let! _ = repo.Transact goodbye
                      let! after = repo.Count customers
                      ok after |> shouldBe __
                  }
          )

          Koan.Task(
              "Inserting returns the stored record",
              fun repo ->
                  task {
                      let sprocket = New.product "SPROCKET" "Sprocket"
                      let! saved = repo.Transact(Tx.insert sprocket: Tx<Product, unit>)
                      // The database generated a key (its value depends on earlier inserts).
                      ((ok saved).Id <> ProductId 0L) |> shouldBe __
                      sprocket.Id |> shouldBe __
                  }
          )

          Koan.Task(
              "Updates write only the changes",
              fun repo ->
                  task {
                      let! draft = order repo 2L
                      let! _ = repo.Transact(Tx.update (draft, { draft with Status = Published }): Tx<Order, unit>)
                      let update = sqlSent () |> List.find (fun c -> c.StartsWith "UPDATE")
                      // Which columns does the UPDATE set?
                      update.Contains "\"status\" = " |> shouldBe __
                      update.Contains "\"total\" = " |> shouldBe __
                  }
          )

          Koan.Task(
              "Stale updates are refused",
              fun repo ->
                  task {
                      // An update checks that the row is still the one you read: for a
                      // record with a version, by its version. failure is at the top of
                      // this lesson.
                      let! first = order repo 1L
                      let! _ = repo.Transact(Tx.update (first, { first with Total = 31m }): Tx<Order, unit>)
                      // `first` still has the old version: this change was made from a stale copy.
                      let! again = repo.Transact(Tx.update (first, { first with Total = 32m }): Tx<Order, unit>)
                      failure again |> shouldBe __
                  }
          )

          Koan.Task(
              "Without a version, every column is checked",
              fun repo ->
                  task {
                      // A Customer has no version field, so an update checks every column
                      // of the record it was made from. Another writer renamed Ada first.
                      let! ada = repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Ada") })
                      let ada = (ok ada).Value
                      let! _ = repo.Transact(Tx.update (ada, { ada with Name = "Ada L." }): Tx<Customer, unit>)
                      let! late = repo.Transact(Tx.update (ada, { ada with Name = "Ada B." }): Tx<Customer, unit>)
                      failure late |> shouldBe __
                  }
          )

          Koan.Task(
              "A copy from elsewhere says which version it saw",
              fun repo ->
                  task {
                      // A copy that comes back from a browser or another service carries
                      // the version it was read at. Tx.oneAsSeen reads the record as that
                      // copy saw it: a stored version other than the one seen is a
                      // Conflict, before anything is decided on it.
                      let! draft = order repo 2L
                      let byId = plasm { for o in tableOf<Order> do where (o.Id = draft.Id) }
                      let! r = repo.Transact(Tx.oneAsSeen (byId, 7): Tx<Order option, unit>)
                      failure r |> shouldBe __
                      let! current = repo.Transact(Tx.oneAsSeen (byId, draft.Version): Tx<Order option, unit>)
                      (ok current).Value.Total |> shouldBe __
                  }
          ) ]
