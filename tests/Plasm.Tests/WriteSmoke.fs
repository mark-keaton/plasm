module Plasm.Tests.WriteSmoke

open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

[<Fact>]
let ``write path smoke`` () =
    task {
        use! db = freshDefault ()
        let! c = Seed.customer db "Ada" "ada@example.com"
        let! p = Seed.product db "A"

        let draft = { New.order (Some c) Draft 10m with Lines = [ New.line p 2 5m ] }
        let write (tx: Tx<'T, unit>) = db.Repo.Transact tx

        let! inserted = write (Tx.insert draft)
        let order = ok (inserted |> Result.mapError string)
        printfn "%A" order
        Assert.NotEqual(OrderId 0L, order.Id)
        Assert.Equal(OrderId 0L, draft.Id)
        Assert.Equal(1, order.Lines.Length)
        Assert.Equal(order.Id, order.Lines.[0].OrderId)

        // update with version
        let! updated = write (Tx.update (order, { order with Status = Published }))
        let updated = ok (updated |> Result.mapError string)
        Assert.Equal(2, updated.Version)

        // stale
        let! stale = write (Tx.update (order, { order with Status = Cancelled }))

        match stale with
        | Error { Failure = TxFailure.Conflict _ } -> ()
        | other -> failwithf "expected a conflict, got %A" other

        // constraint
        let! dup = write (Tx.insert (New.customer "Bob" "ada@example.com"))

        match dup with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> printfn "%A" errors
        | other -> failwithf "expected invalid, got %A" other

        // transaction
        let tx =
            transaction {
                let! o = Tx.insert (New.order (Some c) Draft 1m)
                let! _ = Tx.insert (New.order (Some c) Draft -1m)
                return o
            }

        let! r = db.Repo.Transact tx
        printfn "%A" (r |> Result.map ignore)
        let! count = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(1L, count)

        for e in db.Recorder.Events do
            printfn "%s" e.Sql
    }
