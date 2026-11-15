/// Lesson 13: About transactions.
module Plasm.Koans.Lessons.L13_Transactions

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (repo: Repo) =
    task {
        let! n = repo.Count(Query.from Db.customers)
        return ok n
    }

/// A transaction of one step that inserts Dan. Tx<string, unit>: it gives a
/// string, and has no reasons of its own to fail (unit).
let private addDan: Tx<string, unit> =
    transaction {
        let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
        return dan.Name
    }

let lesson =
    Lesson.make
        "About transactions"
        [ Koan.Task(
              "A transaction does nothing until run",
              fun repo ->
                  task {
                      // A transaction is a value describing database steps that succeed or fail together.
                      // transaction { } builds it; Tx.insert a step that inserts a record.
                      let tx = addDan
                      (commands ()).Length |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Running a transaction commits it",
              fun repo ->
                  task {
                      // repo.Transact runs the steps and commits them: Ok carries what the transaction returns.
                      let! r = repo.Transact addDan
                      ok r |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "An Error rolls it back",
              fun repo ->
                  task {
                      let tx =
                          transaction {
                              let! _ = Tx.insert (New.customer "Dan" "dan@example.com")
                              // Tx.fail a step that always fails, with a reason.
                              return! Tx.fail "changed my mind"
                          }

                      let! r = repo.Transact tx
                      Result.isError r |> shouldBe __
                      // A failed step rolls back (undoes) the steps before it.
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A step's result flows to the next",
              fun repo ->
                  task {
                      let rename =
                          transaction {
                              let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
                              // let! gives the next step what this one stored: Dan, with his new key.
                              let! daniel = Tx.update (dan, { dan with Name = "Daniel" })
                              return daniel.Name
                          }

                      let! r = repo.Transact rename
                      ok r |> shouldBe __
                  }
          )

          Koan.Task(
              "Steps compose with let!",
              fun repo ->
                  task {
                      let signUp =
                          transaction {
                              let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
                              let! order = Tx.insert (New.order (Some dan.Id) Draft 1m)
                              return order.CustomerId = Some dan.Id
                          }

                      let! r = repo.Transact signUp
                      ok r |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure says where its step is written",
              fun repo ->
                  task {
                      let signUp =
                          transaction {
                              let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
                              // A negative total breaks orders_total_check.
                              let! _ = Tx.insert (New.order (Some dan.Id) Draft -1m)
                              return ()
                          }

                      let! r = repo.Transact signUp
                      let e = err r
                      // A failure gives the step that failed: what it did (Action), and
                      // where it is written (File and Line), so steps need no names.
                      e.Step.Action |> shouldBe __
                      e.RolledBack |> List.map (fun s -> s.Action) |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "An empty transaction is fine",
              fun repo ->
                  task {
                      let! r = repo.Transact(transaction { return 42 })
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
              fun repo ->
                  task {
                      let audit =
                          transaction {
                              let! _ =
                                  Tx.run (fun ctx ->
                                      task {
                                          use cmd = ctx.CreateCommand()
                                          cmd.CommandText <- "insert into products(sku, name) values ('AUDIT', 'Audit')"
                                          let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                          return (Ok(): Result<unit, string>)
                                      })

                              return! Tx.fail "changed my mind"
                          }

                      let! r = repo.Transact audit
                      (err r).Step.Action |> shouldBe __
                      let! audited = repo.Count(plasm { for p in Db.products do where (p.Sku = "AUDIT") })
                      ok audited |> shouldBe __
                  }
          )

          Koan.Task(
              "Effects outside the database are not rolled back",
              fun repo ->
                  task {
                      let emailsSent = ref 0

                      let tx =
                          transaction {
                              let! _ =
                                  Tx.run (fun _ ->
                                      task {
                                          emailsSent.Value <- emailsSent.Value + 1
                                          return (Ok(): Result<unit, string>)
                                      })

                              return! Tx.fail "changed my mind"
                          }

                      let! _ = repo.Transact tx
                      emailsSent.Value |> shouldBe __
                  }
          ) ]
