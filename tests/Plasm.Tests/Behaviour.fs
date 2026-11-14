/// Edge-path integration tests against real PostgreSQL.
module Plasm.Tests.Behaviour

open System
open System.Threading
open Xunit
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

let rawId (OrderId v) = box v

let seedOrderWithLines (db: TestDb) n =
    task {
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 10m (Seed.at 1)
        let! ls = [ for i in 1..n -> Seed.line db o p i 1m ] |> Threading.Tasks.Task.WhenAll
        let! r = db.Repo.All(plasm { for x in orders do preload x.Lines })
        return p, (ok r).Head, ls
    }

// -- Reads -------------------------------------------------------------------

[<Fact>]
let ``One returns None, Some, or an error for several rows`` () =
    task {
        let! db = freshDefault ()
        let! none = db.Repo.One(Query.from orders)
        Assert.Equal(None, ok none)
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! one = db.Repo.One(Query.from orders)
        Assert.Equal(Some o, ok one |> Option.map (fun x -> x.Id))
        let! _ = Seed.order db None "draft" 1m (Seed.at 2)
        let! many = db.Repo.One(Query.from orders)
        Assert.True(Result.isError many)
    }

[<Fact>]
let ``projections read only projected columns and rebuild tuples, anonymous records and computed values`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 10m (Seed.at 1)
        db.Recorder.Clear()
        let! a = db.Repo.All(plasm { for x in orders do select (x.Id, x.Total) })
        Assert.Equal<(OrderId * decimal) list>([ o, 10m ], ok a)
        let sql = db.Recorder.Events.Head.Sql
        notContains "placed_at" sql
        let! b = db.Repo.All(plasm { for x in orders do select {| Id = x.Id; Reason = x.CancellationReason; Double = x.Total * 2m; Tag = "k" |} })
        let row = (ok b).Head
        Assert.Equal(o, row.Id)
        Assert.Equal(None, row.Reason)
        Assert.Equal(20m, row.Double)
        Assert.Equal("k", row.Tag)
    }

[<Fact>]
let ``a persisted value rejected by its domain constructor is a materialization error`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 0 1m
        let! r = db.Repo.All(Query.from orderLines)

        match r with
        | Error(DbError.Materialization m) ->
            Assert.Equal("OrderLine", m.Model)
            Assert.Equal("Quantity", m.Property)
            Assert.Contains("positive", m.Message)
        | other -> failwithf "expected Materialization, got %A" other
    }

[<Fact>]
let ``a filtered load that hides a required related record is a materialization error`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 1 1m
        let none = plasm { for x in products do where (x.Sku = "nope") }
        let! r = db.Repo.All(plasm { for l in orderLines do preload l.Product none })

        match r with
        | Error(DbError.Materialization m) -> Assert.Equal("Product", m.Property)
        | other -> failwithf "expected Materialization, got %A" other
    }

[<Fact>]
let ``connection failures are classified as infrastructure failures`` () =
    task {
        let repo = Repo(Schemas.model, Postgres.connect "Host=127.0.0.1;Port=1;Timeout=2")
        let! r = repo.All(Query.from orders)

        match r with
        | Error(DbError.Database f) -> Assert.Equal(DbFailureKind.ConnectionFailure, f.Kind)
        | other -> failwithf "expected DbFailureKind.ConnectionFailure, got %A" other
    }

[<Fact>]
let ``snapshot reads do not see writes committed between commands`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let mutable injected = false

        let inject (e: CommandEvent) =
            if not injected && e.Sql.Contains "FROM \"orders\"" then
                injected <- true
                (Seed.payment db o 1m "pending" (Seed.at 2)).GetAwaiter().GetResult() |> ignore

        let repo = Repo(db.Model, db.Provider, { OnCommand = Some inject; IncludeParameterValues = false })
        let q = plasm { for x in orders do preload x.Payments }
        let! snap = repo.All(q, ReadConsistency.Snapshot)
        Assert.Equal(Loaded [], (ok snap).Head.Payments)
        injected <- false
        let! indep = repo.All(q, ReadConsistency.Independent)
        Assert.Equal(2, (Assoc.get (ok indep).Head.Payments).Length) // both injected payments
    }

// -- Writes ------------------------------------------------------------------

