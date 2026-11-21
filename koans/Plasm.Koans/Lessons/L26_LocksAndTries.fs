/// Lesson 26: About locks and tries.
module Plasm.Koans.Lessons.L26_LocksAndTries

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (repo: Repo) =
    task {
        let! n = repo.Count(Query.from (tableOf<Customer>))
        return ok n
    }

let lesson =
    Lesson.make
        "About locks and tries"
        [ Koan.Task(
              "A transaction locks the rows it reads",
              fun repo ->
                  task {
                      // locking, the last clause of a query, locks the rows it reads until
                      // the transaction ends: RowLock.ForUpdate, ForNoKeyUpdate, ForShare or
                      // ForKeyShare. A locked query is read only by Tx.all and Tx.one, inside
                      // a transaction. Its SELECT ends FOR UPDATE OF its own table's name.
                      let drafts =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Status = Draft)
                                  locking RowLock.ForUpdate
                          }

                      let! r = repo.Transact(Tx.all drafts: Tx<Order list, unit>)
                      ok r |> List.map (fun o -> o.Id) |> shouldBe __
                      let select = sqlSent () |> List.find (fun s -> s.StartsWith "SELECT")
                      select.Contains "FOR UPDATE OF" |> shouldBe __
                  }
          )

          Koan.Task(
              "What an order owns is read, not locked",
              fun repo ->
                  task {
                      // The lock is on the query's own rows. What they own is loaded with
                      // them, in a command of its own, and is not locked.
                      let first =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Id = OrderId 1L)
                                  locking RowLock.ForUpdate
                          }

                      let! r = repo.Transact(Tx.all first: Tx<Order list, unit>)
                      (List.head (ok r)).Lines.Length |> shouldBe __
                      sqlSent () |> List.filter (fun s -> s.Contains "FOR UPDATE") |> List.length |> shouldBe __
                  }
          )

          Koan.Task(
              "A tried step's failure is a value",
              fun repo ->
                  task {
                      // Tx.attempt runs a step under a savepoint and gives a Result: Ok with
                      // what the step gave, or Error with its failure, what it wrote undone,
                      // and the transaction able to go on.
                      let register =
                          transaction {
                              let! tried = Tx.attempt (Tx.insert (New.customer "Ada again" "ada@example.com"))

                              match tried with
                              | Ok stored -> return [], stored
                              | Error(TxFailure.InvalidChanges(_, errors)) ->
                                  let! other = Tx.insert (New.customer "Ada again" "ada.2@example.com")
                                  return errors |> List.map (fun e -> e.Field), other
                              | Error _ -> return! Tx.fail (())
                          }

                      let! r = repo.Transact(register: Tx<string list * Customer, unit>)
                      let refused, stored = ok r
                      refused |> shouldBe __
                      stored.Email.Value |> shouldBe __
                  }
          )

          Koan.Task(
              "A tried step that succeeds is kept",
              fun repo ->
                  task {
                      // A step that succeeds under Tx.attempt is Ok, and what it wrote stays.
                      let tried: Tx<Result<Customer, TxFailure<unit>>, unit> = Tx.attempt (Tx.insert (New.customer "Dan" "dan@example.com"))
                      let! r = repo.Transact tried
                      Result.isOk (ok r) |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A tried Conflict is a value",
              fun repo ->
                  task {
                      // Any failure of a tried step is a value, a stale copy's Conflict
                      // among them. (A serialization failure, a deadlock or a lost
                      // connection ends the transaction itself, so it is not caught.)
                      let! read = repo.One(plasm { for o in tableOf<Order> do where (o.Id = OrderId 1L) })
                      let first = (ok read).Value
                      let! _ = repo.Transact(Tx.update (first, { first with Total = 31m }): Tx<Order, unit>)
                      let late: Tx<Result<Order, TxFailure<unit>>, unit> = Tx.attempt (Tx.update (first, { first with Total = 32m }))
                      let! r = repo.Transact late
                      ok r |> shouldBe __
                  }
          ) ]
