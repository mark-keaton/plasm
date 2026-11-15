namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: Association writes have explicit lifecycle semantics
[<Binding>]
type AssociationWriteSteps(w: World) =

    let orderWithLines (id: OrderId) =
        task {
            let! r =
                w.Repo.One(
                    plasm {
                        for o in Schemas.orders do
                            where (o.Id = id)
                            preload o.Lines
                    }
                )

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

    let lineCount () = w.Db.Scalar<int64>("select count(*) from order_lines")

    // Scenario: Omitting an association does not modify it

    [<StepDefinition("an Order with existing OrderLines")>]
    member _.WithLines() = seedOrderWithLines 2 |> unit

    [<StepDefinition("I update only the Order status")>]
    member _.UpdateStatus() =
        task {
            let! r = w.Repo.One(plasm { for o in Schemas.orders do where (o.Id = w.Get "id") })
            let order = (ok r).Value
            equal NotLoaded order.Lines "lines not loaded"
            let! u = w.Repo.Update(order |> Changeset.edit (fun o -> { o with Status = Published }))
            w.Set("updated", ok u)
        }
        |> unit

    [<StepDefinition("existing OrderLines are preserved")>]
    member _.LinesPreserved() =
        task {
            let! n = lineCount ()
            equal 2L n "stored lines"
        }
        |> unit

    [<StepDefinition("an unloaded collection is not interpreted as empty")>]
    member _.UnloadedNotEmpty() =
        equal NotLoaded (w.Get<Order> "updated").Lines "returned lines remain unloaded"
        check (w.Db.Recorder.Writes |> List.forall (fun e -> not (e.Sql.Contains "order_lines"))) "no line writes"

    // Scenario: Updating an association matches existing identities

    [<StepDefinition("an Order with an existing OrderLine")>]
    member _.WithLine() = seedOrderWithLines 1 |> unit

    [<StepDefinition("an association changeset modifies that OrderLine by its identity")>]
    member _.ModifyLine() =
        task {
            let! order = orderWithLines (w.Get "id")
            let line = Assoc.get order.Lines |> List.exactlyOne
            w.Set("lineId", line.Id)

            let cs =
                Changeset.update order
                |> Changeset.putChildren((fun o -> o.Lines), [ line |> Changeset.edit (fun l -> { l with Quantity = qty 7 }) ])

            let! r = w.Repo.Update cs
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

    // Scenario: Removing related records requires a configured policy

    [<StepDefinition("an association replacement omits an existing child")>]
    member _.ReplacementOmits() =
        task {
            let! id = seedOrderWithLines 2
            let! order = orderWithLines id
            let kept = Assoc.get order.Lines |> List.head

            w.Set(
                "changeset",
                Changeset.update order
                |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.allChildren, [ Changeset.update kept ])
            )
        }
        |> unit

    [<StepDefinition("no removal policy has been configured")>]
    member _.NoPolicy() =
        task {
            w.MarkCommands()
            let! r = w.Repo.Update(w.Get<Changeset<Order>> "changeset")
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the replacement is rejected")>]
    member _.Rejected() =
        match w.Get<Result<Order, WriteError<Order>>> "result" with
        | Error(WriteError.Invalid cs) ->
            let e = cs.Errors |> List.exactlyOne
            equal FieldErrorKind.ReplacementRejected e.Kind "error kind"
            equal "Lines" e.Field "field"
        | other -> failwithf "expected Invalid, got %A" other

    [<StepDefinition("the child is not silently deleted or detached")>]
    member _.NotDeleted() =
        task {
            let (OrderId id) = w.Get "id"
            let! n = w.Db.Scalar<int64>("select count(*) from order_lines where order_id = @id", [ "id", box id ])
            equal 2L n "lines still attached"
            // The same replacement succeeds once a policy is configured explicitly.
            let repo = w.Db.RepoWith(model = Schemas.modelDeletingLines)
            let! r = repo.Update(w.Get<Changeset<Order>> "changeset")
            ok r |> ignore
            let! after = w.Db.Scalar<int64>("select count(*) from order_lines where order_id = @id", [ "id", box id ])
            equal 1L after "with OnReplace.Delete the omitted line is deleted"
        }
        |> unit

    // Scenario: Parent and association writes are atomic

    [<StepDefinition("a valid Order change and an invalid OrderLine change")>]
    member _.ValidAndInvalid() =
        task {
            let! id = seedOrderWithLines 1
            let! order = orderWithLines id
            let line = Assoc.get order.Lines |> List.exactlyOne

            // Violates the database's check constraint.
            let invalidInDatabase = line |> Changeset.edit (fun l -> { l with UnitPrice = -1m })
            let parent = order |> Changeset.edit (fun o -> { o with Status = Published })
            w.Set("inDatabase", parent |> Changeset.putChildren((fun o -> o.Lines), [ invalidInDatabase ]))
        }
        |> unit

    [<StepDefinition("I submit them as one association write")>]
    member _.SubmitTogether() =
        task {
            let! b = w.Repo.Update(w.Get<Changeset<Order>> "inDatabase")

            // The database's check (order_lines_unit_price_check) comes back as
            // the child field's error.
            match b with
            | Error(WriteError.Invalid cs) ->
                equal [ "Lines[0].UnitPrice", FieldErrorKind.CheckConstraint "order_lines_unit_price_check" ] (cs.Errors |> List.map (fun e -> e.Field, e.Kind)) "child error from the database"
            | other -> failwithf "expected Invalid, got %A" other
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
