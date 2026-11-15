namespace Plasm.Specs

open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Test sandboxes
[<Binding>]
type SandboxSteps(w: World) =

    let insert (repo: Repo) name =
        repo.Transact(Tx.insert (New.customer name $"{name}@example.com"): Tx<_, unit>)

    let names (repo: Repo) =
        task {
            let! r = repo.All(plasm { for c in Schemas.customers do orderBy c.Name })
            return ok r |> List.map (fun c -> c.Name)
        }

    /// Names visible outside any sandbox (raw SQL on a separate connection).
    let committed () =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from customers")
            return n
        }

    [<StepDefinition("code inside a sandbox inserts a Customer and reads it back")>]
    member _.InsertAndRead() =
        task {
            let! seen =
                w.Repo.Sandbox(fun repo ->
                    task {
                        let! r = insert repo "Ada"
                        ok r |> ignore
                        return! names repo
                    })

            w.Set("seen", seen)
        }
        |> unit

    [<StepDefinition("the Customer is visible inside the sandbox")>]
    member _.Visible() = equal [ "Ada" ] (w.Get<string list> "seen") "inside"

    [<StepDefinition("after the sandbox ends the Customer does not exist")>]
    [<StepDefinition("after the sandbox ends neither exists")>]
    [<StepDefinition("after both end neither Customer exists")>]
    [<StepDefinition("after the outer sandbox ends neither exists")>]
    member _.Gone() =
        task {
            let! n = committed ()
            equal 0L n "customers after the sandbox"
        }
        |> unit

    [<StepDefinition("code inside a sandbox inserts a Customer and opens an inner sandbox that inserts another")>]
    member _.Nested() =
        task {
            let! seen =
                w.Repo.Sandbox(fun outer ->
                    task {
                        let! a = insert outer "Ada"
                        ok a |> ignore

                        let! inner =
                            outer.Sandbox(fun inner ->
                                task {
                                    let! b = insert inner "Bob"
                                    ok b |> ignore
                                    return! names inner
                                })

                        let! after = names outer
                        return inner, after
                    })

            w.Set("nested", seen)
        }
        |> unit

    [<StepDefinition("the inner sandbox sees both Customers")>]
    member _.InnerSeesBoth() =
        equal [ "Ada"; "Bob" ] (fst (w.Get<string list * string list> "nested")) "customers inside the inner sandbox"

    [<StepDefinition("after the inner sandbox ends only the outer sandbox's Customer is visible")>]
    member _.OuterOnly() =
        equal [ "Ada" ] (snd (w.Get<string list * string list> "nested")) "customers after the inner sandbox"

    [<StepDefinition("code inside a sandbox runs one transaction that succeeds and one that fails")>]
    member _.Transactions() =
        task {
            let! seen =
                w.Repo.Sandbox(fun repo ->
                    task {
                        let! a = repo.Transact(Tx.insert (New.customer "Ada" "ada@example.com"))
                        ok a |> ignore

                        let! b =
                            repo.Transact(
                                transaction {
                                    let! _ = Tx.insert (New.customer "Bob" "bob@example.com")
                                    return! Tx.fail "stop"
                                }
                            )

                        equal "fail" (err b).Step.Action "failed step"
                        return! names repo
                    })

            w.Set("seen", seen)
        }
        |> unit

    [<StepDefinition("the successful transaction's changes are visible inside the sandbox")>]
    member _.SuccessVisible() = check (w.Get<string list> "seen" |> List.contains "Ada") "Ada visible"

    [<StepDefinition("the failed transaction's changes are not")>]
    member _.FailedInvisible() = equal [ "Ada" ] (w.Get<string list> "seen") "inside"

    [<StepDefinition("code inside a sandbox hits a unique constraint violation and then continues")>]
    member _.Violation() =
        task {
            let! outcome =
                w.Repo.Sandbox(fun repo ->
                    task {
                        let! first = insert repo "Ada"
                        ok first |> ignore
                        let! duplicate = insert repo "Ada"
                        let! later = insert repo "Bob"
                        let! seen = names repo
                        return duplicate, later, seen
                    })

            w.Set("violation", outcome)
        }
        |> unit

    [<StepDefinition("the violation is reported as usual")>]
    member _.Reported() =
        let duplicate, _, _ = w.Get<Result<Customer, TransactionError<unit>> * Result<Customer, TransactionError<unit>> * string list> "violation"

        match duplicate with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> equal [ FieldErrorKind.UniqueConstraint "customers_email_key" ] (errors |> List.map (fun e -> e.Kind)) "kind"
        | other -> failwithf "expected a unique violation, got %A" other

    [<StepDefinition("later work in the same sandbox succeeds")>]
    member _.Continues() =
        let _, later, seen = w.Get<Result<Customer, TransactionError<unit>> * Result<Customer, TransactionError<unit>> * string list> "violation"
        ok later |> ignore
        equal [ "Ada"; "Bob" ] seen "customers inside"

    [<StepDefinition("two sandboxes run at the same time and each inserts a Customer")>]
    member _.Concurrent() =
        task {
            let run name =
                w.Repo.Sandbox(fun repo ->
                    task {
                        let! r = insert repo name
                        ok r |> ignore
                        do! Task.Delay 200 // overlap with the other sandbox
                        return! names repo
                    })

            let! both = Task.WhenAll [| run "Ada"; run "Bob" |]
            w.Set("both", both)
        }
        |> unit

    [<StepDefinition("each sandbox sees only its own Customer")>]
    member _.OwnOnly() =
        equal [| [ "Ada" ]; [ "Bob" ] |] (w.Get<string list[]> "both") "customers seen by each sandbox"

    [<StepDefinition("code inside a sandbox starts several reads at once")>]
    member _.Overlapping() =
        task {
            let! results =
                w.Repo.Sandbox(fun repo ->
                    task {
                        for i in 1..3 do
                            let! _ = Seed.order w.Db None "draft" 1m (Seed.at i) // committed outside: visible inside
                            ()

                        let! r = insert repo "Ada"
                        ok r |> ignore
                        let a = repo.Count(Query.from Schemas.orders)
                        let b = names repo
                        let c = repo.Exists(Query.from Schemas.customers)
                        let! n = a
                        let! seen = b
                        let! any = c
                        return ok n, seen, ok any
                    })

            w.Set("overlap", results)
        }
        |> unit

    [<StepDefinition("they all complete with correct results")>]
    member _.AllComplete() =
        equal (3L, [ "Ada" ], true) (w.Get<int64 * string list * bool> "overlap") "results"
