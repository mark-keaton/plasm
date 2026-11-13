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

        let successful =
            plasm {
                for p in Schemas.payments do
                    where (p.Status = Succeeded)
                    orderBy p.CreatedAt
            }

        let linesWithProduct = plasm { for l in Schemas.orderLines do preload l.Product }

        let page =
            plasm {
                for o in Schemas.orders do
                    where (o.Status = Published)
                    orderByDescending o.PlacedAt
                    take 20
                    preload o.Lines linesWithProduct
                    preload o.Payments successful
            }

        let plan = db.Repo.Explain page |> ok
        printfn "%s" plan.Sql
        for l in plan.LoadPaths do printfn "load %s" l

        let! orders = db.Repo.All page
        let orders = ok orders
        for e in db.Recorder.Events do printfn "%s" e.Sql
        Assert.Equal(2, orders.Length)
        Assert.Equal(o2, orders.[0].Id)
        let lines = Assoc.get orders.[1].Lines
        Assert.Equal(2, lines.Length)
        Assert.Equal("B", (Assoc.get lines.[1].Product).Sku)
        Assert.Equal(1, (Assoc.get orders.[1].Payments).Length)
        Assert.Equal<Payment list>([], Assoc.get orders.[0].Payments)
        Assert.Equal(NotLoaded, orders.[0].Customer)
        Assert.Equal(4, db.Recorder.Count)
    }
