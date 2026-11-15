/// Shared dataset and the equivalent Plasm / EF Core workloads.
module Plasm.Benchmarks.Workloads

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
type EfOrder = Plasm.Benchmarks.EfModel.Order
type EfContext = Plasm.Benchmarks.EfModel.ShopContext
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
//
// An Order owns its lines, so every query of Orders brings them, in one more
// command per batch of orders. Payments are another aggregate, referred to by
// id: they are read by a second query, for the orders the first one found.

/// Published orders, newest first: the lines come with them.
let plasmPage (n: int) =
    plasm {
        for o in Schemas.orders do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            thenBy o.Id
            take n
    }

/// The succeeded payments of the given orders, built and planned once.
let succeededPaymentsOf =
    Query.prepare (fun (orders: Arg<OrderId list>) ->
        plasm {
            for p in Schemas.payments do
                where (List.contains p.OrderId orders.Value && p.Status = Succeeded)
                orderBy p.CreatedAt
        })

let siblingOrderId = 1L

/// One order and all its payments, the queries built for each call.
let plasmSiblingQueries (id: OrderId) =
    plasm { for o in Schemas.orders do where (o.Id = id) },
    plasm {
        for p in Schemas.payments do
            where (p.OrderId = id)
            orderBy p.Id
    }

/// The same two queries, prepared once: each call only binds the key.
let orderById =
    Query.prepare (fun (id: Arg<OrderId>) -> plasm { for o in Schemas.orders do where (o.Id = id.Value) })

let paymentsOfOrder =
    Query.prepare (fun (id: Arg<OrderId>) ->
        plasm {
            for p in Schemas.payments do
                where (p.OrderId = id.Value)
                orderBy p.Id
        })

let private all (repo: Repo) (q: Query<'T>) =
    task {
        match! repo.All q with
        | Ok rows -> return rows
        | Error e -> return failwithf "Plasm query failed: %O" e
    }

/// A page of orders with their lines, then the succeeded payments of those orders.
let runPage (repo: Repo) (page: Query<Order>) : Task<Order list * Payment list> =
    task {
        let! orders = all repo page
        let! payments = all repo (succeededPaymentsOf (orders |> List.map (fun o -> o.Id)))
        return orders, payments
    }

let runSiblings (repo: Repo) (id: OrderId) : Task<Order list * Payment list> =
    task {
        let orderQuery, paymentQuery = plasmSiblingQueries id
        let! orders = all repo orderQuery
        let! payments = all repo paymentQuery
        return orders, payments
    }

let runSiblingsPrepared (repo: Repo) (id: OrderId) : Task<Order list * Payment list> =
    task {
        let! orders = all repo (orderById id)
        let! payments = all repo (paymentsOfOrder id)
        return orders, payments
    }

/// (orders, lines, payments): every variant must return the same graph.
type Shape = { Orders: int; Lines: int; Payments: int }

let plasmShape (orders: Order list, payments: Payment list) =
    { Orders = orders.Length
      Lines = orders |> List.sumBy (fun o -> o.Lines.Length)
      Payments = payments.Length }

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
    { Orders = orders.Count
      Lines = orders |> Seq.sumBy (fun o -> o.Lines.Count)
      Payments = orders |> Seq.sumBy (fun o -> o.Payments.Count) }
