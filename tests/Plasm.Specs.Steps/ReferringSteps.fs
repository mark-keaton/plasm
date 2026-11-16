/// A domain of its own for referring.feature: a Category that refers to its
/// own kind, and a Transfer with two fields of one key type.
namespace Plasm.Specs.Referring

open Plasm

type CategoryId = CategoryId of int64

[<Table "ref_categories">]
type Category =
    { Id: CategoryId
      Name: string
      Parent: CategoryId option }

type AccountId = AccountId of int64

[<Table "ref_accounts">]
type Account = { Id: AccountId; Name: string }

type TransferId = TransferId of int64

[<Table "ref_transfers">]
type Transfer =
    { Id: TransferId
      From: AccountId
      To: AccountId
      Amount: decimal }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Category>; typeof<Account>; typeof<Transfer> ]

    let tables =
        "drop table if exists ref_categories;
         create table ref_categories (
           id bigint generated always as identity primary key,
           name text not null,
           parent bigint references ref_categories(id));"

module ReferringGood =
    let ofAtLeast =
        Query.prepare (fun (least: Arg<decimal>) ->
            plasm {
                for o in tableOf<Plasm.TestDomain.Domain.Order> do
                    orderBy o.Total
                    including (plasm { for p in referring<Plasm.TestDomain.Domain.Payment> o do where (p.Amount >= least.Value); orderBy p.CreatedAt })
            })

/// Checked by `repo.CheckQueries`: a Transfer has two fields of AccountId.
module ReferringBad =
    let withTransfers =
        plasm {
            for a in tableOf<Account> do
                including (referring<Transfer> a)
        }

namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.Specs.Referring

