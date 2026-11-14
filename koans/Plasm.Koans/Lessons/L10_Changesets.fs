/// Lesson 10: About changesets.
module Plasm.Koans.Lessons.L10_Changesets

open System.Collections.Generic
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private ada = { New.customer "Ada" "ada@example.com" with Id = CustomerId 1L }

let lesson =
    Lesson.make
        "About changesets"
        [ Koan.Sync(
              "A changeset leaves its input alone",
              fun _ ->
                  let cs = ada |> Changeset.edit (fun c -> { c with Name = "Ada L." })
                  ada.Name |> shouldBe __
                  cs.Changes |> List.map fst |> shouldBe __
          )

          Koan.Sync(
              "Unchanged and cleared differ",
              fun _ ->
                  let cancelled = { New.order None Cancelled 5m with Id = OrderId 4L; CancellationReason = Some "duplicate" }
                  let cs = cancelled |> Changeset.edit (fun o -> { o with CancellationReason = None })
                  // Every other field is unchanged, so it is not a change at all.
                  cs.Changes |> shouldBe __
          )

          Koan.Sync(
              "Errors can be inspected",
              fun k ->
                  let cs =
                      Changeset.insert (New.customer "" "not an email")
                      |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                      |> Changeset.validateFormat ((fun c -> c.Email), "^[^@\\s]+@[^@\\s]+$")

                  cs.IsValid |> shouldBe __
                  cs.Errors |> List.map (fun e -> e.Field) |> shouldBe __
                  k.Commands.Length |> shouldBe __
          )

          Koan.Sync(
              "Input cannot touch protected fields",
              fun _ ->
                  let form = readOnlyDict [ "name", "Mallory"; "email", "ada@new.example.com"; "id", "99" ]
                  let cs = Changeset.update ada |> Changeset.cast (form, fun c -> c.Email)
                  cs.Changes |> List.map fst |> shouldBe __
          )

          Koan.Task(
              "Invalid changes are never written",
              fun k ->
                  task {
                      let invalid = Changeset.insert (New.customer "" "dan@example.com") |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                      let! r = k.Repo.Insert invalid

                      let isInvalid =
                          match r with
                          | Error(WriteError.Invalid _) -> true
                          | _ -> false

                      isInvalid |> shouldBe __
                      let! n = k.Repo.Count(Query.from Db.customers)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Constraints become field errors",
              fun k ->
                  task {
                      let again =
                          Changeset.insert (New.customer "Ada again" "ada@example.com")
                          |> Changeset.uniqueConstraint ((fun c -> c.Email), "customers_email_key")

                      let! r = k.Repo.Insert again

                      match r with
                      | Error(WriteError.Invalid cs) -> cs.Errors |> List.map (fun e -> e.Field) |> shouldBe __
                      | other -> failwithf "expected invalid changes, got %A" other
                  }
          ) ]
