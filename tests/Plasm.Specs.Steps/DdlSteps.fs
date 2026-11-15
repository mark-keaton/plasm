namespace Plasm.Specs

open Reqnroll
open Plasm

/// Feature: Plasm prints the DDL its model expects
[<Binding>]
type DdlSteps(w: World) =

    let modelNamed (name: string) =
        match name with
        | "the test domain" -> w.Db.Model
        | "Tickets" -> Model.buildSchemasOrFail [] [ Plasm.Specs.Conventions.tickets ]
        | "Cards with a Stage" -> UnionCards.model
        | "Invoices with their lines" -> OwnedInvoices.model
        | "Cards linked to tags" -> LinkedTags.model
        | other -> failwith $"no model named {other}"

    let repoFor name = w.Db.RepoWith(model = modelNamed name)

    [<StepDefinition("an empty database")>]
    member _.Empty() = w.Db.Exec "drop schema public cascade; create schema public;" |> unit

    [<StepDefinition("^I run the DDL Plasm prints for (.+)$")>]
    member _.RunDdl(name: string) =
        task {
            let ddl = (repoFor name).Ddl()
            w.Set("ddl", ddl)
            let! _ = w.Db.Exec ddl
            ()
        }
        |> unit

    [<StepDefinition("^checking (.+) against that database reports no problems$")>]
    member _.Check(name: string) =
        task {
            let! r = (repoFor name).CheckSchema()
            let issues = ok r
            let ddl = w.Get<string> "ddl"
            check issues.IsEmpty $"the schema check reports %A{issues}\nfor the DDL\n{ddl}"
        }
        |> unit

    [<StepDefinition("^I print the DDL for (.+)$")>]
    member _.Print(name: string) =
        w.MarkCommands()
        w.Set("ddl", (repoFor name).Ddl())

    [<StepDefinition("^it declares \"(.*)\"$")>]
    member _.Declares(text: string) =
        let ddl = w.Get<string> "ddl"
        check (ddl.Contains text) $"the DDL does not declare {text}:\n{ddl}"

    [<StepDefinition("^it does not declare an index on \"(.*)\"$")>]
    member _.NoIndex(target: string) =
        let ddl = w.Get<string> "ddl"
        check (not (ddl.Contains $"on {target}")) $"the DDL declares an index on {target}:\n{ddl}"

    [<StepDefinition("the payments table has no index on order_id")>]
    member _.NoPaymentIndex() =
        w.Db.Exec "drop index if exists payments_order_id_idx" |> unit
