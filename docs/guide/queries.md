# Queries

A query is a value built with `plasm { }`. Building one runs nothing; the
repository runs it. A condition that cannot be translated to SQL fails with
the expression named, and rows are never fetched to be filtered in memory.
The examples are from
[`examples/Plasm.Examples/Queries.fs`](../../examples/Plasm.Examples/Queries.fs).

## Filters

Domain values become their stored representation as parameters.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let lookup (id: OrderId) =
    plasm { for o in tableOf<Order> do where (o.Id = id && o.Status = Draft) }
```

A query can start from another query, so reusable filters compose by
ordinary function application:

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let forCustomer customerId (q: Query<Order>) =
    plasm { for o in q do where (o.CustomerId = Some customerId) }
// ...
let overdue customerId = Query.from (tableOf<Order>) |> forCustomer customerId |> unpaid
```

## What a record owns, in a condition

An order's own lines are read as F# reads them. A member of the domain marked
`[<ReflectedDefinition>]`, here `IsOpen`, is written as SQL too.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let openWithProduct (product: ProductId) =
    plasm {
        for o in tableOf<Order> do
            where (o.IsOpen && (o.Lines |> List.exists (fun l -> l.ProductId = product)))
    }
```

## Paging

A page of orders, each with its lines, is two commands however long the page
is. The page of orders is selected first; the lines are read for those keys.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let orderPage page size (q: Query<Order>) =
    plasm {
        for o in q do
            orderByDescending o.PlacedAt // the key is appended as a tiebreaker
            skip (page * size)
            take size
    }
```

## Another aggregate

Payments are their own aggregate. As a condition, they are a correlated
`exists`, so an order never appears twice:

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let unpaid (q: Query<Order>) =
    plasm {
        for o in q do
            where (not (exists (plasm { for p in referring<Payment> o do where (p.Status = Succeeded) })))
    }
```

As data, `let!` binds what one more query finds for each record, read with
one more command for all of them, and `return` says what a row is.
`referring<Payment> o` is the payments that refer to `o`: `Payment`'s one
field of type `OrderId` is the tie, so it is not written by hand.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let withSucceededPayments (q: Query<Order>) =
    plasm {
        for o in q do

        let! payments =
            plasm {
                for p in referring<Payment> o do
                where (p.Status = Succeeded)
                orderBy p.CreatedAt
            }

        return {| Order = o; Payments = payments |}
    }
```

## Projections

Only the projected columns are read. A projection is not a record and cannot
be written back.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let totals =
    plasm {
        for o in tableOf<Order> do
            orderBy o.Id
            select {| Id = o.Id; Total = o.Total; Version = o.Version |}
    }
```

## Joins

A left join's other side is an option.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let customerEmails =
    plasm {
        for o in tableOf<Order> do
            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            orderBy o.Id
            select (o.Id, c |> Option.map (fun c -> c.Email))
    }
```

## Aggregates and grouping

The database adds up; nothing is fetched to be counted.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let revenueByCustomer =
    plasm {
        for o in tableOf<Order> do
            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            groupBy c.Name
            having (Agg.count () > 1L)
            orderByDescending (Agg.sum o.Total)
            select (c.Name, Agg.count (), Agg.sum o.Total)
    }
```

## Prepared queries

A prepared query is built and planned once; each call only binds its
arguments.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let orderById =
    Query.prepare (fun (id: Arg<OrderId>) -> plasm { for o in tableOf<Order> do where (o.Id = id.Value) })
```

## SQL inside a typed query

A fragment of literal SQL keeps its arguments as parameters.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let customersMatching (pattern: string) =
    plasm {
        for c in tableOf<Customer> do
            where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, pattern))
            orderBy c.Name
    }
```

## Running a query

`repo.All` reads every row, `repo.One` reads one, `repo.Count` counts, and
`repo.Explain` returns the SQL and the lists read with it without touching
the database.

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let page = Query.from (tableOf<Order>) |> forCustomer ada.Id |> orderPage 0 20
        let! orders = repo.All(withSucceededPayments page)
// ...
        let! stats = repo.One orderStats
// ...
        match repo.Explain page with
        | Ok plan -> show "plan (no database access)" (plan.LoadPaths, plan.Sql)
        | Error e -> show "plan error" e
```

The site's examples page shows the SQL Plasm generated for each of these,
captured from a real run.
