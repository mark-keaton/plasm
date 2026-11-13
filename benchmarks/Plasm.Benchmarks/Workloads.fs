/// Shared dataset and the equivalent Plasm / EF Core workloads.
module Plasm.Benchmarks.Workloads

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
type EfOrder = Plasm.Benchmarks.EfModel.Order
type EfContext = Plasm.Benchmarks.EfModel.ShopContext
type EfCapture = Plasm.Benchmarks.EfModel.CaptureInterceptor
type EfQueries = Plasm.Benchmarks.EfModel.Queries

let orderCount = 10_000
let linesPerOrder = 10
let paymentsPerOrder = 10

let seedSql =
    $"""
insert into customers(name, email) select 'c' || g, 'c' || g || '@example.com' from generate_series(1, 100) g;
insert into products(sku, name) select 'sku' || g, 'product ' || g from generate_series(1, 200) g;
insert into orders(customer_id, status, total, placed_at)
  select (g %% 100) + 1, case when g %% 3 = 0 then 'draft' else 'published' end, 100.00,
         timestamptz '2026-01-01 00:00:00+00' + g * interval '1 minute'
  from generate_series(1, {orderCount}) g;
insert into order_lines(order_id, product_id, quantity, unit_price)
  select o.id, ((o.id * 7 + i) %% 200) + 1, i, 2.50 from orders o, generate_series(1, {linesPerOrder}) i;
insert into payments(order_id, amount, status, created_at)
  select o.id, 10.00, case i %% 3 when 0 then 'succeeded' when 1 then 'failed' else 'pending' end,
         o.placed_at + i * interval '1 second'
  from orders o, generate_series(1, {paymentsPerOrder}) i;
analyze;
"""

/// A seeded database, created once per process.
let database: Lazy<Task<TestDb>> =
    lazy
        (task {
            let! db = freshDefault ()
            let! _ = db.Exec seedSql
            return db
        })

// -- Plasm workloads -----------------------------------------------------------

let successfulPayments =
    plasm {
        for p in Schemas.payments do
            where (p.Status = Succeeded)
            orderBy p.CreatedAt
    }

let linesWithProduct = plasm { for l in Schemas.orderLines do preload l.Product }

let plasmPage (n: int) =
    plasm {
        for o in Schemas.orders do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            thenBy o.Id
            take n
            preload o.Lines linesWithProduct
            preload o.Payments successfulPayments
    }

let plasmSiblings (id: OrderId) =
    plasm {
        for o in Schemas.orders do
            where (o.Id = id)
            preload o.Lines
            preload o.Payments
    }

let siblingOrderId = 1L

/// The same query, prepared once: each call only binds the key.
let plasmSiblingsPrepared =
    Query.prepare (fun (id: Arg<OrderId>) ->
        plasm {
            for o in Schemas.orders do
                where (o.Id = id.Value)
                preload o.Lines
                preload o.Payments
        })

let run (repo: Repo) (q: Query<Order>) =
    task {
        match! repo.All q with
        | Ok orders -> return orders
        | Error e -> return failwithf "Plasm query failed: %O" e
    }

/// (orders, lines, payments, distinct Product instances) – used to check that
/// every variant returns the same graph.
type Shape =
    { Orders: int
      Lines: int
      Payments: int
      ProductInstances: int }

let plasmShape (orders: Order list) =
    let products = HashSet<obj>(HashIdentity.Reference)
    let mutable lines = 0
    let mutable payments = 0

    for o in orders do
        for l in Assoc.defaultValue [] o.Lines do
            lines <- lines + 1

            match l.Product with
            | Loaded p -> products.Add(box p) |> ignore
            | NotLoaded -> ()

        payments <- payments + (Assoc.defaultValue [] o.Payments).Length

    { Orders = orders.Length
      Lines = lines
      Payments = payments
      ProductInstances = products.Count }

// -- EF Core workloads ---------------------------------------------------------

let efPage (cs: string) (capture: Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor) (n: int) (split: bool) =
    task {
        use ctx = new EfContext(cs, capture)
        return! (EfQueries.Page(ctx, n, split)).ToListAsync()
    }

let efSiblings (cs: string) (capture: Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor) (split: bool) =
    task {
        use ctx = new EfContext(cs, capture)
        return! (EfQueries.Siblings(ctx, siblingOrderId, split)).ToListAsync()
    }

let efShape (orders: List<EfOrder>) =
    let products = HashSet<obj>(HashIdentity.Reference)
    let mutable lines = 0
    let mutable payments = 0

    for o in orders do
        for l in o.Lines do
            lines <- lines + 1

            if not (isNull l.Product) then
                products.Add(box l.Product) |> ignore

        payments <- payments + o.Payments.Count

    { Orders = orders.Count
      Lines = lines
      Payments = payments
      ProductInstances = products.Count }
