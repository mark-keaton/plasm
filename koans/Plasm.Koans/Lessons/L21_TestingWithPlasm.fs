/// Lesson 21: About testing with Plasm.
module Plasm.Koans.Lessons.L21_TestingWithPlasm

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (repo: Repo) =
    task {
        let! n = repo.Count(Query.from Db.customers)
        return ok n
    }

let lesson =
    Lesson.make
        "About testing with Plasm"
        [ Koan.Task(
              "A sandbox gives back its body's result",
              fun repo ->
                  task {
                      // A sandbox runs a function with its own repository and undoes all its changes when the
                      // function ends. The sandbox returns whatever the function returns.
                      // `customers` (top of this file) counts the Customers.
                      let! counted = repo.Sandbox(fun repo -> customers repo)
                      counted |> shouldBe __
                  }
          )

          Koan.Task(
              "An insert is visible inside its sandbox",
              fun repo ->
                  task {
                      let! inside =
                          repo.Sandbox(fun repo ->
                              task {
                                  let! _ = repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                                  return! customers repo
                              })

                      inside |> shouldBe __
                  }
          )

          Koan.Task(
              "A sandbox's insert is gone afterwards",
              fun repo ->
                  task {
                      let! _ = repo.Sandbox(fun repo -> repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com")))
                      // The sandbox has ended: count with the outer repository.
                      let! after = customers repo
                      after |> shouldBe __
                  }
          )

          Koan.Task(
              "A matching model has no schema issues",
              fun repo ->
                  task {
                      // CheckSchema compares the model (Db.model in Shop.fs) with the database's tables and
                      // columns, and lists every mismatch it finds.
                      let! issues = repo.CheckSchema()
                      ok issues |> List.length |> shouldBe __
                  }
          )

          Koan.Task(
              "A sandbox rolls everything back",
              fun repo ->
                  task {
                      let! inside =
                          repo.Sandbox(fun repo ->
                              task {
                                  let! _ = repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                                  return! customers repo
                              })

                      let! after = customers repo
                      (inside, after) |> shouldBe __
                  }
          )

          Koan.Task(
              "Transactions keep their meaning in a sandbox",
              fun repo ->
                  task {
                      let failing =
                          transaction {
                              let! _ = Tx.insert (New.customer "Dan" "dan@example.com")
                              return! Tx.fail "changed my mind"
                          }

                      let! _ = repo.Transact failing
                      let! n = customers repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A database error does not end a sandbox",
              fun repo ->
                  task {
                      // Every koan already runs in a sandbox: repo is one.
                      let! duplicate = repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))
                      Result.isError duplicate |> shouldBe __
                      let! later = repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                      Result.isOk later |> shouldBe __
                  }
          )

          Koan.Task(
              "The model can be checked against the database",
              fun repo ->
                  task {
                      let! issues = repo.CheckSchema()
                      issues |> shouldBe __
                  }
          )

          Koan.Task(
              "Drift is reported precisely",
              fun repo ->
                  task {
                      // DDL is transactional in PostgreSQL, so the sandbox undoes this too.
                      let! _ = repo.ExecuteSql $"alter table orders drop column cancellation_reason"
                      let! issues = repo.CheckSchema()
                      let issue = (ok issues).Head
                      (issue.Kind, issue.Model, issue.Field) |> shouldBe __
                  }
          ) ]
