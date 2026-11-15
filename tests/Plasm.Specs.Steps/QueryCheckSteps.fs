/// Queries for query-check.feature, in a namespace of their own so a check can
/// be pointed at exactly these modules.
namespace Plasm.Specs.CheckedQueries

open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas

module Good =
    let everyone = Query.from (tableOf<Customer>)

    let byName = Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value) })

    let paid = plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })) }

module Bad =
    // String.Normalize has no SQL translation.
    let normalized = plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = "ada") }

module GoodProjection =
    let namesOf = Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value); select c.Name })

module BadProjection =
    let normalizedNames =
        Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = name.Value); select c.Name })

module BadPrepared =
    let normalizedNamed =
        Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = name.Value) })

namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain

/// Feature: Every query is checked before it is used
[<Binding>]
type QueryCheckSteps(w: World) =

    let check' (inModule: string) =
        w.MarkCommands()
        let problems = w.Repo.CheckQueries(typeof<QueryCheckSteps>.Assembly, $"Plasm.Specs.CheckedQueries.{inModule}")
        w.Set("problems", problems)

    let problems () = w.Get<QueryProblem list> "problems"

    [<StepDefinition("I check the queries of a module whose queries all translate")>]
    member _.CheckGood() = check' "Good"

    [<StepDefinition("I check the queries of a module with a query Plasm cannot translate")>]
    member _.CheckBad() = check' "Bad"

    [<StepDefinition("I check the queries of a module with a prepared query Plasm cannot translate")>]
    member _.CheckBadPrepared() = check' "BadPrepared"

    [<StepDefinition("I check the queries of a module with a prepared projection Plasm cannot translate")>]
    member _.CheckBadProjection() = check' "BadProjection"

    [<StepDefinition("I read the names with the checked prepared projection for the name \"(.*)\"")>]
    member _.ReadProjection(name: string) =
        task {
            let! r = w.Repo.All(CheckedQueries.GoodProjection.namesOf name)
            w.Set("names", ok r)
        }
        |> unit

    [<StepDefinition("only the name \"(.*)\" is returned")>]
    member _.OnlyName(name: string) = equal [ name ] (w.Get<string list> "names") "names"

    [<StepDefinition("no problem is reported")>]
    member _.NoProblem() = equal [] (problems ()) "problems"

    [<StepDefinition("one problem is reported, for the query \"(.*)\", saying why it cannot be translated")>]
    member _.OneProblem(name: string) =
        match problems () with
        | [ p ] ->
            check (p.Query.EndsWith name) $"the problem is for {p.Query}"

            match p.Error with
            | TranslationError.UnsupportedExpression(_, reason) -> check (reason <> "") "no reason given"
            | other -> failwithf "expected an unsupported expression, got %A" other
        | other -> failwithf "expected one problem, got %A" other

    [<StepDefinition("^Customers named \"([^\"]*)\" and \"([^\"]*)\"$")>]
    member _.TwoCustomers(a: string, b: string) =
        task {
            for name in [ a; b ] do
                let! r = w.Repo.Transact(Tx.insert (New.customer name $"{name.ToLower()}@example.com"): Tx<_, unit>)
                ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read with the checked prepared query for the name \"(.*)\"")>]
    member _.ReadPrepared(name: string) =
        task {
            let! r = w.Repo.All(CheckedQueries.Good.byName name)
            w.Set("read", ok r)
        }
        |> unit

    [<StepDefinition("only the Customer named \"(.*)\" is returned")>]
    member _.OnlyNamed(name: string) =
        let read = w.Get<Customer list> "read"
        equal [ name ] (read |> List.map (fun c -> c.Name)) "names"
