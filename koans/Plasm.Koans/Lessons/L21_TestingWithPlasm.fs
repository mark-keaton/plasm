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
              "A sandbox rolls everything back",
              fun k ->
                  task {
                      let! inside =
                          k.Repo.Sandbox(fun repo ->
                              task {
                                  let! _ = repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                                  return! customers repo
                              })

                      let! after = customers k.Repo
                      (inside, after) |> shouldBe __
                  }
          )

          Koan.Task(
              "Transactions keep their meaning in a sandbox",
              fun k ->
                  task {
                      let failing =
                          transaction {
                              let! _ = Tx.insert "customer" (Changeset.insert (New.customer "Dan" "dan@example.com"))
                              return! Tx.fail "stop" "changed my mind"
                          }

                      let! _ = k.Repo.Transact failing
                      let! n = customers k.Repo
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "A database error does not end a sandbox",
              fun k ->
                  task {
                      // Every koan already runs in a sandbox: k.Repo is one.
                      let! duplicate = k.Repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))
                      Result.isError duplicate |> shouldBe __
                      let! later = k.Repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                      Result.isOk later |> shouldBe __
                  }
          )

          Koan.Task(
              "The model can be checked against the database",
              fun k ->
                  task {
                      let! issues = k.Repo.CheckSchema()
                      issues |> shouldBe __
                  }
          )

          Koan.Task(
              "Drift is reported precisely",
              fun k ->
                  task {
                      // DDL is transactional in PostgreSQL, so the sandbox undoes this too.
                      let! _ = k.Repo.ExecuteSql $"alter table orders drop column cancellation_reason"
                      let! issues = k.Repo.CheckSchema()
                      let issue = (ok issues).Head
                      (issue.Kind, issue.Model, issue.Field) |> shouldBe __
                  }
          ) ]
