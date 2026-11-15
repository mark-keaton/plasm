/// Lesson 04: Plasm's promises.
module Plasm.Koans.Lessons.L04_PlasmsPromises

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// A record with a field Plasm cannot store: a Uri has no column type, and it
/// is not a wrapper Plasm knows how to store by convention.
[<Table "bookmarks">]
type Bookmark = { Id: int64; Address: System.Uri }

let lesson =
    Lesson.make
        "Plasm's promises"
        [ Koan.Task(
              "Records stay plain F#",
              fun repo ->
                  task {
                      // A record read from the database is an ordinary F# value: it
                      // is not watched, and changing a copy writes nothing.
                      // (Lesson 05 goes deeper.)
                      let! products = repo.All(Query.from (tableOf<Product>))
                      let widget = ok products |> List.find (fun p -> p.Sku = "WIDGET")
                      let _renamed = { widget with Name = "Blue widget" }
                      widget.Name |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Sync(
              "Nothing runs until you ask",
              fun repo ->
                  // A query and a transaction (a description of writes) are values.
                  // Only a repo call sends anything. (Lessons 06 and 13 go deeper.)
                  let _drafts = plasm { for o in tableOf<Order> do where (o.Status = Draft) }
                  let _addDan: Tx<Customer, unit> = Tx.insert (New.customer "Dan" "dan@example.com")
                  sqlSent().Length |> shouldBe __
          )

          Koan.Task(
              "No hidden queries",
              fun repo ->
                  task {
                      // An order's Lines are read with the order, every time: one
                      // command for the order, one for its lines. Reading o.Lines
                      // afterwards is reading a list; it never queries the database
                      // behind your back. (Lesson 09 goes deeper.)
                      let! first = repo.One(plasm { for o in tableOf<Order> do where (o.Id = OrderId 1L) })
                      let first = (ok first).Value
                      first.Lines.Length |> shouldBe __
                      first.Lines |> List.map (fun l -> l.UnitPrice) |> ignore
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Nothing is evaluated behind your back",
              fun repo ->
                  task {
                      // shout is ordinary F#, which SQL cannot run. Plasm refuses the
                      // query rather than reading every customer to run it in memory.
                      // (Lesson 07 goes deeper.)
                      let shout (s: string) = s.ToUpperInvariant() + "!"
                      let! r = repo.All(plasm { for c in tableOf<Customer> do where (shout c.Name = "ADA!") })
                      Result.isError r |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A failure is a value",
              fun repo ->
                  task {
                      // Ada's email is taken, and tableOf<Customer> states the email is
                      // unique. The failure comes back as an Error you can match on,
                      // here as invalid changes naming the field; nothing is thrown.
                      // (Lessons 10 and 20 go deeper.)
                      let addAdaAgain: Tx<Customer, unit> = Tx.insert (New.customer "Ada again" "ada@example.com")
                      let! r = repo.Transact addAdaAgain

                      match (err r).Failure with
                      | TxFailure.InvalidChanges(_, errors) -> errors |> List.map (fun e -> e.Field) |> shouldBe __
                      | other -> failwithf "expected invalid changes, got %A" other
                  }
          )

          Koan.Task(
              "Only what you change is written",
              fun repo ->
                  task {
                      // Tx.update (original, changed) describes a change to a record you
                      // read. The UPDATE sets only the fields that differ. (Lesson 11
                      // goes deeper.)
                      let! drafts = repo.All(plasm { for o in tableOf<Order> do where (o.Status = Draft) })
                      let draft = ok drafts |> List.head
                      let publish: Tx<Order, unit> = Tx.update (draft, { draft with Status = Published })
                      let! _ = repo.Transact publish
                      let update = List.last (sqlSent ())
                      update.Contains "\"status\" = " |> shouldBe __
                      update.Contains "\"total\" = " |> shouldBe __
                  }
          )

          Koan.Sync(
              "The model is checked before any query",
              fun repo ->
                  // Model.build checks every record type once, when the model is made.
                  // It cannot store a Bookmark's Address (above, under Given).
                  // Each error names the record and the field (Property) at fault.
                  // (Lesson 05 goes deeper.)
                  match Model.build [] [ typeof<Bookmark> ] with
                  | Ok _ -> failwith "expected a configuration error"
                  | Error errors ->
                      (errors |> List.map (fun e -> e.Property) |> List.distinct) |> shouldBe __
                      sqlSent().Length |> shouldBe __
          )

          Koan.Task(
              "Queries mean what F# means",
              fun repo ->
                  task {
                      // In F#, `o.CustomerId = None` is true for an order with no
                      // customer. In the query it means the same: SQL's IS NULL.
                      // (Lesson 07 goes deeper.)
                      let noCustomer = plasm { for o in tableOf<Order> do where (o.CustomerId = None) }
                      (ok (repo.Explain noCustomer)).Sql.Contains "IS NULL" |> shouldBe __
                      let! n = repo.Count noCustomer
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "What an order owns loads in one batch",
              fun repo ->
                  task {
                      // Reading all four orders reads all their lines too: in one
                      // more command, not one command per order. (Lesson 09 goes deeper.)
                      let! _ = repo.All(Query.from (tableOf<Order>))
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Tests roll back",
              fun repo ->
                  task {
                      // repo.Sandbox runs code in a transaction that is rolled back
                      // when it ends: every koan runs in one. (Lesson 21 goes deeper.)
                      let! inside =
                          repo.Sandbox(fun repo ->
                              task {
                                  let addDan: Tx<Customer, unit> = Tx.insert (New.customer "Dan" "dan@example.com")
                                  let! _ = repo.Transact addDan
                                  return! repo.Count(Query.from (tableOf<Customer>))
                              })

                      let! after = repo.Count(Query.from (tableOf<Customer>))
                      ok inside |> shouldBe __
                      ok after |> shouldBe __
                  }
          ) ]
