/// Lesson 10: About constructors and constraints.
module Plasm.Koans.Lessons.L10_ConstructorsAndConstraints

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// The field errors a failed transaction reports, or [] for any other outcome.
let private fieldErrors (r: Result<'T, TransactionError<unit>>) =
    match r with
    | Error e ->
        match e.Failure with
        | TxFailure.InvalidChanges(_, errors) -> errors
        | _ -> []
    | Ok _ -> []

let lesson =
    Lesson.make
        "About constructors and constraints"
        [ Koan.Sync(
              "A constructor refuses bad input",
              fun _ ->
                  // Plasm validates no input. Validation is the domain's: an Email
                  // (in Shop.fs) can only be made by Email.create, which refuses text
                  // without an '@'.
                  Email.create "ada.example.com" |> err |> shouldBe __
                  (ok (Email.create "ada@example.com")).Value |> shouldBe __
          )

          Koan.Task(
              "What the domain refuses is refused when read",
              fun repo ->
                  task {
                      // Plasm reads an Email back through Email.create too. A stored
                      // email the domain refuses is a read error naming the field: never
                      // an Email that Email.create would not make.
                      let broken: Tx<int, unit> = Tx.executeSql $"update customers set email = 'nobody' where id = 3"
                      let! _ = repo.Transact broken
                      let! customers = repo.All(Query.from (tableOf<Customer>))

                      match customers with
                      | Error(DbError.Materialization m) -> (m.Property, m.Message) |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "A duplicate is the field's error",
              fun repo ->
                  task {
                      // The rest is the database's: its constraints. Customer's Email is
                      // marked [<Unique>] in Shop.fs, and the table's unique constraint has
                      // PostgreSQL's default name, customers_email_key. A duplicate comes
                      // back as the Email field's error. fieldErrors is at the top of this lesson.
                      // (repo.Transact runs a transaction and Tx.insert is a step that
                      // inserts a record: lesson 11 goes deeper.)
                      let addAdaAgain: Tx<Customer, unit> = Tx.insert (New.customer "Ada again" "ada@example.com")
                      let! r = repo.Transact addAdaAgain
                      fieldErrors r |> List.map (fun e -> e.Field, e.Kind) |> shouldBe __
                  }
          )

          Koan.Task(
              "A missing reference is the field's error",
              fun repo ->
                  task {
                      // An order's CustomerId refers to a customer, and the table's foreign
                      // key has the default name orders_customer_id_fkey. There is no
                      // customer 99.
                      let order: Tx<Order, unit> = Tx.insert (New.order (Some(CustomerId 99L)) Draft 1m)
                      let! r = repo.Transact order
                      fieldErrors r |> List.map (fun e -> e.Field, e.Message) |> shouldBe __
                  }
          )

          Koan.Task(
              "A broken check is the field's error",
              fun repo ->
                  task {
                      // The orders table checks `total >= 0`, in a check constraint with
                      // the default name orders_total_check.
                      let order: Tx<Order, unit> = Tx.insert (New.order None Draft -1m)
                      let! r = repo.Transact order
                      fieldErrors r |> List.map (fun e -> e.Kind) |> shouldBe __
                  }
          )

          Koan.Task(
              "Refused changes are never written",
              fun repo ->
                  task {
                      // A refused insert writes nothing: the transaction is rolled back.
                      let order: Tx<Order, unit> = Tx.insert (New.order None Draft -1m)
                      let! r = repo.Transact order
                      Result.isError r |> shouldBe __
                      let! n = repo.Count(Query.from (tableOf<Order>))
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "A rule across columns is no field's error",
              fun repo ->
                  task {
                      // The orders table has a rule across two columns: a cancelled order
                      // needs a reason. Its constraint, orders_cancelled_has_reason, has no
                      // field's default name, so breaking it stays a database failure.
                      let order: Tx<Order, unit> = Tx.insert (New.order None Cancelled 5m)
                      let! r = repo.Transact order

                      match (err r).Failure with
                      | TxFailure.Failed(DbError.Database f) -> f.Constraint |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          ) ]
