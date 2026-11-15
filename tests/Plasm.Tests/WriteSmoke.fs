module Plasm.Tests.WriteSmoke

open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

[<Fact>]
let ``write path smoke`` () =
    task {
        let! db = freshDefault ()
        let! c = Seed.customer db "Ada" "ada@example.com"
        let! p = Seed.product db "A"

        let draft = New.order (Some c) Draft 10m

        let cs =
            Changeset.insert draft
            |> Changeset.putChildren((fun o -> o.Lines), [ Changeset.insert (New.line p 2 5m) ])

        let! inserted = db.Repo.Insert cs
        let order = ok (inserted |> Result.mapError string)
        printfn "%A" order
        Assert.NotEqual(OrderId 0L, order.Id)
        Assert.Equal(OrderId 0L, draft.Id)
        let lines = Assoc.get order.Lines
        Assert.Equal(1, lines.Length)
        Assert.Equal(order.Id, lines.[0].OrderId)

        // update with version
        let! updated = db.Repo.Update(order |> Changeset.edit (fun o -> { o with Status = Published }))
        let updated = ok (updated |> Result.mapError string)
        Assert.Equal(2, updated.Version)

        // stale
        let! stale = db.Repo.Update(order |> Changeset.edit (fun o -> { o with Status = Cancelled }))

        match stale with
        | Error(WriteError.Stale _) -> ()
        | other -> failwithf "expected stale, got %A" other

        // constraint
        let! dup =
            db.Repo.Insert(Changeset.insert (New.customer "Bob" "ada@example.com"))

        match dup with
        | Error(WriteError.Invalid cs) -> printfn "%A" cs.Errors
        | other -> failwithf "expected invalid, got %A" other

        // transaction
        let tx =
            transaction {
                let! o = Tx.insert (New.order (Some c) Draft 1m)
                let! l = Tx.insert { New.line p 1 1m with OrderId = o.Id }
                let! _ = Tx.insert (New.order (Some c) Draft -1m)
                return o, l
            }

        let! r = db.Repo.Transact tx
        printfn "%A" (r |> Result.map ignore)
        let! count = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(1L, count)

        for e in db.Recorder.Events do
            printfn "%s" e.Sql
    }
