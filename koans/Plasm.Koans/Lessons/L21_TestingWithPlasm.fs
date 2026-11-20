/// Lesson 21: About testing with Plasm.
module Plasm.Koans.Lessons.L21_TestingWithPlasm

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private customers (repo: Repo) =
    task {
        let! n = repo.Count(Query.from (tableOf<Customer>))
        return ok n
    }

/// A step that inserts Dan, a new customer.
let private addDan: Tx<Customer, unit> = Tx.insert (New.customer "Dan" "dan@example.com")

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
                                  let! _ = repo.Transact addDan
                                  return! customers repo
                              })

                      inside |> shouldBe __
                  }
          )

          Koan.Task(
              "A sandbox's insert is gone afterwards",
              fun repo ->
                  task {
                      let! _ = repo.Sandbox(fun repo -> repo.Transact addDan)
                      // The sandbox has ended: count with the outer repository.
                      let! after = customers repo
                      after |> shouldBe __
                  }
          )

          Koan.Task(
              "Every query is checked before it runs",
              fun repo ->
                  task {
                      // Repo.Start translates every query an assembly defines as a value
                      // in its modules, runs none of them, and by default fails start-up
                      // naming those Plasm cannot translate. Warn reports each through
                      // OnProblem instead, and starts. Here it checks the shop's
                      // assembly (Shop.fs): Queries, whose queries translate, and
                      // Mistakes, whose one query, Mistakes.shouting, does not. A problem
                      // names its query in full, by module and value: Shop.fs is the
                      // module Plasm.Koans.Shop.
                      let found = ResizeArray<StartupProblem>()

                      let! _ =
                          Repo.Start(
                              Db.model,
                              repo.Provider,
                              queriesIn = typeof<Customer>.Assembly,
                              options =
                                  { RepoOptions.Default with
                                      Queries = StartupCheck.Warn
                                      Schema = StartupCheck.Ignore
                                      OnProblem = Some found.Add }
                          )

                      [ for p in found do
                            match p with
                            | StartupProblem.Query q -> q.Query
                            | _ -> () ]
                      |> shouldBe __

                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "The model prints the tables it expects",
              fun repo ->
                  // repo.Ddl() prints the tables the model expects, with the constraint
                  // names Plasm maps errors by. It runs nothing: it is the target for
                  // the migration you write.
                  let ddl = repo.Ddl()
                  ddl.Contains "constraint customers_email_key unique (email)" |> shouldBe __
                  ddl.Contains "constraint orders_customer_id_fkey foreign key (customer_id) references customers (id)" |> shouldBe __
                  sqlSent().Length |> shouldBe __
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
                      let! duplicate = repo.Transact(Tx.insert (New.customer "Ada again" "ada@example.com"): Tx<Customer, unit>)
                      Result.isError duplicate |> shouldBe __
                      let! later = repo.Transact addDan
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
                      // A SchemaIssue has a Kind, a SchemaIssueKind (MissingTable,
                      // MissingColumn, IncompatibleType, ...), the Model's name as text,
                      // and the Field when one is at fault, as an option.
                      let drop: Tx<int, unit> = Tx.executeSql $"alter table orders drop column cancellation_reason"
                      let! _ = repo.Transact drop
                      let! issues = repo.CheckSchema()
                      let issue = (ok issues).Head
                      (issue.Kind, issue.Model, issue.Field) |> shouldBe __
                  }
          ) ]
