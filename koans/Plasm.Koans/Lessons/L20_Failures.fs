/// Lesson 20: About failures.
module Plasm.Koans.Lessons.L20_Failures

open System.Threading.Tasks
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// Your own union may use the names Plasm uses for its errors.
type Problem =
    | Database
    | Translation

/// A domain error a transaction step can fail with.
type StockError = OutOfStock of sku: string

let lesson =
    Lesson.make
        "About failures"
        [ Koan.Task(
              "Nothing found is not a failure",
              fun repo ->
                  task {
                      // Every repository call returns a Result: Ok with the value, or Error with what went wrong.
                      let! r = repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Zed") })
                      r |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure is an Error you can match on",
              fun repo ->
                  task {
                      // customers.email is unique ([<Unique>] in Shop.fs), and Ada already has this email.
                      let! r = repo.Transact(Tx.insert (New.customer "Ada again" "ada@example.com"): Tx<Customer, unit>)

                      let outcome =
                          match r with
                          | Ok _ -> "saved"
                          | Error _ -> "refused"

                      outcome |> shouldBe __
                  }
          )

          Koan.Task(
              "A failed write says what kind of failure",
              fun repo ->
                  task {
                      // TxFailure.Failed(DbError.Database f) is a failure reported by the database.
                      // Its Kind is a DbFailureKind: UniqueViolation, ForeignKeyViolation,
                      // CheckViolation, NotNullViolation, and others. The orders table's rule
                      // orders_cancelled_has_reason (a cancelled order needs a reason) spans two
                      // columns, so it is no single field's error.
                      let! r = repo.Transact(Tx.insert (New.order None Cancelled 5m): Tx<Order, unit>)

                      match (err r).Failure with
                      | TxFailure.Failed(DbError.Database f) -> f.Kind |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          )

          Koan.Task(
              "Your refusals and Plasm's are apart",
              fun repo ->
                  task {
                      // A transaction fails with its own reasons (TxFailure.Rejected, of its
                      // own error type) or with Plasm's: the database's (TxFailure.Failed),
                      // a stale copy (TxFailure.Conflict) or a refused field
                      // (TxFailure.InvalidChanges). whose tells them apart.
                      let whose (r: Result<Order, TransactionError<StockError>>) =
                          match r with
                          | Ok _ -> "nobody's"
                          | Error e ->
                              match e.Failure with
                              | TxFailure.Rejected _ -> "the shop's"
                              | _ -> "Plasm's"

                      let! outOfStock = repo.Transact(Tx.fail (OutOfStock "GIZMO"))
                      let! cancelled = repo.Transact(Tx.insert (New.order None Cancelled 5m))
                      whose outOfStock |> shouldBe __
                      whose cancelled |> shouldBe __
                  }
          )

          Koan.Task(
              "Failures are values",
              fun repo ->
                  task {
                      let kept = ref Unchecked.defaultof<Repo>

                      do!
                          repo.Sandbox(fun inner ->
                              kept.Value <- inner
                              Task.FromResult())

                      // The sandbox has ended; its repository returns an error rather than throwing.
                      let! r = kept.Value.All(Query.from (tableOf<Customer>))

                      match r with
                      | Error(DbError.Database f) -> f.Kind |> shouldBe __
                      | other -> failwithf "expected a database error, got %A" other
                  }
          )

          Koan.Sync(
              "Error cases are qualified",
              fun _ ->
                  let classify (e: DbError) =
                      match e with
                      | DbError.Database _ -> Database
                      | _ -> Translation

                  classify (DbError.Translation(TranslationError.InvalidQuery "example")) |> shouldBe __
          )

          Koan.Task(
              "Unreadable rows are named",
              fun repo ->
                  task {
                      let lose: Tx<int, unit> = Tx.executeSql $"update orders set status = 'lost' where id = 1"
                      let! _ = repo.Transact lose
                      let! r = repo.All(Query.from (tableOf<Order>))

                      match r with
                      | Error(DbError.Materialization m) -> (m.Model, m.Property) |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "SQL mismatches name the column",
              fun repo ->
                  task {
                      // Customer needs an email column, and this query does not return one.
                      let! r = repo.Sql<Customer>($"SELECT id, name FROM customers")

                      match r with
                      | Error e -> (string e).Contains "Email" |> shouldBe __
                      | Ok rows -> failwithf "expected an error, got %A" rows
                  }
          )

          Koan.Task(
              "Too many rows is about the data",
              fun repo ->
                  task {
                      let! r = repo.One(plasm { for o in tableOf<Order> do where (o.Status = Published) })
                      r |> shouldBe __
                  }
          )

          Koan.Task(
              "Rejections keep their type",
              fun repo ->
                  task {
                      let! r = repo.Transact(transaction { return! Tx.fail (OutOfStock "GIZMO") })

                      // A transaction's own reasons to fail have its own type, here StockError.
                      match (err r).Failure with
                      | TxFailure.Rejected reason -> reason |> shouldBe __
                      | other -> failwithf "expected a rejection, got %A" other
                  }
          )

          Koan.Sync(
              "Every union is qualified",
              fun _ ->
                  // PaymentStatus (in Shop.fs) has a case Failed, and so does Plasm's
                  // TxFailure. Plasm's cases are always written with their type name, so
                  // a plain Failed is your domain's, and TxFailure.Failed is Plasm's.
                  let paid (s: PaymentStatus) =
                      match s with
                      | Succeeded -> true
                      | Pending
                      | Failed -> false

                  let databaseFailed (f: TxFailure<unit>) =
                      match f with
                      | TxFailure.Failed _ -> true
                      | _ -> false

                  paid Failed |> shouldBe __
                  databaseFailed (TxFailure.Conflict "Order") |> shouldBe __
          ) ]
