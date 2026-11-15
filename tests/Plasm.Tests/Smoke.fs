module Plasm.Tests.Smoke

open System
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

[<Fact>]
let ``milestone page with nested batched loads`` () =
    task {
        let! db = freshDefault ()
        let! c = Seed.customer db "Ada" "ada@example.com"
        let! p1 = Seed.product db "A"
        let! p2 = Seed.product db "B"
        let! o1 = Seed.order db (Some c) "published" 10m (Seed.at 1)
        let! o2 = Seed.order db (Some c) "published" 20m (Seed.at 2)
        let! _ = Seed.line db o1 p1 1 5m
        let! _ = Seed.line db o1 p2 2 2.5m
        let! _ = Seed.payment db o1 10m "succeeded" (Seed.at 3)
        let! _ = Seed.payment db o2 20m "failed" (Seed.at 4)

        let page =
            plasm {
                for o in Schemas.orders do
                    where (o.Status = Published)
                    orderByDescending o.PlacedAt
                    take 20
            }

        // Another aggregate is reached by a query of its own.
        let paymentsOf =
            Query.prepare (fun (ids: Arg<OrderId list>) ->
                plasm {
                    for p in Schemas.payments do
                        where (List.contains p.OrderId ids.Value && p.Status = Succeeded)
                        orderBy p.CreatedAt
                })

        let plan = db.Repo.Explain page |> ok
        printfn "%s" plan.Sql
        for l in plan.LoadPaths do printfn "load %s" l

        let! orders = db.Repo.All page
        let orders = ok orders
        let! payments = db.Repo.All(paymentsOf (orders |> List.map (fun o -> o.Id)))
        let payments = ok payments
        for e in db.Recorder.Events do printfn "%s" e.Sql
        Assert.Equal(2, orders.Length)
        Assert.Equal(o2, orders.[0].Id)
        let lines = orders.[1].Lines
        Assert.Equal(2, lines.Length)
        Assert.Equal(p2, lines.[1].ProductId)
        Assert.Equal(1, payments.Length)
        Assert.Equal(orders.[1].Id, payments.[0].OrderId)
        Assert.Equal(3, db.Recorder.Count)
    }