/// Feature: A tie that the typed ids prove is left to Plasm
[<Binding>]
type ReferringSteps(w: World) =

    let total (d: decimal) = d.ToString("0.##", Globalization.CultureInfo.InvariantCulture)

    let payment (p: Payment) = $"{total p.Amount} {p.Status}"

    let orderOf (t: decimal) =
        (w.Get<Map<decimal, OrderId>> "includingOrders").[t]

    let pairs (r: Result<('T * 'U list) list, DbError>) =
        r |> Result.map (List.map (fun (o, xs) -> box o, List.map box xs))

    let orderRecord (t: decimal) =
        task {
            let! r = w.Repo.One(plasm { for o in tableOf<Order> do where (o.Id = orderOf t) })
            return (ok r).Value
        }

    [<StepDefinition("I read the Orders by total, including the Payments referring to each, by minute")>]
    member _.Referring() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.Total
                            including (plasm { for p in referring<Payment> o do orderBy p.CreatedAt })
                    }
                )

            w.Set("including", pairs r)
            w.Set("referringSql", (w.CommandsSinceMark |> List.last).Sql)
        }
        |> unit

    [<StepDefinition("the Payments command is the one the tie written by hand sends")>]
    member _.SameSql() =
        task {
            w.MarkCommands()
            let! _ = w.Repo.All Plasm.Specs.Including.IncludingGood.withPayments
            let byHand = (w.CommandsSinceMark |> List.last).Sql
            equal byHand (w.Get<string> "referringSql") "the Payments command"
        }
        |> unit

    [<StepDefinition("I read the Orders by total, including the succeeded Payments referring to each")>]
    member _.Narrowed() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.Total
                            including (plasm { for p in referring<Payment> o do where (p.Status = Succeeded); orderBy p.CreatedAt })
                    }
                )

            w.Set("including", pairs r)
        }
        |> unit

    [<StepDefinition("I read the Orders including the Payments referring to each of at least 25, through a prepared query")>]
    member _.Prepared() =
        task {
            let! r = w.Repo.All(ReferringGood.ofAtLeast 25m)
            w.Set("including", pairs r)
        }
        |> unit

    [<StepDefinition("I read the Orders a Payment refers to")>]
    member _.Exists() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do where (exists (referring<Payment> o)); orderBy o.Total })
            w.Set("includingExists", ok r)
        }
        |> unit

    [<StepDefinition("I read the Payments referring to the Order of 50, by minute")>]
    member _.InHand() =
        task {
            let! order = orderRecord 50m
            let! r = w.Repo.All(plasm { for p in referring<Payment> order do orderBy p.CreatedAt })
            w.Set("referringPayments", ok r)
        }
        |> unit

    [<StepDefinition("the Payments read are (.*)")>]
    member _.PaymentsRead(text: string) =
        equal text (w.Get<Payment list> "referringPayments" |> List.map payment |> String.concat ", ") "payments"

    [<StepDefinition("I read the Orders by total, including the Customer each refers to")>]
    member _.ReferredBy() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.Total
                            includingOne (referredBy<Customer> o)
                    }
                )

            w.Set("includingOne", r |> Result.map (List.map (fun (o, c) -> total o.Total, c |> Option.map (fun (c: Customer) -> c.Name))))
        }
        |> unit

    [<StepDefinition("I read the Customer the Order of 200 refers to")>]
    member _.ReferredByInHand() =
        task {
            let! order = orderRecord 200m
            let! r = w.Repo.All(referredBy<Customer> order)
            w.Set("referredCustomers", ok r)
        }
        |> unit

    [<StepDefinition("the Customer read is Bob")>]
    member _.Bob() =
        equal [ "Bob" ] (w.Get<Customer list> "referredCustomers" |> List.map (fun c -> c.Name)) "customers"

    [<StepDefinition("the Categories \"Tools\", \"Saws\" and \"Drills\" in Tools, and \"Hand saws\" in Saws")>]
    member _.Categories() =
        task {
            let! _ = w.Db.Exec Schemas.tables

            let! _ =
                w.Db.Exec
                    "insert into ref_categories(name, parent) values ('Tools', null);
                     insert into ref_categories(name, parent) select 'Saws', id from ref_categories where name = 'Tools';
                     insert into ref_categories(name, parent) select 'Drills', id from ref_categories where name = 'Tools';
                     insert into ref_categories(name, parent) select 'Hand saws', id from ref_categories where name = 'Saws';"

            w.Set("categoryRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("I read the Categories by name, including their children and their parent")>]
    member _.Family() =
        task {
            let repo = w.Get<Repo> "categoryRepo"

            let! r =
                repo.All(
                    plasm {
                        for c in tableOf<Category> do
                            orderBy c.Name
                            including (plasm { for x in referring<Category> c do orderBy x.Name })
                            includingOne (referredBy<Category> c)
                    }
                )

            w.Set("family", ok r)
        }
        |> unit

    [<StepDefinition("the Categories, their children and their parent are:")>]
    member _.FamilyAre(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["category"], r.["children"].Trim(), r.["parent"] ]

        let actual =
            [ for c, children, parent in w.Get<(Category * Category list * Category option) list> "family" ->
                  c.Name, children |> List.map (fun x -> x.Name) |> String.concat ", ", parent |> Option.map (fun p -> p.Name) |> Option.defaultValue "none" ]

        equal expected actual "categories, children and parents"

    [<StepDefinition("I check the queries of a module that reads the Transfers referring to an Account")>]
    member _.CheckTransfers() =
        let repo = w.Db.RepoWith(model = Schemas.model)
        w.Set("problems", repo.CheckQueries(typeof<ReferringSteps>.Assembly, "Plasm.Specs.Referring.ReferringBad"))

    [<StepDefinition("a problem is reported naming Transfer, AccountId, From and To")>]
    member _.TransferProblem() =
        match w.Get<QueryProblem list> "problems" with
        | [ p ] ->
            let text = string p.Error

            for word in [ "Transfer"; "AccountId"; "From"; "To" ] do
                check (text.Contains word) text
        | other -> failwithf "expected one problem, got %A" other

    [<StepDefinition("I read the Customers referring to an Order")>]
    member _.NoField() =
        task {
            let! order = orderRecord 50m
            let! r = w.Repo.All(referring<Customer> order)
            w.Set("referringRefusal", r)
        }
        |> unit

    [<StepDefinition("the read is refused, saying Customer has no field holding an OrderId")>]
    member _.NoFieldRefused() =
        match w.Get<Result<Customer list, DbError>> "referringRefusal" with
        | Error(DbError.Translation e) ->
            let text = string e
            check (text.Contains "Customer" && text.Contains "no field" && text.Contains "OrderId") text
        | other -> failwithf "expected a refusal, got %A" other

    [<StepDefinition("I read the Payments referring to the Order of 50 as an option")>]
    member _.AsOption() =
        task {
            let! order = orderRecord 50m
            let! r = w.Repo.All(referring<Payment> (Some order))
            w.Set("referringOption", r)
        }
        |> unit

    [<StepDefinition("the read is refused, saying Order option is not a stored record")>]
    member _.OptionRefused() =
        match w.Get<Result<Payment list, DbError>> "referringOption" with
        | Error(DbError.Translation e) ->
            let text = string e
            check (text.Contains "Order option" && text.Contains "not one") text
        | other -> failwithf "expected a refusal, got %A" other
