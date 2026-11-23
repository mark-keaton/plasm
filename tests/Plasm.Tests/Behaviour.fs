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
        let! r = db.Repo.All(Query.from (tableOf<Order>))
        return p, (ok r).Head, ls
    }

// -- Reads -------------------------------------------------------------------

[<Fact>]
let ``One returns None, Some, or an error for several rows`` () =
    task {
        use! db = freshDefault ()
        let! none = db.Repo.One(Query.from (tableOf<Order>))
        Assert.Equal(None, ok none)
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! one = db.Repo.One(Query.from (tableOf<Order>))
        Assert.Equal(Some o, ok one |> Option.map (fun x -> x.Id))
        let! _ = Seed.order db None "draft" 1m (Seed.at 2)
        let! many = db.Repo.One(Query.from (tableOf<Order>))
        Assert.True(Result.isError many)
    }

[<Fact>]
let ``projections read only projected columns and rebuild tuples, anonymous records and computed values`` () =
    task {
        use! db = freshDefault ()
        let! o = Seed.order db None "draft" 10m (Seed.at 1)
        db.Recorder.Clear()
        let! a = db.Repo.All(plasm { for x in tableOf<Order> do select (x.Id, x.Total) })
        Assert.Equal<(OrderId * decimal) list>([ o, 10m ], ok a)
        let sql = db.Recorder.Events.Head.Sql
        notContains "placed_at" sql
        let! b = db.Repo.All(plasm { for x in tableOf<Order> do select {| Id = x.Id; Reason = x.CancellationReason; Double = x.Total * 2m; Tag = "k" |} })
        let row = (ok b).Head
        Assert.Equal(o, row.Id)
        Assert.Equal(None, row.Reason)
        Assert.Equal(20m, row.Double)
        Assert.Equal("k", row.Tag)
    }

[<Fact>]
let ``a persisted value rejected by its domain constructor is a materialization error`` () =
    task {
        use! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 0 1m
        let! r = db.Repo.All(Query.from (tableOf<OrderLine>))

        match r with
        | Error(DbError.Materialization m) ->
            Assert.Equal("OrderLine", m.Record)
            Assert.Equal("Quantity", m.Property)
            Assert.Contains("positive", m.Message)
        | other -> failwithf "expected Materialization, got %A" other
    }

[<Fact>]
let ``connection failures are classified as infrastructure failures`` () =
    task {
        let repo = Repo(Schemas.model, Postgres.connect "Host=127.0.0.1;Port=1;Timeout=2")
        let! r = repo.All(Query.from (tableOf<Order>))

        match r with
        | Error(DbError.Database f) -> Assert.Equal(DbFailureKind.ConnectionFailure, f.Kind)
        | other -> failwithf "expected DbFailureKind.ConnectionFailure, got %A" other
    }

[<Fact>]
let ``snapshot reads do not see writes committed between commands`` () =
    task {
        use! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! p = Seed.product db "A"
        let mutable injected = false

        // A line is committed between the Orders' command and the load of the
        // lines they own.
        let inject (e: CommandEvent) =
            if not injected && e.Sql.Contains "FROM \"orders\"" then
                injected <- true
                (Seed.line db o p 1 1m).GetAwaiter().GetResult() |> ignore

        let repo = Repo(db.Model, db.Provider, { RepoOptions.Default with OnCommand = Some inject })
        let q = Query.from (tableOf<Order>)
        let! snap = repo.All(q, ReadConsistency.Snapshot)
        Assert.Equal<OrderLine list>([], (ok snap).Head.Lines)
        injected <- false
        let! indep = repo.All(q, ReadConsistency.Independent)
        Assert.Equal(2, (ok indep).Head.Lines.Length) // both injected lines
    }

// -- Writes ------------------------------------------------------------------

[<Fact>]
let ``changing the primary key or the version is rejected before writing`` () =
    task {
        use! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! all = db.Repo.All(Query.from (tableOf<Order>))
        let order = (ok all).Head
        let! a = db.Repo.Transact(order |> edit (fun x -> { x with Id = OrderId 99L }))
        let! b = db.Repo.Transact(order |> edit (fun x -> { x with Version = 7 }))

        match a, b with
        | Error { Failure = TxFailure.InvalidChanges(_, ea) }, Error { Failure = TxFailure.InvalidChanges(_, eb) } ->
            Assert.Equal(FieldErrorKind.KeyChange, ea.Head.Kind)
            Assert.Equal("Version", eb.Head.Field)
        | x -> failwithf "unexpected %A" x

        Assert.Empty(db.Recorder.Writes)
        ignore o
    }

