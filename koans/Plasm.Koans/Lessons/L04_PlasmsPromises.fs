/// Lesson 04: Plasm's promises.
module Plasm.Koans.Lessons.L04_PlasmsPromises

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "Plasm's promises"
        [ Koan.Task(
              "Records stay plain F#",
              fun k ->
                  task {
                      // A record read from the database is an ordinary F# value: it
                      // is not watched, and changing a copy writes nothing.
                      // (Lesson 05 goes deeper.)
                      let! products = k.Repo.All(Query.from Db.products)
                      let widget = ok products |> List.find (fun p -> p.Sku = "WIDGET")
                      let _renamed = { widget with Name = "Blue widget" }
                      widget.Name |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "Nothing runs until you ask",
              fun k ->
                  // A query and a changeset (a description of a write) are values.
                  // Only a repo call sends anything. (Lessons 06 and 10 go deeper.)
                  let _drafts = plasm { for o in Db.orders do where (o.Status = Draft) }
                  let _dan = Changeset.insert (New.customer "Dan" "dan@example.com")
                  k.Commands.Length |> shouldBe __
          )

          Koan.Task(
              "No hidden queries",
              fun k ->
                  task {
                      // An order's Lines is a relationship: Loaded with the lines, or
                      // NotLoaded. Reading a NotLoaded relationship never queries the
                      // database behind your back. (Lesson 09 goes deeper.)
                      let! orders = k.Repo.All(Query.from Db.orders)
                      let first = ok orders |> List.find (fun o -> o.Id = OrderId 1L)
                      first.Lines |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Nothing is evaluated behind your back",
              fun k ->
                  task {
                      // shout is ordinary F#, which SQL cannot run. Plasm refuses the
                      // query rather than reading every customer to run it in memory.
                      // (Lesson 07 goes deeper.)
                      let shout (s: string) = s.ToUpperInvariant() + "!"
                      let! r = k.Repo.All(plasm { for c in Db.customers do where (shout c.Name = "ADA!") })
                      Result.isError r |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure is a value",
              fun k ->
                  task {
                      // Ada's email is taken, and the customers table's email has a
                      // unique constraint (see the schema). The failure comes back as
                      // an Error you can match on; nothing is thrown. (Lesson 20 goes deeper.)
                      let! r = k.Repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))

                      match err r with
                      | WriteError.Failed failure -> failure.Constraint |> shouldBe __
                      | other -> failwithf "expected a database failure, got %A" other
                  }
          )

          Koan.Task(
              "Only what you change is written",
              fun k ->
                  task {
                      // Changeset.edit describes a change to a record you hold. The
                      // UPDATE sets only the fields that changed. (Lesson 11 goes deeper.)
                      let! drafts = k.Repo.All(plasm { for o in Db.orders do where (o.Status = Draft) })
                      let draft = ok drafts |> List.head
                      let! _ = k.Repo.Update(draft |> Changeset.edit (fun o -> { o with Status = Published }))
                      let update = List.last k.Commands
                      update.Contains "\"status\" = " |> shouldBe __
                      update.Contains "\"total\" = " |> shouldBe __
                  }
          )

          Koan.Sync(
              "The model is checked before any query",
              fun k ->
                  // Model.build checks every schema once, when the model is made.
                  // Without scalar mappings (Db.scalars), it cannot store a ProductId.
                  // Each error names the record and the field (Property) at fault.
                  // (Lesson 05 goes deeper.)
                  match Model.build [] [ Db.products ] with
                  | Ok _ -> failwith "expected a configuration error"
                  | Error errors ->
                      (errors |> List.map (fun e -> e.Property) |> List.distinct) |> shouldBe __
                      k.Commands.Length |> shouldBe __
          )

          Koan.Task(
              "Queries mean what F# means",
              fun k ->
                  task {
                      // In F#, `o.CustomerId = None` is true for an order with no
                      // customer. In the query it means the same: SQL's IS NULL.
                      // (Lesson 07 goes deeper.)
                      let noCustomer = plasm { for o in Db.orders do where (o.CustomerId = None) }
                      (k.Sql noCustomer).Contains "IS NULL" |> shouldBe __
                      let! n = k.Repo.Count noCustomer
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Relationships load in batches",
              fun k ->
                  task {
                      // preload asks for a relationship to be loaded with the rows.
                      // The lines of all four orders come in one command, not one
                      // command per order. (Lesson 09 goes deeper.)
                      let! _ = k.Repo.All(plasm { for o in Db.orders do preload o.Lines })
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Tests roll back",
              fun k ->
                  task {
                      // repo.Sandbox runs code in a transaction that is rolled back
                      // when it ends: every koan runs in one. (Lesson 21 goes deeper.)
                      let! inside =
                          k.Repo.Sandbox(fun repo ->
                              task {
                                  let! _ = repo.Insert(Changeset.insert (New.customer "Dan" "dan@example.com"))
                                  return! repo.Count(Query.from Db.customers)
                              })

                      let! after = k.Repo.Count(Query.from Db.customers)
                      ok inside |> shouldBe __
                      ok after |> shouldBe __
                  }
          ) ]
