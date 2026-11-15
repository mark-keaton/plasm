namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: Writing an aggregate writes what it owns
[<Binding>]
type AssociationWriteSteps(w: World) =

    let readOrder (id: OrderId) =
        task {
            let! r = w.Repo.One(plasm { for o in tableOf<Order> do where (o.Id = id) })
            return (ok r).Value
        }

    let seedOrderWithLines n =
        task {
            let! p = Seed.product w.Db "SKU"
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)

            for i in 1..n do
                let! _ = Seed.line w.Db id p i 2m
                ()

            w.Set("id", id)
            w.Set("product", p)
            return id
        }

    /// Writes `changed`, made from `original` (the Order read).
    let update (original: Order) (changed: Order) =
        task {
            let! r = w.Repo.Transact(Tx.update (original, changed): Tx<Order, unit>)
            return r
        }

    let lineCount () = w.Db.Scalar<int64>("select count(*) from order_lines")

    // Scenario: Omitting an association does not modify it

    [<StepDefinition("an Order with existing OrderLines")>]
    member _.WithLines() = seedOrderWithLines 2 |> unit

    [<StepDefinition("I update only the Order status")>]
    member _.UpdateStatus() =
        task {
            let! order = readOrder (w.Get "id")
            equal 2 order.Lines.Length "lines read with the Order"
            w.MarkCommands()
            let! u = update order { order with Status = Published }
            w.Set("updated", ok u)
        }
        |> unit

    [<StepDefinition("existing OrderLines are preserved")>]
    member _.LinesPreserved() =
        task {
            let! n = lineCount ()
            equal 2L n "stored lines"
            equal 2 (w.Get<Order> "updated").Lines.Length "returned lines"
        }
        |> unit

    [<StepDefinition("no command is sent for its OrderLines")>]
    member _.NoLineCommand() =
        check (w.CommandsSinceMark |> List.forall (fun e -> not (e.Sql.Contains "order_lines"))) "no command touches order_lines"

    // Scenario: Updating an association matches existing identities

    [<StepDefinition("an Order with an existing OrderLine")>]
    member _.WithLine() = seedOrderWithLines 1 |> unit

    [<StepDefinition("I change that OrderLine in the Order I read")>]
    member _.ModifyLine() =
        task {
            let! order = readOrder (w.Get "id")
            let line = List.exactlyOne order.Lines
            w.Set("lineId", line.Id)
            let! r = update order { order with Lines = [ { line with Quantity = qty 7 } ] }
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the existing row is updated")>]
    member _.RowUpdated() =
        task {
            let (OrderLineId lineId) = w.Get "lineId"
            let! q = w.Db.Scalar<int>("select quantity from order_lines where id = @id", [ "id", box lineId ])
            equal 7 q "stored quantity"
        }
        |> unit

    [<StepDefinition("a duplicate row is not inserted")>]
    member _.NoDuplicate() =
        task {
            let! n = lineCount ()
            equal 1L n "stored lines"
        }
        |> unit

    // Scenario: Removing an owned record deletes it

    [<StepDefinition("an Order with two OrderLines")>]
    member _.WithTwoLines() = seedOrderWithLines 2 |> unit

    [<StepDefinition("I remove one of them from the Order I read")>]
    member _.RemoveOne() =
        task {
            let! order = readOrder (w.Get "id")
            let kept, removed = order.Lines.[0], order.Lines.[1]
            w.Set("kept", kept.Id)
            w.Set("removed", removed.Id)
            let! r = update order { order with Lines = [ kept ] }
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("that OrderLine is deleted")>]
    member _.Deleted() =
        task {
            let (OrderLineId removed) = w.Get "removed"
            let! n = w.Db.Scalar<int64>("select count(*) from order_lines where id = @id", [ "id", box removed ])
            equal 0L n "the removed line"
        }
        |> unit

    [<StepDefinition("the other is kept")>]
    member _.Kept() =
        task {
            let (OrderLineId kept) = w.Get "kept"
            let! n = w.Db.Scalar<int64>("select count(*) from order_lines where id = @id", [ "id", box kept ])
            equal 1L n "the kept line"
        }
        |> unit

    // Scenario: Parent and association writes are atomic

    [<StepDefinition("a valid Order change and an invalid OrderLine change")>]
    member _.ValidAndInvalid() =
        task {
            let! id = seedOrderWithLines 1
            let! order = readOrder id
            let line = List.exactlyOne order.Lines
            // The line violates the database's check (order_lines_unit_price_check).
            w.Set("original", order)
            w.Set("changed", { order with Status = Published; Lines = [ { line with UnitPrice = -1m } ] })
        }
        |> unit

    [<StepDefinition("I submit them as one association write")>]
    member _.SubmitTogether() =
        task {
            let! r = update (w.Get "original") (w.Get "changed")

            // The database's check comes back as the child field's error.
            match r with
            | Error e ->
                match e.Failure with
                | TxFailure.InvalidChanges(_, errors) ->
                    equal [ "Lines[0].UnitPrice", FieldErrorKind.CheckConstraint "order_lines_unit_price_check" ] (errors |> List.map (fun e -> e.Field, e.Kind)) "child error from the database"
                | other -> failwithf "expected invalid changes, got %A" other
            | Ok o -> failwithf "the write succeeded: %A" o
        }
        |> unit

    [<StepDefinition("neither change is committed")>]
    member _.NeitherCommitted() =
        task {
            let! status = w.Db.Scalar<string>("select status from orders")
            let! version = w.Db.Scalar<int>("select version from orders")
            let! price = w.Db.Scalar<decimal>("select unit_price from order_lines")
            equal "draft" status "order status"
            equal 1 version "order version"
            equal 2m price "line price"
        }
        |> unit