[<Fact>]
let ``updating a child that belongs to another parent is rejected and rolled back`` () =
    task {
        use! db = freshDefault ()
        let! _, order, _ = seedOrderWithLines db 1
        let! other = Seed.order db None "draft" 1m (Seed.at 2)
        let! _ = db.Exec("update order_lines set order_id = @o", [ "o", rawId other ])
        let line = order.Lines.Head
        let changed = { order with Total = 99m; Lines = [ { line with UnitPrice = 5m } ] }
        let! r = db.Repo.Transact(Tx.update (order, changed): Tx<Order, unit>)

        match r with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> Assert.Contains(errors, fun e -> e.Kind = FieldErrorKind.NotOwned)
        | other -> failwithf "expected invalid changes, got %A" other

        let! total = db.Scalar<decimal>("select total from orders where id = @i", [ "i", rawId order.Id ])
        Assert.Equal(10m, total)
    }

[<Fact>]
let ``a database check failing in a child write rolls back the parent`` () =
    task {
        use! db = freshDefault ()
        let! p, order, _ = seedOrderWithLines db 1
        // unit_price < 0 violates order_lines_unit_price_check in the database only.
        let changed = { order with Total = 99m; Lines = order.Lines @ [ New.line p 1 -1m ] }
        let! r = db.Repo.Transact(Tx.update (order, changed): Tx<Order, unit>)
        Assert.True(match r with Error { Failure = TxFailure.InvalidChanges(_, errors) } -> errors |> List.exists (fun e -> e.Kind = FieldErrorKind.CheckConstraint "order_lines_unit_price_check") | _ -> false)
        let! total = db.Scalar<decimal>("select total from orders")
        let! v = db.Scalar<int>("select version from orders")
        Assert.Equal(10m, total)
        Assert.Equal(1, v)
    }

[<Fact>]
let ``an owned item with a key its owner did not have is refused`` () =
    task {
        use! db = freshDefault ()
        let! _, order, _ = seedOrderWithLines db 1
        let! p = Seed.product db "B"
        let! otherId = Seed.order db None "draft" 5m (Seed.at 2)
        let! _ = Seed.line db otherId p 4 1m
        let! all = db.Repo.All(Query.from (tableOf<Order>))
        let other = ok all |> List.find (fun o -> o.Id = otherId)
        // A line of another Order, copied into this one: not this Order's to write.
        let stolen = List.exactlyOne other.Lines
        let! r = db.Repo.Transact(Tx.update (order, { order with Lines = order.Lines @ [ stolen ] }): Tx<Order, unit>)

        match r with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> Assert.Contains(errors, fun e -> e.Kind = FieldErrorKind.NotOwned)
        | other -> failwithf "expected invalid changes, got %A" other

        let! n = db.Scalar<int64>("select count(*) from order_lines")
        Assert.Equal(2L, n)
        ignore p
    }

// -- Transactions ------------------------------------------------------------

[<Fact>]
let ``Tx.traverse reports the failing step and rolls back the earlier ones`` () =
    task {
        use! db = freshDefault ()
        let totals = [ 1m; -1m; 3m ]
        let tx = totals |> Tx.traverse (fun t -> Tx.insert (New.order None Draft t))
        let! r = db.Repo.Transact tx
        Assert.True(match r with Error e -> e.Step.Action = "insert Order" && e.RolledBack.Length = 1 | Ok _ -> false)
    }

[<Fact>]
let ``cancellation propagates to a blocked database command`` () =
    task {
        use! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! all = db.Repo.All(Query.from (tableOf<Order>))
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
                    let! _ = db.Repo.Transact((ok all).Head |> edit (fun x -> { x with Total = 5m }), cancellationToken = cts.Token)
                    return false
                with :? OperationCanceledException ->
                    return true
            }

        Assert.True(thrown)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds 5.)
        ignore o
    }
