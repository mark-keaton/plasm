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
              "A changeset starts from a record",
              fun _ ->
                  // A changeset describes one change to one record; building it writes nothing.
                  // Its Action says what saving it will do: ChangeAction.Insert, Update or Delete.
                  let adding = Changeset.insert (New.customer "Dan" "dan@example.com")
                  let changing = Changeset.update ada
                  adding.Action |> shouldBe __
                  changing.Action |> shouldBe __
          )

          Koan.Sync(
              "Changes are visible before saving",
              fun _ ->
                  // Changeset.edit makes an update changeset from the fields your function alters.
                  let cs = ada |> Changeset.edit (fun c -> { c with Email = "ada@new.example.com" })
                  // Proposed is Some record: the original with the changes applied.
                  let proposed = cs.Proposed.Value
                  proposed.Email |> shouldBe __
                  proposed.Name |> shouldBe __
          )

          Koan.Sync(
              "A valid changeset has no errors",
              fun _ ->
                  // A validation checks a proposed value; validateLength checks a string's length.
                  let cs = Changeset.insert (New.customer "Dan" "dan@example.com") |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                  cs.IsValid |> shouldBe __
                  cs.Errors |> shouldBe __
          )

          Koan.Sync(
              "A validation adds an error",
              fun _ ->
                  let cs = Changeset.insert (New.customer "" "dan@example.com") |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                  // A failed validation adds a FieldError; its Field names the field it is about.
                  cs.IsValid |> shouldBe __
                  (List.head cs.Errors).Field |> shouldBe __
          )

          Koan.Sync(
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
              fun repo ->
                  let cs =
                      Changeset.insert (New.customer "" "not an email")
                      |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                      |> Changeset.validateFormat ((fun c -> c.Email), "^[^@\\s]+@[^@\\s]+$")

                  cs.IsValid |> shouldBe __
                  cs.Errors |> List.map (fun e -> e.Field) |> shouldBe __
                  (commands ()).Length |> shouldBe __
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
              fun repo ->
                  task {
                      let invalid = Changeset.insert (New.customer "" "dan@example.com") |> Changeset.validateLength ((fun c -> c.Name), min = 1)
                      let! r = repo.Insert invalid

                      let isInvalid =
                          match r with
                          | Error(WriteError.Invalid _) -> true
                          | _ -> false

                      isInvalid |> shouldBe __
                      let! n = repo.Count(Query.from Db.customers)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Constraints become field errors",
              fun repo ->
                  task {
                      let again =
                          Changeset.insert (New.customer "Ada again" "ada@example.com")
                          |> Changeset.uniqueConstraint ((fun c -> c.Email), "customers_email_key")

                      let! r = repo.Insert again

                      match r with
                      | Error(WriteError.Invalid cs) -> cs.Errors |> List.map (fun e -> e.Field) |> shouldBe __
                      | other -> failwithf "expected invalid changes, got %A" other
                  }
          ) ]
