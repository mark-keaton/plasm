/// Lesson 10: About changesets.
module Plasm.Koans.Lessons.L10_Changesets

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

          Koan.Task(
              "Invalid changes are never written",
              fun repo ->
                  task {
                      // Plasm validates nothing itself: the database's constraints
                      // decide. Db.orders' table checks `total >= 0`
                      // (orders_total_check), so a negative total is refused, and
                      // the transaction writes nothing.
                      let! r = repo.Transact(Tx.insert (New.order None Draft -1m): Tx<Order, unit>)

                      let refusedField =
                          match r with
                          | Error e ->
                              match e.Failure with
                              | TxFailure.InvalidChanges(_, errors) -> (List.head errors).Field
                              | _ -> "another failure"
                          | Ok _ -> "nothing was refused"

                      refusedField |> shouldBe __
                      let! n = repo.Count(Query.from Db.orders)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Constraints become field errors",
              fun repo ->
                  task {
                      // Db.customers states `unique (fun c -> c.Email)`, and the table's
                      // constraint has PostgreSQL's default name, customers_email_key.
                      // A duplicate comes back as the field's error; nothing is
                      // declared on the changeset.
                      let again = Changeset.insert (New.customer "Ada again" "ada@example.com")

                      let! r = repo.Insert again

                      match r with
                      | Error(WriteError.Invalid cs) -> cs.Errors |> List.map (fun e -> e.Field) |> shouldBe __
                      | other -> failwithf "expected invalid changes, got %A" other
                  }
          ) ]
