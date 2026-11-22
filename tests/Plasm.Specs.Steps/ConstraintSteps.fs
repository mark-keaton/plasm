namespace Plasm.Specs

open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Database
open Plasm.TestDomain.Domain

/// A second record keyed by CustomerId, for the shared-key scenario.
module SharedKey =
    [<Table "suppliers">]
    type Supplier = { Id: CustomerId; Name: string }

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

    /// SQLite drops and renames no unique constraint or foreign key: the
    /// table is made again from its own text, `edit`ed, its rows and indexes
    /// kept, with foreign keys off while it is.
    let rebuild (table: string) (edit: string -> string) =
        task {
            let! text = w.Db.Scalar<string>("select sql from sqlite_schema where type = 'table' and name = @t", [ "t", box table ])
            let! indexes =
                w.Db.Scalar<string>(
                    "select coalesce(group_concat(sql || '; ', ''), '') from sqlite_schema where type = 'index' and tbl_name = @t and sql is not null",
                    [ "t", box table ]
                )
            let edited = edit text
            check (edited <> text) $"the edit changed nothing in {text}"
            let made = Regex.Replace(edited, $"^create table {table}\\b", $"create table {table}_rebuilt", RegexOptions.IgnoreCase)

            let! _ =
                w.Db.Exec(
                    $"PRAGMA foreign_keys = OFF; {made}; insert into {table}_rebuilt select * from {table}; drop table {table}; "
                    + $"alter table {table}_rebuilt rename to {table}; "
                    + indexes
                    + "PRAGMA foreign_keys = ON;"
                )

            ()
        }

    /// The table's text without its constraint `name` (on its own line).
    let without (name: string) (text: string) =
        Regex.Replace(text, $",\\s*constraint {name}\\b[^\\n]*", "")

    let renamed (name: string) (other: string) (text: string) =
        Regex.Replace(text, $"\\bconstraint {name}\\b", $"constraint {other}")

    /// `postgres` on PostgreSQL; on SQLite, `table` rebuilt as `edit` makes it.
    let altered (postgres: string) (table: string) (edit: string -> string) =
        match w.Db.Kind with
        | PostgreSQL -> task { let! _ = w.Db.Exec postgres in () }
        | SQLite -> rebuild table edit

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

    /// A line breaking order_lines' other foreign key (its order), of a
    /// product that exists: first in the table, before the line a scenario
    /// then writes.
    [<StepDefinition("^an order line of the Order with id (\\d+), written while the database did not check its foreign keys$")>]
    member _.HeldViolation(order: int64) =
        let line = $"insert into products (sku, name) values ('held', 'Held'); insert into order_lines (order_id, product_id, quantity, unit_price) select {order}, id, 1, 1 from products where sku = 'held'"

        match w.Db.Kind with
        | PostgreSQL -> w.Db.Exec($"set session_replication_role = replica; {line}") |> unit
        | SQLite -> w.Db.Exec($"PRAGMA foreign_keys = OFF; {line}; PRAGMA foreign_keys = ON") |> unit

    [<StepDefinition("^a trigger on customers$")>]
    member _.Trigger() =
        w.Db.Exec "create trigger customers_seen after insert on customers begin select 1; end" |> unit

    [<StepDefinition("^the insert fails as a foreign key failure naming no constraint$")>]
    member _.UnnamedForeignKey() =
        match failure () with
        | Choice2Of2 f ->
            equal DbFailureKind.ForeignKeyViolation f.Kind "kind"
            equal None f.Constraint "constraint"
            equal None f.Table "table"
            equal ([]: string list) f.Columns "columns"
        | Choice1Of2 errors -> failwithf "expected a database failure, got the field errors %A" errors

    [<StepDefinition("^a Customer with the email \"(.*)\" and an Order of theirs$")>]
    member _.CustomerWithOrder(email: string) =
        task {
            let! r = w.Repo.Transact(Tx.insert (New.customer "Ada" email): Tx<_, unit>)
            let c = ok r
            let! o = w.Repo.Transact(Tx.insert (New.order (Some c.Id) Draft 5m): Tx<_, unit>)
            ok o |> ignore
            w.Set("referred", c)
        }
        |> unit

    [<StepDefinition("^I delete that Customer$")>]
    member _.DeleteReferred() =
        task {
            let! r = w.Repo.Transact(Tx.delete (w.Get<Customer> "referred"): Tx<_, unit>)
            insertResult r
        }
        |> unit

    /// By its name on PostgreSQL; by its table and columns on SQLite, which
    /// names no foreign key (ForeignKeys).
    [<StepDefinition("^the delete fails as a database failure naming the reference from (\\w+)\\.(\\w+)$")>]
    member _.DeleteRefused(table: string, column: string) =
        match failure () with
        | Choice2Of2 f ->
            equal DbFailureKind.ForeignKeyViolation f.Kind "kind"

            match w.Db.Kind with
            | PostgreSQL -> equal (Some $"{table}_{column}_fkey") f.Constraint "constraint"
            | SQLite ->
                equal (Some table) f.Table "table"
                equal [ column ] f.Columns "columns"
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

    [<StepDefinition("^I build a model in which Customers and Suppliers both have a CustomerId key$")>]
    member _.SharedKey() =
        w.Set("sharedKeyModel", Model.build [] [ typeof<Customer>; typeof<SharedKey.Supplier> ])

    [<StepDefinition("^the model is refused, naming CustomerId and both schemas$")>]
    member _.SharedKeyRefused() =
        match w.Get<Result<Model, ConfigError list>> "sharedKeyModel" with
        | Error errors ->
            let text = errors |> List.map string |> String.concat "\n"
            check (text.Contains "CustomerId" && text.Contains "Customer" && text.Contains "Supplier") text
        | Ok _ -> failwith "the model was built; two schemas share the key type CustomerId"

    [<StepDefinition("^the customers table has lost its unique constraint on email$")>]
    member _.DropUnique() =
        altered "alter table customers drop constraint customers_email_key" "customers" (without "customers_email_key") |> unit

    [<StepDefinition("^the orders table has lost its foreign key on customer_id$")>]
    member _.DropReference() =
        altered "alter table orders drop constraint orders_customer_id_fkey" "orders" (without "orders_customer_id_fkey") |> unit

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
        altered $"alter table customers rename constraint customers_email_key to {name}" "customers" (renamed "customers_email_key" name) |> unit

    [<StepDefinition("^the unique constraint on customers.email is replaced by the unique index (\\w+)$")>]
    member _.UniqueIndex(name: string) =
        task {
            do! altered "alter table customers drop constraint customers_email_key" "customers" (without "customers_email_key")
            let! _ = w.Db.Exec $"create unique index {name} on customers (email)"
            ()
        }
        |> unit

    [<StepDefinition("^the unique constraint customers_email_key is added on customers.email$")>]
    member _.AddUnique() =
        w.Db.Exec "alter table customers add constraint customers_email_key unique (email)" |> unit

    [<StepDefinition("^the foreign key on orders.customer_id is renamed to (\\w+)$")>]
    member _.RenameReference(name: string) =
        altered $"alter table orders rename constraint orders_customer_id_fkey to {name}" "orders" (renamed "orders_customer_id_fkey" name) |> unit

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