[<Fact>]
let ``forKey updates only the stated fields`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! r = db.Repo.Update(Changeset.forKey((fun (x: Product) -> x.Id), p) |> Changeset.set((fun x -> x.Name), "renamed"))
        let updated = ok (r |> Result.mapError string)
        Assert.Equal("renamed", updated.Name)
        Assert.Equal("A", updated.Sku)
        Assert.Contains("SET \"name\" = @p0 WHERE", db.Recorder.Writes.Head.Sql)
    }

[<Fact>]
let ``forKey on a versioned record requires an expected version`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let cs = Changeset.forKey((fun (x: Order) -> x.Id), o) |> Changeset.set((fun x -> x.Total), 2m)
        let! missing = db.Repo.Update cs

        match missing with
        | Error(WriteError.Invalid c) -> Assert.Equal("Version", c.Errors.Head.Field)
        | other -> failwithf "expected Invalid, got %A" other

        let! ok1 = db.Repo.Update(cs |> Changeset.expectVersion 1)
        Assert.Equal(2, (ok (ok1 |> Result.mapError string)).Version)
        let! stale = db.Repo.Update(cs |> Changeset.expectVersion 1)
        Assert.True(match stale with Error(WriteError.Stale _) -> true | _ -> false)
    }

[<Fact>]
let ``changing the primary key or the version is rejected before writing`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! all = db.Repo.All(Query.from orders)
        let order = (ok all).Head
        let! a = db.Repo.Update(order |> Changeset.edit (fun x -> { x with Id = OrderId 99L }))
        let! b = db.Repo.Update(order |> Changeset.edit (fun x -> { x with Version = 7 }))

        match a, b with
        | Error(WriteError.Invalid ca), Error(WriteError.Invalid cb) ->
            Assert.Equal(FieldErrorKind.KeyChange, ca.Errors.Head.Kind)
            Assert.Equal("Version", cb.Errors.Head.Field)
        | x -> failwithf "unexpected %A" x

        Assert.Empty(db.Recorder.Writes)
        ignore o
    }

[<Fact>]
let ``putChildren updates an existing child by identity without inserting a duplicate`` () =
    task {
        let! db = freshDefault ()
        let! _, order, _ = seedOrderWithLines db 2
        let first = (Assoc.get order.Lines).Head
        let cs = Changeset.update order |> Changeset.putChildren((fun o -> o.Lines), [ first |> Changeset.edit (fun l -> { l with UnitPrice = 9m }) ])
        let! r = db.Repo.Update cs
        ok (r |> Result.mapError string) |> ignore
        let! n = db.Scalar<int64>("select count(*) from order_lines")
        let! price = db.Scalar<decimal>("select unit_price from order_lines where id = @i", [ "i", box (let (OrderLineId v) = first.Id in v) ])
        Assert.Equal(2L, n)
        Assert.Equal(9m, price)
    }

[<Fact>]
let ``replacing children without a removal policy is rejected and nothing changes`` () =
    task {
        let! db = freshDefault ()
        let! p, order, _ = seedOrderWithLines db 2
        let keep = (Assoc.get order.Lines).Head

        let cs =
            Changeset.update order
            |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.allChildren, [ Changeset.update keep; Changeset.insert (New.line p 1 1m) ])

        let! r = db.Repo.Update cs

        match r with
        | Error(WriteError.Invalid c) -> Assert.Equal(FieldErrorKind.ReplacementRejected, c.Errors.Head.Kind)
        | other -> failwithf "expected Invalid, got %A" other

        let! n = db.Scalar<int64>("select count(*) from order_lines")
        Assert.Equal(2L, n)
        let! v = db.Scalar<int>("select version from orders")
        Assert.Equal(1, v)
    }

[<Fact>]
let ``replacing children with OnReplace.Delete deletes omitted children atomically`` () =
    task {
        let! db = fresh Schemas.modelDeletingLines
        let! p, order, _ = seedOrderWithLines db 3
        let keep = (Assoc.get order.Lines).Head

        let cs =
            Changeset.update order
            |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.allChildren, [ Changeset.update keep; Changeset.insert (New.line p 7 1m) ])

        let! r = db.Repo.Update cs
        let updated = ok (r |> Result.mapError string)
        let! qtys = db.Repo.All(plasm { for l in orderLines do orderBy l.Id })
        Assert.Equal<int list>([ 1; 7 ], ok qtys |> List.map (fun l -> l.Quantity.Value))
        Assert.Equal(2, (Assoc.get updated.Lines).Length)
    }

[<Fact>]
let ``a scoped replacement leaves children outside the scope untouched`` () =
    task {
        let! db = fresh Schemas.modelDeletingLines
        let! p, order, _ = seedOrderWithLines db 3 // quantities 1, 2, 3
        let big = plasm { for l in orderLines do where (l.UnitPrice >= 1m && l.Quantity = qty 3) }

        let cs =
            Changeset.update order
            |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.matching big, [])

        let! r = db.Repo.Update cs
        let updated = ok (r |> Result.mapError string)
        Assert.Equal(NotLoaded, updated.Lines) // a scoped write is not the complete relationship
        let! n = db.Scalar<int64>("select count(*) from order_lines")
        Assert.Equal(2L, n)
        ignore p
    }

[<Fact>]
let ``updating a child that belongs to another parent is rejected and rolled back`` () =
    task {
        let! db = freshDefault ()
        let! _, order, _ = seedOrderWithLines db 1
        let! other = Seed.order db None "draft" 1m (Seed.at 2)
        let! _ = db.Exec("update order_lines set order_id = @o", [ "o", rawId other ])
        let line = (Assoc.get order.Lines).Head

        let cs =
            order
            |> Changeset.edit (fun o -> { o with Total = 99m })
            |> Changeset.putChildren((fun o -> o.Lines), [ line |> Changeset.edit (fun l -> { l with UnitPrice = 5m }) ])

        let! r = db.Repo.Update cs

        match r with
        | Error(WriteError.Invalid c) -> Assert.Contains(c.Errors, fun e -> e.Kind = FieldErrorKind.NotOwned)
        | other -> failwithf "expected Invalid, got %A" other

        let! total = db.Scalar<decimal>("select total from orders where id = @i", [ "i", rawId order.Id ])
        Assert.Equal(10m, total)
    }

[<Fact>]
let ``a database check failing in a child write rolls back the parent`` () =
    task {
        let! db = freshDefault ()
        let! p, order, _ = seedOrderWithLines db 1
        // unit_price < 0 violates order_lines_unit_price_check in the database only.
        let cs =
            order
            |> Changeset.edit (fun o -> { o with Total = 99m })
            |> Changeset.putChildren((fun o -> o.Lines), [ Changeset.insert (New.line p 1 -1m) ])

        let! r = db.Repo.Update cs
        Assert.True(match r with Error(WriteError.Invalid cs) -> cs.Errors |> List.exists (fun e -> e.Kind = FieldErrorKind.CheckConstraint "order_lines_unit_price_check") | _ -> false)
        let! total = db.Scalar<decimal>("select total from orders")
        let! v = db.Scalar<int>("select version from orders")
        Assert.Equal(10m, total)
        Assert.Equal(1, v)
    }

[<Fact>]
let ``duplicate child identities are reported without writing`` () =
    task {
        let! db = freshDefault ()
        let! _, order, _ = seedOrderWithLines db 1
        let line = (Assoc.get order.Lines).Head

        let cs =
            Changeset.update order
            |> Changeset.putChildren(
                (fun o -> o.Lines),
                [ line |> Changeset.edit (fun l -> { l with UnitPrice = 2m }); line |> Changeset.edit (fun l -> { l with UnitPrice = 3m }) ]
            )

        db.Recorder.Clear()
        let! r = db.Repo.Update cs

        match r with
        | Error(WriteError.Invalid c) -> Assert.Contains(c.Errors, fun e -> e.Kind = FieldErrorKind.DuplicateIdentity && e.Field = "Lines")
        | other -> failwithf "expected Invalid, got %A" other

        Assert.Equal(0, db.Recorder.Count)
    }

[<Fact>]
let ``inserting a child through an optional foreign key sets Some parent key`` () =
    task {
        let! db = freshDefault ()
        let cs = Changeset.insert (New.customer "Ada" "a@x") |> Changeset.putChildren((fun c -> c.Orders), [ Changeset.insert (New.order None Draft 1m) ])
        let! r = db.Repo.Insert cs
        let c = ok (r |> Result.mapError string)
        Assert.Equal(Some c.Id, (Assoc.get c.Orders).Head.CustomerId)
    }

// -- Transactions ------------------------------------------------------------

[<Fact>]
let ``Tx.traverse reports the failing step and rolls back the earlier ones`` () =
    task {
        let! db = freshDefault ()
        let totals = [ 1m; -1m; 3m ]
        let tx = totals |> Tx.traverse (fun t -> Tx.insert (Changeset.insert (New.order None Draft t)))
        let! r = db.Repo.Transact tx
        Assert.True(match r with Error e -> e.Step.Action = "insert Order" && e.RolledBack.Length = 1 | Ok _ -> false)
    }

[<Fact>]
let ``cancellation propagates to a blocked database command`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! all = db.Repo.All(Query.from orders)
        use locker = new Npgsql.NpgsqlConnection(db.ConnectionString)
        do! locker.OpenAsync()
        use tx = locker.BeginTransaction()
        use lockCmd = locker.CreateCommand()
        lockCmd.Transaction <- tx
        lockCmd.CommandText <- "select 1 from orders for update"
        let! _ = lockCmd.ExecuteScalarAsync()
        use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.)
        let sw = Diagnostics.Stopwatch.StartNew()

        let! thrown =
            task {
                try
                    let! _ = db.Repo.Update((ok all).Head |> Changeset.edit (fun x -> { x with Total = 5m }), cts.Token)
                    return false
                with :? OperationCanceledException ->
                    return true
            }

        Assert.True(thrown)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds 5.)
        ignore o
    }
