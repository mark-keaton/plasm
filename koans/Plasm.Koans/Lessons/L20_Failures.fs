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
              fun k ->
                  task {
                      // Every repository call returns a Result: Ok with the value, or Error with what went wrong.
                      let! r = k.Repo.One(plasm { for c in Db.customers do where (c.Name = "Zed") })
                      r |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure is an Error you can match on",
              fun k ->
                  task {
                      // customers.email is unique (see the schema), and Ada already has this email.
                      let! r = k.Repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))

                      let outcome =
                          match r with
                          | Ok _ -> "saved"
                          | Error _ -> "refused"

                      outcome |> shouldBe __
                  }
          )

          Koan.Task(
              "A failed write says what kind of failure",
              fun k ->
                  task {
                      // WriteError.Failed is a failure reported by the database. Its Kind is a DbFailureKind:
                      // UniqueViolation, ForeignKeyViolation, CheckViolation, NotNullViolation, and others.
                      let! r = k.Repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))

                      match r with
                      | Error(WriteError.Failed f) -> f.Kind |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          )

          Koan.Task(
              "A unique violation names its constraint",
              fun k ->
                  task {
                      let! r = k.Repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))

                      match r with
                      // Constraint is the name of the database constraint that refused the row, if any.
                      | Error(WriteError.Failed f) -> f.Constraint |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          )

          Koan.Task(
              "Failures are values",
              fun k ->
                  task {
                      let kept = ref Unchecked.defaultof<Repo>

                      do!
                          k.Repo.Sandbox(fun inner ->
                              kept.Value <- inner
                              Task.FromResult())

                      // The sandbox has ended; its repository returns an error rather than throwing.
                      let! r = kept.Value.All(Query.from Db.customers)

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
              fun k ->
                  task {
                      let! _ = k.Repo.ExecuteSql $"update orders set status = 'lost' where id = 1"
                      let! r = k.Repo.All(Query.from Db.orders)

                      match r with
                      | Error(DbError.Materialization m) -> (m.Model, m.Property) |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "SQL mismatches name the column",
              fun k ->
                  task {
                      // Customer needs an email column, and this query does not return one.
                      let! r = k.Repo.Sql<Customer>($"SELECT id, name FROM customers")

                      match r with
                      | Error e -> (string e).Contains "Email" |> shouldBe __
                      | Ok rows -> failwithf "expected an error, got %A" rows
                  }
          )

          Koan.Task(
              "Too many rows is about the data",
              fun k ->
                  task {
                      let! r = k.Repo.One(plasm { for o in Db.orders do where (o.Status = Published) })
                      r |> shouldBe __
                  }
          )

          Koan.Task(
              "Rejections keep their type",
              fun k ->
                  task {
                      let! r = k.Repo.Transact(transaction { return! Tx.fail "check stock" (OutOfStock "GIZMO") })

                      match (err r).Failure with
                      | StepFailure.Rejected rejection -> rejection.As<StockError>() |> shouldBe __
                      | other -> failwithf "expected a rejection, got %A" other
                  }
          )

          Koan.Sync(
              "Every union is qualified",
              fun _ ->
                  let cs = Changeset.insert (New.customer "Eve" "eve@example.com")
                  cs.Action |> shouldBe __
          ) ]
