namespace Plasm.Specs.UniqueWithin

open Plasm

/// unique-within.feature: tags whose name, and whose slug, are unique within
/// their tenant.
type TagId = TagId of int64

[<Table "tenant_tags">]
type Tag =
    { Id: TagId
      Tenant: string
      [<Unique(Within = [| "Tenant" |])>]
      Name: string
      [<Unique(Within = [| "Tenant" |])>]
      Slug: string }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Tag> ]

    let tables =
        "create table tenant_tags (
           id bigint generated always as identity primary key,
           tenant text not null,
           name text not null,
           slug text not null,
           constraint tenant_tags_tenant_name_key unique (tenant, name),
           constraint tenant_tags_tenant_slug_key unique (tenant, slug));"

/// Records whose field cannot be unique within what they name.
module Refused =
    type RegionalId = RegionalId of int64

    [<Table "regional">]
    type Regional =
        { Id: RegionalId
          Region: string option
          [<Unique(Within = [| "Region" |])>]
          Name: string }

    type OptionalNameId = OptionalNameId of int64

    [<Table "optional_name">]
    type OptionalName =
        { Id: OptionalNameId
          Tenant: string
          [<Unique(Within = [| "Tenant" |])>]
          Name: string option }

    type OwnerlessId = OwnerlessId of int64

    [<Table "ownerless">]
    type Ownerless =
        { Id: OwnerlessId
          [<Unique(Within = [| "Owner" |])>]
          Name: string }

    type Address = { Street: string; City: string }
    type PlacedId = PlacedId of int64

    [<Table "placed">]
    type Placed =
        { Id: PlacedId
          Place: Address
          [<Unique(Within = [| "Place" |])>]
          Name: string }

    let models =
        Map
            [ "an optional field", (typeof<Regional>, "Name")
              "a field, while it is optional", (typeof<OptionalName>, "Name")
              "a field that does not exist", (typeof<Ownerless>, "Name")
              "a value of several columns", (typeof<Placed>, "Name") ]

namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs.UniqueWithin

[<Binding>]
type UniqueWithinSteps(w: World) =

    let repo () = w.Get<Repo> "tagsRepo"

    let insert (name: string) (tenant: string) =
        task {
            let tag =
                { Id = TagId 0L
                  Tenant = tenant
                  Name = name
                  Slug = $"{name}-{Guid.NewGuid():N}" }

            let! r = repo().Transact(Tx.insert tag: Tx<Tag, unit>)
            w.Set("tagWrite", r)
            return r
        }

    [<StepDefinition("tags unique within their tenant")>]
    member _.Tables() =
        task {
            do! w.Db.MakeTables(Schemas.tables, Schemas.model)
            w.Set("tagsRepo", w.Db.RepoWith(model = Schemas.model))
        }
        :> Task

    [<StepDefinition("^the Tag \"(.*)\" of the tenant \"(.*)\"$")>]
    member _.Given(name: string, tenant: string) =
        task {
            let! r = insert name tenant
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("^I insert the Tag \"(.*)\" of the tenant \"([^\"]*)\"(?: again)?$")>]
    member _.Insert(name: string, tenant: string) = insert name tenant :> Task

    [<StepDefinition("it is stored")>]
    member _.Stored() =
        ok (w.Get<Result<Tag, TransactionError<unit>>> "tagWrite") |> ignore

    [<StepDefinition("^the insert fails with the (\\w+) field's uniqueness error, naming (\\w+)$")>]
    member _.Duplicate(field: string, name: string) =
        match w.Get<Result<Tag, TransactionError<unit>>> "tagWrite" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check (errors |> List.exists (fun e -> e.Field = field && e.Kind = FieldErrorKind.UniqueConstraint name)) $"the errors are %A{errors}"
        | other -> failwithf "expected the field's uniqueness error, got %A" other

    [<StepDefinition("^the tenant_tags table has lost its constraint (\\w+)$")>]
    member _.Lost(name: string) = w.Db.DropConstraint("tenant_tags", name) |> unit

    [<StepDefinition("^the tenant_tags table's constraint (\\w+) is renamed (\\w+)$")>]
    member _.Renamed(name: string, other: string) =
        w.Db.Exec $"alter table tenant_tags rename constraint {name} to {other}" |> unit

    [<StepDefinition("I check the tenant tags against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("issues", ok r)
        }
        :> Task

    [<StepDefinition("the missing uniqueness of Tag.Name within tenant is reported")>]
    member _.Missing() =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MissingUnique
                 && i.Record = "Tag"
                 && i.Field = Some "Name"
                 && i.Message.Contains "unique (tenant, name)"))
            $"the issues are %A{issues}"

    [<StepDefinition("^(\\w+) is reported as holding Tag.Name's uniqueness, with the rename to (\\w+)$")>]
    member _.Misnamed(actual: string, expected: string) =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MisnamedConstraint
                 && i.Field = Some "Name"
                 && i.Message.Contains actual
                 && i.Message.Contains $"rename {actual} to {expected}"))
            $"the issues are %A{issues}"

    [<StepDefinition("^I build a model of a record whose field is unique within (.*)$")>]
    member _.Build(within: string) =
        let t, field = Refused.models.[within]
        w.Set("withinField", field)
        w.Set("withinBuild", Model.build [] [ t ])

    [<StepDefinition("^the field is refused, saying \"(.*)\"$")>]
    member _.Refused(saying: string) =
        match w.Get<Result<Model, ConfigError list>> "withinBuild" with
        | Ok _ -> failwith "the model was built"
        | Error errors ->
            let field = w.Get<string> "withinField"
            check (errors |> List.exists (fun e -> e.Property = Some field && e.Message.Contains saying)) $"the errors are %A{errors}"
