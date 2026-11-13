namespace Plasm.Specs

open System.Collections.Generic
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

module OrderChanges =
    let parseStatus (s: string) =
        match s.ToLowerInvariant() with
        | "draft" -> Ok Draft
        | "published" -> Ok Published
        | "cancelled" -> Ok Cancelled
        | _ -> Error "is invalid"

    /// The only fields an external caller may change.
    let fromInput (input: IReadOnlyDictionary<string, string>) (order: Order) =
        Changeset.update order
        |> Changeset.castWith(input, (fun o -> o.Status), parseStatus)
        |> Changeset.cast(input, fun o -> o.CancellationReason)
        |> Changeset.validateOptionalLength((fun o -> o.CancellationReason), max = 200)

    /// Domain validation for new orders.
    let validateNew (cs: Changeset<Order>) =
        cs
        |> Changeset.validateRange((fun o -> o.Total), min = 0m)
        |> Changeset.validateOptionalLength((fun o -> o.CancellationReason), max = 10)

/// Rule: Changes are explicit and validation is inspectable
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
        equal (Some Published) (cs |> Changeset.tryGetChange (fun o -> o.Status)) "proposed status"
        equal [ "Status", Assigned(box Published) ] cs.Changes "changes"
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
            let cs = Changeset.update o |> Changeset.clear (fun o -> o.CancellationReason)
            equal [ "CancellationReason", Cleared ] cs.Changes "changes"
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

    // Scenario: Invalid changes can be inspected without execution

    [<StepDefinition("I construct a changeset violating a domain validation")>]
    member _.ConstructInvalid() =
        w.MarkCommands()

        let cs =
            Changeset.insert { New.order None Draft -5m with CancellationReason = Some "far too long a reason" }
            |> OrderChanges.validateNew

        w.Set("changeset", cs)

    [<StepDefinition("it contains structured validation errors")>]
    member _.StructuredErrors() =
        let cs: Changeset<Order> = w.Get "changeset"
        check (not cs.IsValid) "invalid"
        equal [ FieldErrorKind.Range; FieldErrorKind.Length ] (cs.Errors |> List.map (fun e -> e.Kind)) "error kinds"

    [<StepDefinition("errors identify the affected fields")>]
    member _.IdentifyFields() =
        let cs: Changeset<Order> = w.Get "changeset"
        equal [ "Total"; "CancellationReason" ] (cs.Errors |> List.map (fun e -> e.Field)) "fields"

    [<StepDefinition("inspecting it performs no database operations")>]
    member _.InspectNoDb() = equal 0 w.CommandsSinceMark.Length "commands"

    // Scenario: Invalid changes are not persisted

    [<StepDefinition("an invalid changeset")>]
    member _.InvalidChangeset() =
        w.Set("changeset", Changeset.insert (New.order None Draft -5m) |> OrderChanges.validateNew)

    [<StepDefinition("I submit it for persistence")>]
    member _.Submit() =
        task {
            w.MarkCommands()
            let! r = w.Repo.Insert(w.Get<Changeset<Order>> "changeset")
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the operation returns validation errors")>]
    member _.ReturnsErrors() =
        match w.Get<Result<Order, WriteError<Order>>> "result" with
        | Error(WriteError.Invalid cs) -> equal [ "Total" ] (cs.Errors |> List.map (fun e -> e.Field)) "error fields"
        | other -> failwithf "expected Invalid, got %A" other

    [<StepDefinition("no write command is executed")>]
    member _.NoWrite() =
        task {
            equal 0 w.CommandsSinceMark.Length "commands"
            let! n = w.Db.Scalar<int64>("select count(*) from orders")
            equal 0L n "orders"
        }
        |> unit

    // Scenario: External input cannot change protected fields

    [<StepDefinition("only specified fields are accepted from external input")>]
    member _.OnlySpecified() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! o = readOrder id
            w.Set("order", o)
        }
        |> unit

    [<StepDefinition("external input includes an Order identifier or concurrency version")>]
    member _.HostileInput() =
        let input =
            dict
                [ "id", "999"
                  "version", "42"
                  "total", "0.01"
                  "status", "published" ]
            |> fun d -> Dictionary(d) :> IReadOnlyDictionary<string, string>

        let o: Order = w.Get "order"
        w.Set("changeset", OrderChanges.fromInput input o)

    [<StepDefinition("those fields cannot be changed through that input path")>]
    member _.Protected() =
        task {
            let cs: Changeset<Order> = w.Get "changeset"
            equal [ "Status" ] (cs.Changes |> List.map fst) "accepted changes"
            let! r = w.Repo.Update cs
            let updated = ok r
            let original: Order = w.Get "order"
            equal original.Id updated.Id "identifier"
            equal (original.Version + 1) updated.Version "version advanced only by optimistic concurrency"
            equal original.Total updated.Total "unaccepted field"
            let! stored = w.Db.Scalar<int>("select version from orders")
            equal 2 stored "stored version"
        }
        |> unit

    // Scenario: Database constraints produce structured errors

    [<StepDefinition("a named uniqueness constraint is associated with a changeset")>]
    member _.UniqueConstraint() =
        let cs =
            Changeset.insert (New.customer "Ada" "ada@example.com")
            |> Changeset.uniqueConstraint((fun c -> c.Email), "customers_email_key")

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
            // orders_total_check is not declared on this changeset.
            let! r = w.Repo.Insert(Changeset.insert (New.order None Draft -1m))

            match r with
            | Error(WriteError.Failed f) ->
                equal DbFailureKind.CheckViolation f.Kind "failure kind"
                equal (Some "orders_total_check") f.Constraint "constraint"
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
