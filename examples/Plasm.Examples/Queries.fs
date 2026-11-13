/// 2. Queries are values; reusable pieces are ordinary functions.
module Shop.Queries

open Plasm
open Shop.Model

let published = plasm { for o in Db.orders do where (o.Status = Published) }

let forCustomer customerId (q: Query<Order>) =
    plasm { for o in q do where (o.CustomerId = Some customerId) }

/// A relationship predicate: EXISTS in SQL, so parents never duplicate.
let unpaid (q: Query<Order>) =
    plasm { for o in q do where (not (o.Payments |> Rel.any (fun p -> p.Status = Succeeded))) }

/// Child queries are reusable too: filtered, ordered, with nested loads.
let successfulPayments =
    plasm {
        for p in Db.payments do
            where (p.Status = Succeeded)
            orderBy p.CreatedAt
    }

let linesWithProducts = plasm { for l in Db.orderLines do preload l.Product }

let latestPayments n =
    plasm {
        for p in Db.payments do
            orderByDescending p.CreatedAt
            limitPerParent n // per order, never a global limit
    }

/// The milestone: a typed, deterministically ordered page of orders with
/// nested, batched relationship loads.
let orderPage page size (q: Query<Order>) =
    plasm {
        for o in q do
            orderByDescending o.PlacedAt // the primary key is appended as a tiebreaker
            skip (page * size)
            take size
            preload o.Lines linesWithProducts
            preload o.Payments successfulPayments
    }

/// Only the projected columns are read.
let totals =
    plasm {
        for o in Db.orders do
            orderBy o.Id
            select {| Id = o.Id; Total = o.Total; Version = o.Version |}
    }

/// Domain scalars become their stored representation as parameters.
let lookup (id: OrderId) =
    plasm {
        for o in Db.orders do
            where (o.Id = id && o.Status = Draft)
    }

/// Reusable filters compose by ordinary function application.
let overdue customerId = Query.from Db.orders |> forCustomer customerId |> unpaid

/// A per-parent limit inside a load: at most n payments for each order.
let recentPayments n =
    plasm { for o in Db.orders do preload o.Payments (latestPayments n) }

/// A load specification to apply to values already in hand.
let withPayments = plasm { for o in Db.orders do preload o.Payments }

/// Inner join: one row per order that has a customer.
let ordersWithCustomers =
    plasm {
        for o in Db.orders do
            join c in Db.customers on (o.CustomerId = Some c.Id)
            orderBy o.Id
    }

/// Left join: every order, with its customer's email when there is one.
let customerEmails =
    plasm {
        for o in Db.orders do
            leftJoin c in Db.customers on (o.CustomerId = Some c.Id)
            orderBy o.Id
            select (o.Id, c |> Option.map (fun c -> c.Email))
    }

/// Joins chain into flat tuples and project to exactly what is needed.
let soldProducts =
    plasm {
        for o in Db.orders do
            join l in Db.orderLines on (o.Id = l.OrderId)
            join p in Db.products on (l.ProductId = p.Id)
            where (o.Status <> Cancelled)
            orderBy p.Sku
            select (o.Id, p.Sku, l.Quantity)
    }

/// The EF Core comparison query, written clause for clause like the C# version
/// (benchmarks/Plasm.Benchmarks.EfModel/Model.cs).
let efComparisonPage =
    plasm {
        for o in Db.orders do
            where (o.Status = Published)
            orderByDescending o.PlacedAt
            thenBy o.Id
            take 20
            preload o.Lines (plasm { for l in Db.orderLines do preload l.Product })
            preload o.Payments (plasm {
                for p in Db.payments do
                    where (p.Status = Succeeded)
                    orderBy p.CreatedAt
            })
    }

// -- Counts, aggregates and grouping ------------------------------------------

/// One row of statistics: the database adds up, nothing is fetched to count.
let orderStats =
    plasm {
        for o in Db.orders do
            where (o.Status <> Cancelled)
            select (Agg.count (), Agg.sum o.Total, Agg.avg o.Total, Agg.max o.PlacedAt)
    }

/// Customers with more than one order, biggest spenders first.
let revenueByCustomer =
    plasm {
        for o in Db.orders do
            join c in Db.customers on (o.CustomerId = Some c.Id)
            groupBy c.Name
            having (Agg.count () > 1L)
            orderByDescending (Agg.sum o.Total)
            select (c.Name, Agg.count (), Agg.sum o.Total)
    }

// -- Prepared queries ----------------------------------------------------------

/// Built and planned once; each call only binds the customer and page size.
let latestOrdersOf =
    Query.prepare2 (fun (customer: Arg<CustomerId>) (size: Arg<int>) ->
        plasm {
            for o in Db.orders do
                where (o.CustomerId = Some customer.Value)
                orderByDescending o.PlacedAt
                take size.Value
        })

// -- SQL fragments ---------------------------------------------------------------

/// Literal SQL inside a typed query: the pattern is still a parameter.
let customersMatching (pattern: string) =
    plasm {
        for c in Db.customers do
            where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, pattern))
            orderBy c.Name
    }

// -- Many-to-many ------------------------------------------------------------------

let articlesWithTags =
    plasm {
        for a in Db.articles do
            orderBy a.Id
            preload a.Tags (plasm { for t in Db.tags do orderBy t.Name })
    }

let taggedWith (name: string) =
    plasm {
        for a in Db.articles do
            where (Rel.any (fun t -> t.Name = name) a.Tags)
    }
