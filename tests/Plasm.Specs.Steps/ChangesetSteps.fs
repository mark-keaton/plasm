namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: Changes are explicit and the database's refusals are inspectable
[<Binding>]
type ChangesetSteps(w: World) =

    let readOrder (id: OrderId) =
        task {
            let! r = w.Repo.One(plasm { for o in Schemas.orders do where (o.Id = id) })
            return (ok r).Value
        }

    // Scenario: Creating a changeset preserves its input (Given an existing Order is shared)

    [<StepDefinition("I create a changeset with a different status")>]
    member _.CreateChangeset() =
        let o: Order = w.Get "order"
        w.Set("changeset", o |> Changeset.edit (fun o -> { o with Status = Published }))

    [<StepDefinition("the changeset exposes the proposed status")>]
    member _.ExposesStatus() =
        let cs: Changeset<Order> = w.Get "changeset"
        equal [ "Status", FieldChange.Assigned(box Published) ] cs.Changes "changes"
        equal (Some Published) (cs.Proposed |> Option.map (fun o -> o.Status)) "proposed record"

    [<StepDefinition("the original Order retains its original status")>]
    member _.OriginalRetains() =
        equal Draft (w.Get<Order> "order").Status "original status"

    // Scenario: Unspecified and explicitly cleared values differ

    [<StepDefinition("an Order has a CancellationReason")>]
    member _.HasReason() =
        task {
            let! id = Seed.order w.Db None "cancelled" 10m (Seed.at 1)
            let! _ = w.Db.Exec("update orders set cancellation_reason = 'damaged' where id = @id", [ "id", box (let (OrderId v) = id in v) ])
            w.Set("id", id)
        }
        |> unit

    [<StepDefinition("a changeset does not mention CancellationReason")>]
    member _.DoesNotMention() =
        task {
            let! o = readOrder (w.Get "id")
            let cs = o |> Changeset.edit (fun o -> { o with Total = 12m })
            equal [ "Total" ] (cs.Changes |> List.map fst) "changes"
            let! r = w.Repo.Update cs
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the stored value is preserved")>]
    member _.Preserved() =
        task {
            let! v = w.Db.Scalar<string>("select cancellation_reason from orders")
            equal "damaged" v "stored reason"
        }
        |> unit

    [<StepDefinition("a changeset explicitly clears CancellationReason")>]
    member _.Clears() =
        task {
            let! o = readOrder (w.Get "id")
            let cs = o |> Changeset.edit (fun o -> { o with CancellationReason = None })
            equal [ "CancellationReason", FieldChange.Cleared ] cs.Changes "changes"
            let! r = w.Repo.Update cs
            w.Set("updated", ok r)
        }
        |> unit

    [<StepDefinition("the stored value becomes absent")>]
    member _.Absent() =
        task {
            let! isNull = w.Db.Scalar<bool>("select cancellation_reason is null from orders")
            check isNull "stored reason is NULL"
            equal None (w.Get<Order> "updated").CancellationReason "returned value"
        }
        |> unit

    // Scenario: Invalid changes are not persisted

    [<StepDefinition("a change the database refuses")>]
    member _.RefusedChange() =
        // orders_total_check refuses a negative total.
        w.Set("refused", New.order None Draft -5m)

    [<StepDefinition("I submit it for persistence")>]
    member _.Submit() =
        task {
            w.MarkCommands()
            let! r = w.Repo.Transact(Tx.insert (w.Get<Order> "refused"): Tx<Order, unit>)
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the operation returns the field's errors")>]
    member _.ReturnsErrors() =
        match w.Get<Result<Order, TransactionError<unit>>> "result" with
        | Error e ->
            match e.Failure with
            | TxFailure.InvalidChanges(_, errors) ->
                equal [ "Total", FieldErrorKind.CheckConstraint "orders_total_check" ] (errors |> List.map (fun e -> e.Field, e.Kind)) "errors"
            | other -> failwithf "expected invalid changes, got %A" other
        | Ok o -> failwithf "the insert succeeded: %A" o

    [<StepDefinition("nothing is written")>]
    member _.NothingWritten() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from orders")
            equal 0L n "orders"
        }
        |> unit

    // Scenario: Database constraints produce structured errors

    [<StepDefinition("a changeset for a field the model states is unique")>]
    member _.UniqueConstraint() =
        let cs = Changeset.insert (New.customer "Ada" "ada@example.com")

        check cs.IsValid "valid before execution"
        w.Set("changeset", cs)

    [<StepDefinition("a concurrent operation causes that constraint to be violated")>]
    member _.ConcurrentViolation() =
        task {
            // Another writer takes the email after the changeset was validated.
            let! _ = Seed.customer w.Db "Someone else" "ada@example.com"
            let! r = w.Repo.Insert(w.Get<Changeset<Customer>> "changeset")
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the write returns the corresponding structured constraint error")>]
    member _.ConstraintError() =
        match w.Get<Result<Customer, WriteError<Customer>>> "result" with
        | Error(WriteError.Invalid cs) ->
            equal
                [ { Field = "Email"
                    Message = "has already been taken"
                    Kind = FieldErrorKind.UniqueConstraint "customers_email_key" } ]
                cs.Errors
                "errors"
        | other -> failwithf "expected Invalid, got %A" other

    [<StepDefinition("unrelated database failures are not misreported as validation errors")>]
    member _.UnrelatedFailures() =
        task {
            // A table-level rule whose name follows no column's convention belongs
            // to no field, so breaking it stays a database failure.
            let! _ = w.Db.Exec "alter table orders add constraint orders_not_thirteen check (total <> 13)"
            let! r = w.Repo.Insert(Changeset.insert (New.order None Draft 13m))

            match r with
            | Error(WriteError.Failed f) ->
                equal DbFailureKind.CheckViolation f.Kind "failure kind"
                equal (Some "orders_not_thirteen") f.Constraint "constraint"
            | other -> failwithf "expected Failed, got %A" other
        }
        |> unit

    // Scenario: Generated values are returned immutably

    [<StepDefinition("an insert whose identifier is generated by the database")>]
    member _.GeneratedInsert() =
        w.Set("submitted", New.product "SKU-1" "Widget")

    [<StepDefinition("the insert succeeds")>]
    member _.InsertSucceeds() =
        task {
            let! r = w.Repo.Insert(Changeset.insert (w.Get<Product> "submitted"))
            w.Set("inserted", ok r)
        }
        |> unit

    [<StepDefinition("the returned value contains the generated identifier")>]
    member _.HasGeneratedId() =
        task {
            let inserted: Product = w.Get "inserted"
            let! id = w.Db.Scalar<int64>("select id from products where sku = 'SKU-1'")
            equal (ProductId id) inserted.Id "generated identifier"
            equal "Widget" inserted.Name "other fields"
        }
        |> unit

    [<StepDefinition("the submitted value remains unchanged")>]
    member _.SubmittedUnchanged() =
        equal (ProductId 0L) (w.Get<Product> "submitted").Id "submitted identifier"
