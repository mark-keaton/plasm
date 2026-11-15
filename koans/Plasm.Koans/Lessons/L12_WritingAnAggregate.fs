/// Lesson 12: About child writes.
module Plasm.Koans.Lessons.L12_ChildWrites

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private withLines (repo: Repo) (id: int64) =
    task {
        let! o = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId id); preload o.Lines })
        return (ok o).Value
    }

let private linesOf (repo: Repo) (id: int64) =
    task {
        let! n = repo.Count(plasm { for l in Db.orderLines do where (l.OrderId = OrderId id) })
        return ok n
    }

/// A new draft Order for Cy with one line: two Widgets at 10.00 each.
let private cysOrder () =
    let line = Changeset.insert (New.line (ProductId 1L) 2 10m)
    // putChildren adds child changesets (here, Lines) to the parent's changeset.
    Changeset.insert (New.order (Some(CustomerId 3L)) Draft 20m) |> Changeset.putChildren ((fun o -> o.Lines), [ line ])

let lesson =
    Lesson.make
        "About child writes"
        [ Koan.Task(
              "A parent can be saved with a child",
              fun repo ->
                  task {
                      // One repo.Insert writes the Order and then its line.
                      let! _ = repo.Insert(cysOrder ())
                      let! orders = repo.Count(Query.from Db.orders)
                      ok orders |> shouldBe __
                      let! lines = repo.Count(Query.from Db.orderLines)
                      ok lines |> shouldBe __
                  }
          )

          Koan.Task(
              "The saved parent holds its children",
              fun repo ->
                  task {
                      let! saved = repo.Insert(cysOrder ())
                      let order = ok saved
                      // The returned Order's Lines are loaded with the lines just written.
                      let lines = order.Lines |> Assoc.get
                      lines.Length |> shouldBe __
                      // Each line was given the new Order's key.
                      ((List.head lines).OrderId = order.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Saved children can be read back",
              fun repo ->
                  task {
                      let! saved = repo.Insert(cysOrder ())
                      let id = (ok saved).Id
                      let! o = repo.One(plasm { for o in Db.orders do where (o.Id = id); preload o.Lines })
                      let line = (ok o).Value.Lines |> Assoc.get |> List.head
                      line.Quantity.Value |> shouldBe __
                      line.UnitPrice |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding a child to an existing parent",
              fun repo ->
                  task {
                      // Order 2 has one line. A child changeset with no key is a new line.
                      let! o = withLines repo 2L
                      let added = Changeset.insert (New.line (ProductId 1L) 1 10m)
                      let! _ = repo.Update(Changeset.update o |> Changeset.putChildren ((fun o -> o.Lines), [ added ]))
                      let! n = linesOf repo 2L
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Omitted children are untouched",
              fun repo ->
                  task {
                      let! o = withLines repo 1L
                      let! _ = repo.Update(o |> Changeset.edit (fun o -> { o with Total = 40m }))
                      let! n = linesOf repo 1L
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Children are matched by identity",
              fun repo ->
                  task {
                      let! o = withLines repo 1L
                      let first = o.Lines |> Assoc.get |> List.head
                      let repriced = first |> Changeset.edit (fun l -> { l with UnitPrice = 9m })
                      let added = Changeset.insert (New.line (ProductId 3L) 1 2.5m)
                      let! _ = repo.Update(Changeset.update o |> Changeset.putChildren ((fun o -> o.Lines), [ repriced; added ]))
                      let! n = linesOf repo 1L
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Removing children needs a policy",
              fun repo ->
                  task {
                      let! o = withLines repo 1L
                      let keepFirst = [ Changeset.update (o.Lines |> Assoc.get |> List.head) ]
                      let replace = Changeset.update o |> Changeset.replaceChildren ((fun o -> o.Lines), ReplaceScope.allChildren, keepFirst)

                      // Db.orders declares no OnReplace policy for Lines: removing one is refused.
                      let! refused = repo.Update replace
                      Result.isError refused |> shouldBe __

                      // Db.ordersDeletingLines declares OnReplace.Delete.
                      let! allowed = Repo(Db.modelDeletingLines, repo.Provider).Update replace
                      ok allowed |> ignore
                      let! n = linesOf repo 1L
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Parent and children succeed together",
              fun repo ->
                  task {
                      // The second line's negative price breaks order_lines_unit_price_check.
                      let lines = [ Changeset.insert (New.line (ProductId 1L) 1 10m); Changeset.insert (New.line (ProductId 2L) 1 -1m) ]
                      let! r = repo.Insert(Changeset.insert (New.order None Draft 9m) |> Changeset.putChildren ((fun o -> o.Lines), lines))
                      Result.isError r |> shouldBe __
                      let! orders = repo.Count(Query.from Db.orders)
                      ok orders |> shouldBe __
                  }
          )

          Koan.Task(
              "A filtered list is not the whole list",
              fun repo ->
                  task {
                      let! o = withLines repo 1L
                      let gadgets = plasm { for l in Db.orderLines do where (l.ProductId = ProductId 2L) }
                      // Replace only the Gadget lines (with none); the Widget line is outside the scope.
                      let replace = Changeset.update o |> Changeset.replaceChildren ((fun o -> o.Lines), ReplaceScope.matching gadgets, [])
                      let! _ = Repo(Db.modelDeletingLines, repo.Provider).Update replace
                      let! left = repo.All(plasm { for l in Db.orderLines do where (l.OrderId = OrderId 1L) })
                      ok left |> List.map (fun l -> l.ProductId) |> shouldBe __
                  }
          )

          Koan.Task(
              "The same child twice is refused",
              fun repo ->
                  task {
                      let! o = withLines repo 1L
                      let first = o.Lines |> Assoc.get |> List.head
                      let twice = [ Changeset.update first; Changeset.update first ]
                      let! r = repo.Update(Changeset.update o |> Changeset.putChildren ((fun o -> o.Lines), twice))

                      let duplicate =
                          match r with
                          | Error(WriteError.Invalid cs) -> cs.Errors |> List.exists (fun e -> e.Kind = FieldErrorKind.DuplicateIdentity)
                          | _ -> false

                      duplicate |> shouldBe __
                  }
          ) ]
