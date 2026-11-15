/// Lesson 12: About writing an aggregate.
module Plasm.Koans.Lessons.L12_WritingAnAggregate

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private order (repo: Repo) (id: OrderId) =
    task {
        let! o = repo.One(plasm { for o in Db.orders do where (o.Id = id) })
        return (ok o).Value
    }

/// How many lines an order has in the database.
let private linesOf (repo: Repo) (id: OrderId) =
    task {
        let! n = repo.Count(plasm { for l in Db.orderLines do where (l.OrderId = id) })
        return ok n
    }

/// A new draft Order for Cy with one line: two Widgets at 10.00 each. Its
/// lines are part of the record, so it is written whole.
let private cysOrder =
    { New.order (Some(CustomerId 3L)) Draft 20m with
        Lines = [ New.line (ProductId 1L) 2 10m ] }

let lesson =
    Lesson.make
        "About writing an aggregate"
        [ Koan.Task(
              "Inserting an order inserts its lines",
              fun repo ->
                  task {
                      // An order owns its lines, so one Tx.insert writes the order and
                      // then its lines, in one transaction.
                      let! _ = repo.Transact(Tx.insert cysOrder: Tx<Order, unit>)
                      let! orders = repo.Count(Query.from Db.orders)
                      ok orders |> shouldBe __
                      let! lines = repo.Count(Query.from Db.orderLines)
                      ok lines |> shouldBe __
                  }
          )

          Koan.Task(
              "The stored order holds its stored lines",
              fun repo ->
                  task {
                      let! saved = repo.Transact(Tx.insert cysOrder: Tx<Order, unit>)
                      let saved = ok saved
                      // The returned Order holds its lines as stored: each was given the
                      // new order's key, whatever OrderId it was built with.
                      saved.Lines.Length |> shouldBe __
                      ((List.head saved.Lines).OrderId = saved.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Saved lines are read back",
              fun repo ->
                  task {
                      let! saved = repo.Transact(Tx.insert cysOrder: Tx<Order, unit>)
                      let! again = order repo (ok saved).Id
                      let line = List.head again.Lines
                      line.Quantity.Value |> shouldBe __
                      line.UnitPrice |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding a line to an order read",
              fun repo ->
                  task {
                      // Order 2 has one line. Change the list: the line without a key
                      // is new, so the update inserts it.
                      let! o = order repo (OrderId 2L)
                      let added = { o with Lines = o.Lines @ [ New.line (ProductId 1L) 1 10m ] }
                      let! _ = repo.Transact(Tx.update (o, added): Tx<Order, unit>)
                      let! n = linesOf repo o.Id
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Lines that did not change are not written",
              fun repo ->
                  task {
                      // An update writes the difference: changing only the total sends
                      // no command to order_lines but the one that reads them back.
                      let! o = order repo (OrderId 1L)
                      let! _ = repo.Transact(Tx.update (o, { o with Total = 40m }): Tx<Order, unit>)
                      let lineWrites = commands () |> List.filter (fun c -> c.Contains "order_lines" && not (c.StartsWith "SELECT"))
                      lineWrites.Length |> shouldBe __
                      let! n = linesOf repo o.Id
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Lines are matched by key",
              fun repo ->
                  task {
                      // Order 1's lines have keys 1 and 2. A line with a key that is
                      // still in the list is updated in place, and keeps its key; a
                      // line without one is inserted.
                      let! o = order repo (OrderId 1L)
                      let first = List.head o.Lines
                      let changed = { o with Lines = [ { first with UnitPrice = 9m }; New.line (ProductId 3L) 1 2.5m ] }
                      let! stored = repo.Transact(Tx.update (o, changed): Tx<Order, unit>)
                      let lines = (ok stored).Lines
                      lines.Head.Id |> shouldBe __
                      lines.Head.UnitPrice |> shouldBe __
                  }
          )

          Koan.Task(
              "A line left out of the list is deleted",
              fun repo ->
                  task {
                      // The list is the whole truth about an order's lines: a line that
                      // was read and is no longer in the list is deleted. No policy is
                      // needed, because the list is never a part of the lines.
                      let! o = order repo (OrderId 1L)
                      let! _ = repo.Transact(Tx.update (o, { o with Lines = [ List.head o.Lines ] }): Tx<Order, unit>)
                      let! n = linesOf repo o.Id
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Changing some lines is changing the list",
              fun repo ->
                  task {
                      // To remove only the Gadget lines (product 2), filter the list in
                      // F# and write the order: the lines kept are left as they are.
                      let! o = order repo (OrderId 1L)
                      let noGadgets = { o with Lines = o.Lines |> List.filter (fun l -> l.ProductId <> ProductId 2L) }
                      let! _ = repo.Transact(Tx.update (o, noGadgets): Tx<Order, unit>)
                      let! left = repo.All(plasm { for l in Db.orderLines do where (l.OrderId = OrderId 1L) })
                      ok left |> List.map (fun l -> l.ProductId) |> shouldBe __
                  }
          )

          Koan.Task(
              "A line's error is the order's field error",
              fun repo ->
                  task {
                      // The second line's negative price breaks order_lines_unit_price_check.
                      // The error names the line by its place in the list, and nothing is
                      // written: neither the order nor its first line.
                      let lines = [ New.line (ProductId 1L) 1 10m; New.line (ProductId 2L) 1 -1m ]
                      let! r = repo.Transact(Tx.insert { New.order None Draft 9m with Lines = lines }: Tx<Order, unit>)

                      match (err r).Failure with
                      | TxFailure.InvalidChanges(_, errors) -> errors |> List.map (fun e -> e.Field) |> shouldBe __
                      | other -> failwithf "expected invalid changes, got %A" other

                      let! orders = repo.Count(Query.from Db.orders)
                      ok orders |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting an order deletes its lines",
              fun repo ->
                  task {
                      // Tx.delete deletes the order read, and the lines it owns.
                      let! saved = repo.Transact(Tx.insert cysOrder: Tx<Order, unit>)
                      let! _ = repo.Transact(Tx.delete (ok saved): Tx<Order, unit>)
                      let! n = linesOf repo (ok saved).Id
                      n |> shouldBe __
                  }
          ) ]
