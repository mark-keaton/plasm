/// Lesson 13: About transactions.
module Plasm.Koans.Lessons.L13_Transactions

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (repo: Repo) =
    task {
        let! n = repo.Count(Query.from (tableOf<Customer>))
        return ok n
    }

/// A transaction of one step that inserts Dan. Tx<string, unit>: it gives a
/// string, and has no reasons of its own to fail (unit).
let private addDan: Tx<string, unit> =
    transaction {
        let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
        return dan.Name
    }

/// Why the shop refuses a change: its transactions' own error type.
type Refusal =
    | Missing of what: string
    | Refused of reason: string

/// The domain's decision, a plain function: an order may be cancelled with a
/// reason, unless it is already published.
let cancel (reason: string) (order: Order) : Result<Order, string> =
    if order.Status = Published then
        Error "a published order cannot be cancelled"
    else
        Ok { order with Status = Cancelled; CancellationReason = Some reason }

/// Read what the decision needs, let the domain decide, write what it decided.
let private cancelOrder (id: OrderId) : Tx<Order, Refusal> =
    transaction {
        let! order = Tx.one (plasm { for o in tableOf<Order> do where (o.Id = id) }) |> Tx.orReject (Missing "order")
        let! cancelled = cancel "duplicate" order |> Tx.ofResult Refused
        return! Tx.update (order, cancelled)
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
                      sqlSent().Length |> shouldBe __
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
                      // Action is a text: the step's verb and its record type, as
                      // "update Product" is for a Tx.update of a product. RolledBack lists
                      // the steps that ran before it, each undone.
                      e.Step.Action |> shouldBe __
                      e.RolledBack |> List.map (fun s -> s.Action) |> shouldBe __
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading inside a transaction",
              fun repo ->
                  task {
                      // Tx.one reads the one row a query matches (or None), and Tx.count
                      // counts rows, as steps: inside the transaction, so what they read
                      // is what the later steps change.
                      let look =
                          transaction {
                              let! ada = Tx.one (plasm { for c in tableOf<Customer> do where (c.Name = "Ada") })
                              let! orders = Tx.count (Query.from (tableOf<Order>))
                              return (ada |> Option.map (fun c -> c.Name)), orders
                          }

                      let! r = repo.Transact(look: Tx<_, unit>)
                      ok r |> shouldBe __
                  }
          )

          Koan.Task(
              "A missing row is a rejection",
              fun repo ->
                  task {
                      // A transaction's own reasons to fail are its error type: here
                      // Refusal, above under Given. Tx.orReject turns a None into one,
                      // and the transaction fails as TxFailure.Rejected, carrying it.
                      let zed =
                          Tx.one (plasm { for c in tableOf<Customer> do where (c.Name = "Zed") })
                          |> Tx.orReject (Missing "customer Zed")

                      let! r = repo.Transact zed
                      (err r).Failure |> shouldBe __
                  }
          )

          Koan.Task(
              "The domain decides, the transaction writes",
              fun repo ->
                  task {
                      // cancelOrder (above, under Given) reads an order, lets cancel decide,
                      // and writes what it decided. Tx.ofResult turns the decision's Error
                      // into a rejection. Order 2 is a draft; order 1 is published.
                      let! draft = repo.Transact(cancelOrder (OrderId 2L))
                      (ok draft).Status |> shouldBe __
                      let! published = repo.Transact(cancelOrder (OrderId 1L))
                      (err published).Failure |> shouldBe __
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
                  // A RetryPolicy says how often a transaction may be tried after a
                  // serialisation failure. MaxAttempts counts every attempt, the
                  // first included.
                  RetryPolicy.None.MaxAttempts |> shouldBe __
                  (RetryPolicy.OnSerializationFailure 3).MaxAttempts |> shouldBe __
          )

          Koan.Task(
              "Custom steps use the transaction",
              fun repo ->
                  task {
                      // Tx.run is a step you write: it is given the transaction's
                      // connection (ctx) and runs its own commands on it, inside the
                      // transaction. Tx.fail is a step too; a step of no record type
                      // has its verb alone as its Action.
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
                      let! audited = repo.Count(plasm { for p in tableOf<Product> do where (p.Sku = "AUDIT") })
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
