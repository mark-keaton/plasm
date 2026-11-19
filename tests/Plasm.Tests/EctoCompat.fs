/// Behavioral compatibility suite: cases adapted from Ecto's tests
/// (https://github.com/elixir-ecto/ecto, Apache-2.0; see NOTICE). Each test
/// cites its source. Outcomes are compared, not SQL text. Deliberate
/// differences are named "ecto (deliberate difference): ..." and assert Plasm's
/// behavior.
module Plasm.Tests.EctoCompat

open System
open System.Collections.Generic
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

let allOrders (db: TestDb) =
    task {
        let! r = db.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id })
        return ok r
    }

let byId (os: Order list) id = os |> List.find (fun o -> o.Id = id)
let lineIds (o: Order) = o.Lines |> List.map (fun l -> l.Id) |> List.sort

let input (pairs: (string * string) list) =
    Dictionary<string, string>(dict pairs) :> IReadOnlyDictionary<string, string>

let order0 = New.order None Draft 10m

// Preloading (integration_test/cases/preload.exs) was removed with preload
// (one style, 2026-11-14): an aggregate is always read with what it owns
// (owned-collections.feature), and other aggregates are reached by query.

// ===========================================================================
// Repo (integration_test/cases/repo.exs)
// ===========================================================================

// repo.exs: test "insert, update and delete"
[<Fact>]
let ``ecto: insert, update and delete`` () =
    task {
        use! db = freshDefault ()
        let! inserted = db.Repo.Transact(Tx.insert order0: Tx<_, unit>)
        let inserted = ok (inserted |> Result.mapError string)
        let! updated = db.Repo.Transact(inserted |> edit (fun o -> { o with Total = 2m }))
        let updated = ok (updated |> Result.mapError string)
        Assert.Equal(2m, updated.Total)
        let! deleted = db.Repo.Transact(Tx.delete updated: Tx<_, unit>)
        Assert.Equal(updated, ok (deleted |> Result.mapError string))
        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// repo.exs: test "insert and update with empty changeset"
[<Fact>]
let ``ecto: insert and update with empty changeset`` () =
    task {
        use! db = freshDefault ()
        let! inserted = db.Repo.Transact(Tx.insert order0: Tx<_, unit>)
        let inserted = ok (inserted |> Result.mapError string)
        db.Recorder.Clear()
        // An update that changes nothing.
        let! a = db.Repo.Transact(Tx.update (inserted, inserted): Tx<_, unit>)
        let! b = db.Repo.Transact(Tx.update (inserted, inserted): Tx<_, unit>)
        Assert.Equal(inserted, ok (a |> Result.mapError string))
        Assert.Equal(inserted, ok (b |> Result.mapError string))
        Assert.Equal(0, db.Recorder.Count)
    }

// repo.exs: test "insert and update with changeset read after writes"
[<Fact>]
let ``ecto: insert and update with changeset read after writes`` () =
    task {
        use! db = freshDefault ()
        let! inserted = db.Repo.Transact(Tx.insert order0: Tx<_, unit>)
        let inserted = ok (inserted |> Result.mapError string)
        Assert.NotEqual(order0.Id, inserted.Id)
        Assert.Equal(OrderId 0L, order0.Id)
        let! stored = db.Scalar<decimal>("select total from orders where id = @i", [ "i", box (let (OrderId v) = inserted.Id in v) ])
        Assert.Equal(10m, stored)
    }

// repo.exs: test "optimistic locking in update/delete operations"
[<Fact>]
let ``ecto: optimistic locking in update/delete operations`` () =
    task {
        use! db = freshDefault ()
        let! base' = db.Repo.Transact(Tx.insert order0: Tx<_, unit>)
        let base' = ok (base' |> Result.mapError string)
        let! okUpdate = db.Repo.Transact(base' |> edit (fun o -> { o with Total = 3m }))
        Assert.Equal(2, (ok (okUpdate |> Result.mapError string)).Version)
        let! staleUpdate = db.Repo.Transact(base' |> edit (fun o -> { o with Total = 4m }))
        let! staleDelete = db.Repo.Transact(Tx.delete base': Tx<_, unit>)

        match staleUpdate, staleDelete with
        | Error { Failure = TxFailure.Conflict _ }, Error { Failure = TxFailure.Conflict _ } -> ()
        | a, b -> failwithf "expected stale errors, got %A / %A" a b

        let! total = db.Scalar<decimal>("select total from orders")
        Assert.Equal(3m, total)
    }

// repo.exs: test "unique constraint"
[<Fact>]
let ``ecto: unique constraint`` () =
    task {
        use! db = freshDefault ()
        let customer = New.customer "A" "dup@example.com"
        let! first = db.Repo.Transact(Tx.insert customer: Tx<_, unit>)
        ok (first |> Result.mapError string) |> ignore

        // A constraint with PostgreSQL's default name maps to its field, with
        // nothing declared (constraints.feature).
        let! duplicate = db.Repo.Transact(Tx.insert customer: Tx<_, unit>)

        match duplicate with
        | Error { Failure = TxFailure.InvalidChanges("Customer", errors) } ->
            Assert.Equal<FieldError list>(
                [ { Field = "Email"
                    Message = "has already been taken"
                    Kind = FieldErrorKind.UniqueConstraint "customers_email_key" } ],
                errors
            )
        | other -> failwithf "expected Invalid, got %A" other
    }

// repo.exs: test "foreign key constraint"
[<Fact>]
let ``ecto: foreign key constraint`` () =
    task {
        use! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let line = { New.line (ProductId 999L) 1 1m with OrderId = o }
        let! declared = db.Repo.Transact(Tx.insert line: Tx<_, unit>)

        match declared with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            Assert.Equal(FieldErrorKind.ForeignKeyConstraint "order_lines_product_id_fkey", errors.Head.Kind)
            Assert.Equal("ProductId", errors.Head.Field)
        | other -> failwithf "expected Invalid, got %A" other
    }

// changeset_test.exs: test "change/2 with a struct"
[<Fact>]
let ``ecto: change/2 with a struct`` () =
    let cs = order0 |> Changeset.edit (fun o -> { o with Total = 11m; Status = order0.Status })
    Assert.Equal<string list>([ "Total" ], cs.Changes |> List.map fst)
    Assert.Equal(Some order0, cs.Data)

// multi_test.exs: test "new" (empty multi) / repo transaction of an empty multi
[<Fact>]
let ``ecto: empty multi`` () =
    task {
        use! db = freshDefault ()
        let! r = db.Repo.Transact(transaction { return () })
        Assert.Equal(Ok(), r)
        Assert.Equal(0, db.Recorder.Count)
    }

// multi_test.exs: tests "insert changeset", "update changeset" and "insert fun"
[<Fact>]
let ``ecto: insert and update changesets with dependent steps`` () =
    task {
        use! db = freshDefault ()
        let! p = Seed.product db "A"

        let tx =
            transaction {
                let! o = Tx.insert order0
                let! l = Tx.insert { New.line p 1 1m with OrderId = o.Id }
                let! o2 = Tx.update (o, { o with Status = Published })
                return o2, l
            }

        let! r = db.Repo.Transact tx
        let o, l = ok (r |> Result.mapError string)
        Assert.Equal(o.Id, l.OrderId)
        Assert.Equal(Published, o.Status)
    }

// multi_test.exs: test "error" — failure identifies the operation; earlier work rolls back
[<Fact>]
let ``ecto: error`` () =
    task {
        use! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert order0
                let! (_: unit) = Tx.fail "nope"
                return ()
            }

        let! r = db.Repo.Transact tx

        match r with
        | Error e ->
            Assert.Equal("fail", e.Step.Action)
            match e.Failure with
            | TxFailure.Rejected r -> Assert.Equal("nope", r)
            | other -> failwithf "expected a rejection, got %A" other

            Assert.Equal<string list>([ "insert Order" ], e.RolledBack |> List.map (fun s -> s.Action))
        | Ok _ -> failwith "expected failure"

        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// multi_test.exs / repo transaction semantics: an invalid changeset fails its named operation
[<Fact>]
let ``ecto: multi with invalid changeset returns the failed operation and its errors`` () =
    task {
        use! db = freshDefault ()
        // orders_total_check refuses a negative total.
        let tx =
            transaction {
                let! _ = Tx.insert order0
                let! _ = Tx.insert { order0 with Total = -1m }
                return ()
            }

        let! r = db.Repo.Transact tx

        match r with
        | Error { Step = step; Failure = TxFailure.InvalidChanges("Order", errors) } when step.Action = "insert Order" && step.Line > 0 ->
            Assert.Equal("Total", errors.Head.Field)
        | other -> failwithf "unexpected %A" other

        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// multi_test.exs: test "run with fun"
[<Fact>]
let ``ecto: run with fun`` () =
    task {
        use! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert order0

                let! count =
                    Tx.run (fun ctx ->
                        task {
                            use cmd = ctx.CreateCommand()
                            cmd.CommandText <- "select count(*) from orders"
                            let! v = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                            return Ok(unbox<int64> v)
                        })

                return count
            }

        let! r = db.Repo.Transact tx
        Assert.Equal(Ok 1L, r |> Result.mapError string)
    }

// multi_test.exs: test "all queryable"
[<Fact>]
let ``ecto: all queryable`` () =
    task {
        use! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert order0
                let! seen = Tx.all (Query.from (tableOf<Order>))
                return seen.Length
            }

        let! r = db.Repo.Transact tx
        Assert.Equal(Ok 1, r |> Result.mapError string)
    }

// ===========================================================================
// Query semantics
// ===========================================================================

// Ecto raises on `== nil` comparisons and requires is_nil/1.
[<Fact>]
let ``ecto (deliberate difference): comparing with None means IS NULL`` () =
    task {
        use! db = freshDefault ()
        let! a = Seed.order db None "draft" 1m (Seed.at 1)
        let! b = Seed.order db None "draft" 2m (Seed.at 2)
        let! _ = db.Exec("update orders set cancellation_reason = 'late' where id = @i", [ "i", box (let (OrderId v) = b in v) ])
        let! r = db.Repo.All(plasm { for o in tableOf<Order> do where (o.CancellationReason = None) })
        Assert.Equal<OrderId list>([ a ], ok r |> List.map (fun o -> o.Id))
        let! r2 = db.Repo.All(plasm { for o in tableOf<Order> do where (o.CancellationReason <> Some "late") })
        Assert.Equal<OrderId list>([ a ], ok r2 |> List.map (fun o -> o.Id))
    }
