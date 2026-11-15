namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain

/// Feature: Database constraints are stated once, in the model
[<Binding>]
type ConstraintSteps(w: World) =

    let insertResult (r: Result<'T, TransactionError<unit>>) =
        match r with
        | Ok _ -> failwith "the insert succeeded"
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> w.Set("constraintFailure", (Choice1Of2 errors: Choice<FieldError list, DbFailure>))
        | Error { Failure = TxFailure.Failed(DbError.Database f) } -> w.Set("constraintFailure", (Choice2Of2 f: Choice<FieldError list, DbFailure>))
        | Error other -> failwithf "unexpected %A" other

    let failure () = w.Get<Choice<FieldError list, DbFailure>> "constraintFailure"

    [<StepDefinition("^a Customer with the email \"(.*)\"$")>]
    member _.Customer(email: string) =
        task {
            let! r = w.Repo.Transact(Tx.insert (New.customer "Ada" email): Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("^I insert another Customer with the email \"(.*)\"$")>]
    member _.InsertDuplicate(email: string) =
        task {
            let! r = w.Repo.Transact(Tx.insert (New.customer "Another" email): Tx<_, unit>)
            insertResult r
        }
        |> unit

    [<StepDefinition("^I insert an Order with a line for the Product with id (\\d+)$")>]
    member _.InsertWithMissingProduct(id: int64) =
        task {
            let order = { New.order None Draft 5m with Lines = [ New.line (ProductId id) 1 1m ] }
            let! r = w.Repo.Transact(Tx.insert order: Tx<_, unit>)
            insertResult r
        }
        |> unit

    [<StepDefinition("^I insert an Order for the Customer with id (\\d+)$")>]
    member _.InsertForMissing(id: int64) =
        task {
            let! r = w.Repo.Transact(Tx.insert (New.order (Some(CustomerId id)) Draft 5m): Tx<_, unit>)
            insertResult r
        }
        |> unit

    [<StepDefinition("^I insert an Order with the total (-?\\d+)$")>]
    member _.InsertTotal(total: decimal) =
        task {
            let! r = w.Repo.Transact(Tx.insert (New.order None Draft total): Tx<_, unit>)
            insertResult r
        }
        |> unit

    [<StepDefinition("^the insert fails with a (uniqueness|reference|check) error on ([\\w\\[\\]\\.]+), naming (\\w+)$")>]
    member _.FieldError(kind: string, field: string, name: string) =
        match failure () with
        | Choice1Of2 errors ->
            let expected =
                match kind with
                | "uniqueness" -> FieldErrorKind.UniqueConstraint name
                | "reference" -> FieldErrorKind.ForeignKeyConstraint name
                | _ -> FieldErrorKind.CheckConstraint name

            check (errors |> List.exists (fun e -> e.Field = field && e.Kind = expected)) $"the errors are %A{errors}"
        | Choice2Of2 f -> failwithf "expected a field error, got the database failure %A" f

    [<StepDefinition("^a table constraint named \"(.*)\" that refuses a total of (\\d+)$")>]
    member _.TableConstraint(name: string, total: int) =
        w.Db.Exec $"alter table orders add constraint {name} check (total <> {total})" |> unit

    [<StepDefinition("^the insert fails as a database failure naming \"(.*)\"$")>]
    member _.DatabaseFailure(name: string) =
        match failure () with
        | Choice2Of2 f -> equal (Some name) f.Constraint "constraint"
        | Choice1Of2 errors -> failwithf "expected a database failure, got the field errors %A" errors

    [<StepDefinition("^no Order was written$")>]
    member _.NoOrder() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from orders")
            equal 0L n "orders"
        }
        |> unit

    [<StepDefinition("^I list the references the model infers$")>]
    member _.References() = w.Set("references", w.Db.Model.References)

    [<StepDefinition("^(\\w+)\\.(\\w+) references (\\w+)$")>]
    member _.Reference(model: string, field: string, target: string) =
        let refs = w.Get<ModelReference list> "references"
        check (List.contains { Model = model; Field = field; Target = target } refs) $"the references are %A{refs}"

    [<StepDefinition("^no record's own key references itself$")>]
    member _.NoSelfKey() =
        let refs = w.Get<ModelReference list> "references"
        check (refs |> List.forall (fun r -> r.Field <> "Id")) $"the references are %A{refs}"

    [<StepDefinition("^the customers table has lost its unique constraint on email$")>]
    member _.DropUnique() =
        w.Db.Exec "alter table customers drop constraint customers_email_key" |> unit

    [<StepDefinition("^the orders table has lost its foreign key on customer_id$")>]
    member _.DropReference() =
        w.Db.Exec "alter table orders drop constraint orders_customer_id_fkey" |> unit

    [<StepDefinition("^the missing uniqueness of (\\w+)\\.(\\w+) is reported$")>]
    member _.MissingUnique(model: string, field: string) =
        let issues = w.Get<SchemaIssue list> "issues"
        check (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingUnique && i.Model = model && i.Field = Some field)) $"the issues are %A{issues}"

    [<StepDefinition("^the missing reference from (\\w+)\\.(\\w+) to (\\w+) is reported$")>]
    member _.MissingReference(model: string, field: string, table: string) =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingReference && i.Model = model && i.Field = Some field && i.Message.Contains table))
            $"the issues are %A{issues}"

    [<StepDefinition("^the unique constraint on customers.email is renamed to (\\w+)$")>]
    member _.RenameUnique(name: string) =
        w.Db.Exec $"alter table customers rename constraint customers_email_key to {name}" |> unit

    [<StepDefinition("^the unique constraint on customers.email is replaced by the unique index (\\w+)$")>]
    member _.UniqueIndex(name: string) =
        w.Db.Exec $"alter table customers drop constraint customers_email_key; create unique index {name} on customers (email)" |> unit

    [<StepDefinition("^the unique constraint customers_email_key is added on customers.email$")>]
    member _.AddUnique() =
        w.Db.Exec "alter table customers add constraint customers_email_key unique (email)" |> unit

    [<StepDefinition("^the foreign key on orders.customer_id is renamed to (\\w+)$")>]
    member _.RenameReference(name: string) =
        w.Db.Exec $"alter table orders rename constraint orders_customer_id_fkey to {name}" |> unit

    [<StepDefinition("^(\\w+)\\.(\\w+) is reported as misnamed, saying to rename (\\w+) to (\\w+)$")>]
    member _.Misnamed(model: string, field: string, actual: string, expected: string) =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MisnamedConstraint
                 && i.Model = model
                 && i.Field = Some field
                 && i.Message.Contains $"rename {actual} to {expected}"))
            $"the issues are %A{issues}"

    [<StepDefinition("^no issue is reported for (\\w+)\\.(\\w+)$")>]
    member _.NoIssueFor(model: string, field: string) =
        let issues = w.Get<SchemaIssue list> "issues"
        check (issues |> List.forall (fun i -> not (i.Model = model && i.Field = Some field))) $"the issues are %A{issues}"

