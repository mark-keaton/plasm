/// A domain of its own for query-exists.feature: orders and payments as two
/// aggregates, a payment referring to its order by id.
namespace Plasm.Specs.ExistsDomain

open Plasm

type OrderId = OrderId of int64
type PaymentId = PaymentId of int64

[<Table "exists_orders">]
type Order =
    { Id: OrderId
      Number: string
      Total: decimal }

[<Table "exists_payments">]
type Payment =
    { Id: PaymentId
      Order: OrderId
      Amount: decimal
      Succeeded: bool }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Order>; typeof<Payment> ]

    let tables =
        "drop table if exists exists_payments; drop table if exists exists_orders;
         create table exists_orders (id bigint generated always as identity primary key, number text not null, total numeric(12,2) not null);
         create table exists_payments (
           id bigint generated always as identity primary key,
           \"order\" bigint not null references exists_orders(id),
           amount numeric(12,2) not null,
           succeeded boolean not null);"

open Schemas

/// Queries checked by `repo.CheckQueries`, in modules of their own.
module ExistsGood =
    let paid =
        plasm {
            for o in tableOf<Order> do
                where (exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id && p.Succeeded) }))
                orderBy o.Number
        }

    let paidAtLeast =
        Query.prepare (fun (least: Arg<decimal>) ->
            plasm {
                for o in tableOf<Order> do
                    where (exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id && p.Amount >= least.Value) }))
                    orderBy o.Number
            })

module ExistsBad =
    // String.Normalize has no SQL translation.
    let untranslatable =
        plasm {
            for o in tableOf<Order> do
                where (exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id && o.Number.Normalize() = "A") }))
        }

namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.Specs.ExistsDomain
open Plasm.Specs.ExistsDomain.Schemas

/// Feature: A query can ask whether another query finds anything
[<Binding>]
type ExistsSteps(w: World) =

    let repo () = w.Get<Repo> "existsRepo"

    let read (q: Query<Order>) =
        task {
            w.MarkCommands()
            let! r = repo().All q
            w.Set("existsRead", r)
        }
        |> unit

    let insertOrder number total =
        task {
            let! r = repo().Transact(Tx.insert { Id = OrderId 0L; Number = number; Total = total }: Tx<_, unit>)
            return ok r
        }

    [<StepDefinition("orders and payments as separate aggregates")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec Schemas.tables
            w.Set("existsRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("^the Orders \"A\" for 10, \"B\" for 50 and \"C\" for 200$")>]
    member _.Orders() =
        task {
            let! a = insertOrder "A" 10m
            let! b = insertOrder "B" 50m
            let! c = insertOrder "C" 200m
            w.Set("existsOrders", Map [ "A", a; "B", b; "C", c ])
        }
        |> unit

    [<StepDefinition("^\"A\" has a succeeded payment of 10, and \"B\" has a failed payment of 50$")>]
    member _.Payments() =
        task {
            let byNumber = w.Get<Map<string, Order>> "existsOrders"

            for n, amount, ok' in [ "A", 10m, true; "B", 50m, false ] do
                let! r =
                    repo().Transact(
                        Tx.insert
                            { Id = PaymentId 0L
                              Order = byNumber.[n].Id
                              Amount = amount
                              Succeeded = ok' }
                        : Tx<_, unit>
                    )

                ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read the Orders with a succeeded payment")>]
    member _.Paid() = read ExistsGood.paid

    [<StepDefinition("I read the Orders without a succeeded payment")>]
    member _.Unpaid() =
        read (
            plasm {
                for o in tableOf<Order> do
                    where (not (exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id && p.Succeeded) })))
                    orderBy o.Number
            }
        )

    [<StepDefinition("I read the Orders over 100 or with any payment")>]
    member _.LargeOrPaid() =
        read (
            plasm {
                for o in tableOf<Order> do
                    where (o.Total > 100m || exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id) }))
                    orderBy o.Number
            }
        )

    [<StepDefinition("I read the Orders with a payment of at least 50, through a prepared query")>]
    member _.PaidAtLeast() = read (ExistsGood.paidAtLeast 50m)

    [<StepDefinition("I read the Orders with an exists query that takes one row")>]
    member _.Paged() =
        read (
            plasm {
                for o in tableOf<Order> do
                    where (exists (plasm { for p in tableOf<Payment> do where (p.Order = o.Id); orderBy p.Amount; take 1 }))
            }
        )

    [<StepDefinition("^the Orders read are (.+)$")>]
    member _.ReadAre(text: string) =
        let expected = System.Text.RegularExpressions.Regex.Matches(text, "\"(\\w+)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
        let read = ok (w.Get<Result<Order list, DbError>> "existsRead")
        equal expected (read |> List.map (fun o -> o.Number)) "orders"

    [<StepDefinition("^one command is sent$")>]
    member _.OneCommand() = equal 1 w.CommandsSinceMark.Length "commands"

    [<StepDefinition("the read is refused, saying an exists query is a condition only")>]
    member _.Refused() =
        match w.Get<Result<Order list, DbError>> "existsRead" with
        | Error(DbError.Translation e) -> check ((string e).Contains "condition only") (string e)
        | other -> failwithf "expected a translation error, got %A" other

    [<StepDefinition("I check the queries of a module whose exists queries translate")>]
    member _.CheckGood() =
        w.Set("problems", repo().CheckQueries(typeof<ExistsSteps>.Assembly, "Plasm.Specs.ExistsDomain.ExistsGood"))

    [<StepDefinition("I check the queries of a module whose exists query cannot be translated")>]
    member _.CheckBad() =
        w.Set("problems", repo().CheckQueries(typeof<ExistsSteps>.Assembly, "Plasm.Specs.ExistsDomain.ExistsBad"))
