namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Records mapped deliberately wrongly onto the test database.
module Drift =
    type OrderWithNotes =
        { Id: OrderId
          Status: OrderStatus
          Total: decimal
          PlacedAt: DateTime
          Notes: string }

    type Invoice = { Id: int64; Number: string }

    type OrderTotalAsText =
        { Id: OrderId
          Status: OrderStatus
          Total: string
          PlacedAt: DateTime }

    type OrderRequiredReason =
        { Id: OrderId
          Status: OrderStatus
          Total: decimal
          PlacedAt: DateTime
          CancellationReason: string }

    type ProductWithoutName = { Id: ProductId; Sku: string }

    type ArticleWrongJoin =
        { Id: ArticleId
          Title: string
          Tags: Assoc<Tag list> }

    let model (schemas: ISchema list) = Model.buildOrFail Schemas.scalars schemas

/// Feature: Checking the model against the database schema
[<Binding>]
type SchemaCheckSteps(w: World) =

    let issues () = w.Get<SchemaIssue list> "issues"

    let find kind (model: string) =
        match issues () |> List.tryFind (fun i -> i.Kind = kind && i.Model = model) with
        | Some i -> i
        | None -> failwithf "expected %A for %s, got %A" kind model (issues () |> List.map string)

    let useModel (schemas: ISchema list) = w.Set("checkModel", Drift.model schemas)

    [<StepDefinition("I check the model against the database")>]
    member _.Check() =
        task {
            let repo =
                if w.Has "checkModel" then w.Db.RepoWith(model = w.Get "checkModel") else w.Repo

            w.MarkCommands()
            let! r = repo.CheckSchema()
            w.Set("issues", ok r)
        }
        |> unit

    [<StepDefinition("no problems are reported")>]
    member _.NoProblems() = equal [] (issues () |> List.map string) "issues"

    [<StepDefinition("the check only reads the database catalogue")>]
    member _.OnlyCatalogue() =
        let cmds = w.CommandsSinceMark
        check (not cmds.IsEmpty) "the catalogue was read"

        for c in cmds do
            check (c.Sql.StartsWith "SELECT" && c.Sql.Contains "information_schema.columns") c.Sql

    [<StepDefinition("a model with a field whose column does not exist and a record whose table does not exist")>]
    member _.Missing() =
        useModel
            [ schema<Drift.OrderWithNotes> "orders" { identityKey (fun o -> o.Id) }
              schema<Drift.Invoice> "invoices" { key (fun i -> i.Id) } ]

    [<StepDefinition("the missing column is reported with its model and field")>]
    member _.MissingColumn() =
        let i = find SchemaIssueKind.MissingColumn "OrderWithNotes"
        equal (Some "Notes", Some "notes", "orders") (i.Field, i.Column, i.Table) "field, column, table"

    [<StepDefinition("the missing table is reported with its model")>]
    member _.MissingTable() =
        let i = find SchemaIssueKind.MissingTable "Invoice"
        equal "invoices" i.Table "table"

    [<StepDefinition("a model that maps a text field to a numeric column")>]
    member _.TextOverNumeric() =
        useModel [ schema<Drift.OrderTotalAsText> "orders" { identityKey (fun o -> o.Id) } ]

    [<StepDefinition("the incompatible type is reported with the expected and actual types")>]
    member _.Incompatible() =
        let i = find SchemaIssueKind.IncompatibleType "OrderTotalAsText"
        equal (Some "Total") i.Field "field"
        check (i.Message.Contains "column is numeric" && i.Message.Contains "needs text") i.Message
        equal 1 (issues () |> List.length) "only the type is reported"

    [<StepDefinition("a model that maps a non-optional field to a nullable column")>]
    member _.RequiredOverNullable() =
        useModel [ schema<Drift.OrderRequiredReason> "orders" { identityKey (fun o -> o.Id) } ]

    [<StepDefinition("the field is reported as able to read NULL, with a suggestion to make it optional")>]
    member _.Nullable() =
        let i = find SchemaIssueKind.NullableColumn "OrderRequiredReason"
        equal (Some "CancellationReason") i.Field "field"
        check (i.Message.Contains "Make the field an option") i.Message

    [<StepDefinition("a model that does not map a required column without a default")>]
    member _.Unmapped() =
        useModel [ schema<Drift.ProductWithoutName> "products" { identityKey (fun p -> p.Id) } ]

    [<StepDefinition("the unmapped column is reported because inserts would fail")>]
    member _.Unwritable() =
        let i = find SchemaIssueKind.UnwritableColumn "ProductWithoutName"
        equal (Some "name") i.Column "column"
        check (i.Message.Contains "inserts would fail") i.Message

    [<StepDefinition("a many-to-many relationship whose join table column does not exist")>]
    member _.WrongJoin() =
        useModel
            [ schema<Drift.ArticleWrongJoin> "articles" {
                  identityKey (fun a -> a.Id)
                  manyToMany (fun a -> a.Tags) "article_tags" "article_id" "label_id"
              }
              Schemas.tags ]

    [<StepDefinition("the missing join table column is reported with its relationship")>]
    member _.MissingJoinColumn() =
        let i = find SchemaIssueKind.MissingColumn "ArticleWrongJoin"
        equal (Some "Tags", "article_tags", Some "label_id") (i.Field, i.Table, i.Column) "relationship, table, column"
