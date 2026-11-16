/// 2. Queries are values; reusable pieces are ordinary functions. Every query
///    of a record brings what it owns: an Order comes with its lines.
module Shop.Queries

open Plasm
open Shop.Model

let published = plasm { for o in tableOf<Order> do where (o.Status = Published) }

/// Domain scalars become their stored representation as parameters.
let lookup (id: OrderId) =
    plasm { for o in tableOf<Order> do where (o.Id = id && o.Status = Draft) }

let forCustomer customerId (q: Query<Order>) =
    plasm { for o in q do where (o.CustomerId = Some customerId) }

/// Another aggregate as a condition: a correlated NOT EXISTS, so an order
/// never appears twice.
let unpaid (q: Query<Order>) =
    plasm {
        for o in q do
            where (not (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })))
    }

/// The domain's own member, and the order's own lines, as F# reads them.
let openWithProduct (product: ProductId) =
    plasm {
        for o in tableOf<Order> do
            where (o.IsOpen && (o.Lines |> List.exists (fun l -> l.ProductId = product)))
    }

/// Reusable filters compose by ordinary function application.
let overdue customerId = Query.from (tableOf<Order>) |> forCustomer customerId |> unpaid

/// A page of orders, each with its lines: two commands however long the page.
let orderPage page size (q: Query<Order>) =
    plasm {
        for o in q do
            orderByDescending o.PlacedAt // the key is appended as a tiebreaker
            skip (page * size)
            take size
    }

/// Payments are their own aggregate: each order of a query with its succeeded
/// payments, as (Order * Payment list), read with one more command for all of
/// them.
let withSucceededPayments (q: Query<Order>) =
    plasm {
        for o in q do
            including (
                plasm {
                    for p in tableOf<Payment> do
                        where (p.OrderId = o.Id && p.Status = Succeeded)
                        orderBy p.CreatedAt
                }
            )
    }

/// Only the projected columns are read.
let totals =
    plasm {
        for o in tableOf<Order> do
            orderBy o.Id
            select {| Id = o.Id; Total = o.Total; Version = o.Version |}
    }

// -- Joins ------------------------------------------------------------------------

/// Inner join: one row per order that has a customer.
let ordersWithCustomers =
    plasm {
        for o in tableOf<Order> do
            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            orderBy o.Id
    }

/// Left join: every order, with its customer's email when there is one.
let customerEmails =
    plasm {
        for o in tableOf<Order> do
            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            orderBy o.Id
            select (o.Id, c |> Option.map (fun c -> c.Email))
    }

/// Joins chain into flat tuples and project to exactly what is needed.
let soldProducts =
    plasm {
        for o in tableOf<Order> do
            join l in tableOf<OrderLine> on (o.Id = l.OrderId)
            join p in tableOf<Product> on (l.ProductId = p.Id)
            where o.IsOpen
            orderBy p.Sku
            select (o.Id, p.Sku, l.Quantity)
    }

// -- Counts, aggregates and grouping ------------------------------------------

/// One row of statistics: the database adds up, nothing is fetched to count.
let orderStats =
    plasm {
        for o in tableOf<Order> do
            where o.IsOpen
            select (Agg.count (), Agg.sum o.Total, Agg.avg o.Total, Agg.max o.PlacedAt)
    }

/// Customers with more than one order, biggest spenders first.
let revenueByCustomer =
    plasm {
        for o in tableOf<Order> do
            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
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
            for o in tableOf<Order> do
                where (o.CustomerId = Some customer.Value)
                orderByDescending o.PlacedAt
                take size.Value
        })

let orderById =
    Query.prepare (fun (id: Arg<OrderId>) -> plasm { for o in tableOf<Order> do where (o.Id = id.Value) })

let customerNamed =
    Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value) })

// -- SQL fragments ---------------------------------------------------------------

/// Literal SQL inside a typed query: the pattern is still a parameter.
let customersMatching (pattern: string) =
    plasm {
        for c in tableOf<Customer> do
            where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, pattern))
            orderBy c.Name
    }

// -- Linked records ------------------------------------------------------------------

/// Articles come with their tags, in the order their field names.
let articles = plasm { for a in tableOf<Article> do orderBy a.Id }

let taggedWith =
    Query.prepare (fun (name: Arg<string>) ->
        plasm {
            for a in tableOf<Article> do
                where (a.Tags |> List.exists (fun t -> t.Name = name.Value))
        })

let tagsNamed =
    Query.prepare (fun (names: Arg<string list>) ->
        plasm { for t in tableOf<Tag> do where (List.contains t.Name names.Value) })

// -- Compared with EF Core --------------------------------------------------------

/// The page the EF Core comparison reads, clause for clause like the C# version
/// (benchmarks/Plasm.Benchmarks.EfModel/Model.cs); the lines come with each
/// order, and the succeeded payments come with `including`.
let efComparisonPage =
    Query.prepare (fun (size: Arg<int>) ->
        plasm {
            for o in tableOf<Order> do
                where (o.Status = Published)
                orderByDescending o.PlacedAt
                thenBy o.Id
                take size.Value

                including (
                    plasm {
                        for p in tableOf<Payment> do
                            where (p.OrderId = o.Id && p.Status = Succeeded)
                            orderBy p.CreatedAt
                    }
                )
        })
