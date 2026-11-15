namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Reading at most one row
/// (Data from "Orders for several Customers": totals 10, 20, 30 and 40.)
[<Binding>]
type ReadingSteps(w: World) =

    let read (q: Query<Order>) =
        task {
            let! r = w.Repo.One q
            w.Set("one", r)
        }
        |> unit

    let result () = w.Get<Result<Order option, DbError>> "one"

    [<StepDefinition("I read One of a query ordered by total, descending, taking 1")>]
    member _.Largest() = read (plasm { for o in tableOf<Order> do orderByDescending o.Total; take 1 })

    [<StepDefinition("the Order with the largest total is returned")>]
    member _.LargestReturned() =
        match result () with
        | Ok(Some o) -> equal 40m o.Total "total"
        | other -> failwithf "expected the largest Order, got %A" other

    [<StepDefinition("I read One of a query that matches no Order")>]
    member _.NoMatch() = read (plasm { for o in tableOf<Order> do where (o.Total > 1000m) })

    [<StepDefinition("None is returned")>]
    member _.NoneReturned() = equal (Ok None) (result ()) "result"

    [<StepDefinition("I read One of a query that matches several Orders")>]
    member _.Several() = read (plasm { for o in tableOf<Order> do where (o.Status = Published) })

    [<StepDefinition("the read returns an error saying more than one row matched")>]
    member _.TooMany() = equal (Error DbError.MoreThanOneRow) (result ()) "result"

    [<StepDefinition("the error is not a translation error")>]
    member _.NotTranslation() =
        match result () with
        | Error(DbError.Translation t) -> failwithf "reported as a translation error: %A" t
        | _ -> ()

    [<StepDefinition("I read One of the same query twice")>]
    member _.Twice() =
        task {
            let repo = w.Db.RepoWith()
            let q = plasm { for o in tableOf<Order> do where (o.Total > 35m) }
            let! a = repo.One q
            let! b = repo.One q
            ok a |> ignore
            ok b |> ignore
            w.Set("plans", repo.PlansMade)
        }
        |> unit
