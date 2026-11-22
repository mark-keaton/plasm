namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Records mapped deliberately wrongly onto the test database.
module Drift =
    [<Table "orders">]
    type OrderWithNotes =
        { Id: OrderId
          Status: OrderStatus
          Total: decimal
          PlacedAt: DateTime
          Notes: string }

    [<Table "invoices">]
    type Invoice = { [<Assigned>] Id: int64; Number: string }

    [<Table "orders">]
    type OrderTotalAsText =
        { Id: OrderId
          Status: OrderStatus
          Total: string
          PlacedAt: DateTime }

    /// orders.version is an integer column on both databases.
    [<Table "orders">]
    type OrderVersionAsText =
        { Id: OrderId
          Status: OrderStatus
          Total: decimal
          PlacedAt: DateTime
          Version: string }

    [<Table "orders">]
    type OrderRequiredReason =
        { Id: OrderId
          Status: OrderStatus
          Total: decimal
          PlacedAt: DateTime
          CancellationReason: string }

    [<Table "products">]
    type ProductWithoutName = { Id: ProductId; Sku: string }

    [<Table "articles">]
    type ArticleWrongJoin =
        { Id: ArticleId
          Title: string
          [<JoinTable("article_tags", "article_id", "label_id")>]
          Tags: Tag list }

    let model (types: Type list) = Model.buildOrFail Schemas.scalars types

/// Feature: Checking the model against the database schema
[<Binding>]
type SchemaCheckSteps(w: World) =

    let issues () = w.Get<SchemaIssue list> "issues"

    let find kind (model: string) =
        match issues () |> List.tryFind (fun i -> i.Kind = kind && i.Model = model) with
        | Some i -> i
        | None -> failwithf "expected %A for %s, got %A" kind model (issues () |> List.map string)

    let useModel (types: Type list) = w.Set("checkModel", Drift.model types)

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
            let catalogue =
                match w.Db.Kind with
                | PostgreSQL -> [ "information_schema.columns"; "pg_index"; "pg_constraint"; "pg_type" ]
                | SQLite -> [ "pragma_table_xinfo"; "pragma_index_list"; "pragma_foreign_key_list"; "pragma_table_info"; "sqlite_schema" ]
            check (c.Sql.StartsWith "SELECT" && catalogue |> List.exists c.Sql.Contains) c.Sql

    [<StepDefinition("a model with a field whose column does not exist and a record whose table does not exist")>]
    member _.Missing() =
        useModel
            [ typeof<Drift.OrderWithNotes>; typeof<Drift.Invoice> ]

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
        useModel [ typeof<Drift.OrderTotalAsText> ]

    [<StepDefinition("the incompatible type is reported with the expected and actual types")>]
    member _.Incompatible() =
        let i = find SchemaIssueKind.IncompatibleType "OrderTotalAsText"
        equal (Some "Total") i.Field "field"
        check (i.Message.Contains "column is numeric" && i.Message.Contains "needs text") i.Message
        equal 1 (issues () |> List.length) "only the type is reported"

    [<StepDefinition("a model that maps a text field to an integer column")>]
    member _.TextOverInteger() =
        useModel [ typeof<Drift.OrderVersionAsText> ]

    [<StepDefinition("the incompatible type of the integer column is reported with the expected and actual types")>]
    member _.IncompatibleInteger() =
        let i = find SchemaIssueKind.IncompatibleType "OrderVersionAsText"
        equal (Some "Version") i.Field "field"
        check (i.Message.Contains "column is INTEGER" && i.Message.Contains "needs TEXT") i.Message
        equal 1 (issues () |> List.length) "only the type is reported"

    [<StepDefinition("a model that maps a non-optional field to a nullable column")>]
    member _.RequiredOverNullable() =
        useModel [ typeof<Drift.OrderRequiredReason> ]

    [<StepDefinition("the field is reported as able to read NULL, with a suggestion to make it optional")>]
    member _.Nullable() =
        let i = find SchemaIssueKind.NullableColumn "OrderRequiredReason"
        equal (Some "CancellationReason") i.Field "field"
        check (i.Message.Contains "Make the field an option") i.Message

    [<StepDefinition("a model that does not map a required column without a default")>]
    member _.Unmapped() =
        useModel [ typeof<Drift.ProductWithoutName> ]

    [<StepDefinition("the unmapped column is reported because inserts would fail")>]
    member _.Unwritable() =
        let i = find SchemaIssueKind.UnwritableColumn "ProductWithoutName"
        equal (Some "name") i.Column "column"
        check (i.Message.Contains "inserts would fail") i.Message

    [<StepDefinition("mappings for Articles and Tags linked through a join table")>]
    member _.LinkedMappings() =
        check (w.Db.Model.Types |> List.contains typeof<Article>) "Article is mapped"

    [<StepDefinition("a link whose join table column does not exist")>]
    member _.WrongJoin() =
        useModel
            [ typeof<Drift.ArticleWrongJoin>; typeof<Tag> ]

    [<StepDefinition("the missing join table column is reported with its relationship")>]
    member _.MissingJoinColumn() =
        let i = find SchemaIssueKind.MissingColumn "ArticleWrongJoin"
        equal (Some "Tags", "article_tags", Some "label_id") (i.Field, i.Table, i.Column) "relationship, table, column"
