/// Lesson 13: About transactions.
module Plasm.Koans.Lessons.L13_Transactions

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (k: KoanContext) =
    task {
        let! n = k.Repo.Count(Query.from Db.customers)
        return ok n
    }

let lesson =
    Lesson.make
        "About transactions"
        [ Koan.Task(
              "Steps compose with let!",
              fun k ->
                  task {
                      let signUp =
                          transaction {
                              let! dan = Tx.insert "customer" (Changeset.insert (New.customer "Dan" "dan@example.com"))
                              let! order = Tx.insert "order" (Changeset.insert (New.order (Some dan.Id) Draft 1m))
                              return order.CustomerId = Some dan.Id
                          }

                      let! r = k.Repo.Transact signUp
                      ok r |> shouldBe __
                      let! n = customers k
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure names its step",
              fun k ->
                  task {
                      let signUp =
                          transaction {
                              let! dan = Tx.insert "customer" (Changeset.insert (New.customer "Dan" "dan@example.com"))
                              // A negative total breaks orders_total_check.
                              let! _ = Tx.insert "order" (Changeset.insert (New.order (Some dan.Id) Draft -1m))
                              return ()
                          }

                      let! r = k.Repo.Transact signUp
                      let e = err r
                      e.Step |> shouldBe __
                      e.RolledBack |> shouldBe __
                      let! n = customers k
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "An empty transaction is fine",
              fun k ->
                  task {
                      let! r = k.Repo.Transact(transaction { return 42 })
                      r |> shouldBe __
                  }
          )

          Koan.Sync(
              "Retries are opt-in",
              fun _ ->
                  RetryPolicy.None.MaxAttempts |> shouldBe __
                  (RetryPolicy.OnSerializationFailure 3).MaxAttempts |> shouldBe __
          )

          Koan.Task(
              "Custom steps use the transaction",
              fun k ->
                  task {
                      let audit =
                          transaction {
                              let! _ =
                                  Tx.run "audit" (fun ctx ->
                                      task {
                                          use cmd = ctx.CreateCommand()
                                          cmd.CommandText <- "insert into products(sku, name) values ('AUDIT', 'Audit')"
                                          let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                          return (Ok(): Result<unit, string>)
                                      })

                              return! Tx.fail "stop" "changed my mind"
                          }

                      let! r = k.Repo.Transact audit
                      (err r).Step |> shouldBe __
                      let! audited = k.Repo.Count(plasm { for p in Db.products do where (p.Sku = "AUDIT") })
                      ok audited |> shouldBe __
                  }
          )

          Koan.Task(
              "Effects outside the database are not rolled back",
              fun k ->
                  task {
                      let emailsSent = ref 0

                      let tx =
                          transaction {
                              let! _ =
                                  Tx.run "email" (fun _ ->
                                      task {
                                          emailsSent.Value <- emailsSent.Value + 1
                                          return (Ok(): Result<unit, string>)
                                      })

                              return! Tx.fail "stop" "changed my mind"
                          }

                      let! _ = k.Repo.Transact tx
                      emailsSent.Value |> shouldBe __
                  }
          ) ]
