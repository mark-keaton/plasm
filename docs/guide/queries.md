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

Inside an aggregate, a condition counts or adds only the rows meeting it,
and a list is in the order the call states.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let customerSummary =
    plasm {
        for o in tableOf<Order> do
            groupBy o.CustomerId
            orderBy o.CustomerId
            select (o.CustomerId, Agg.countWhere (o.Status = Draft), Agg.sumWhere (o.Status = Published) o.Total, Agg.listBy o.Id o.PlacedAt)
    }
```

## Windows

A window function reads a value over the rows around each one: its number,
its rank, the total so far. The window is a partition and an order.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let orderHistory =
    plasm {
        for o in tableOf<Order> do
            orderBy o.CustomerId
            thenBy o.PlacedAt
            select (
                o.Id,
                Win.rowNumber (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt),
                Win.runningSum o.Total (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt)
            )
    }
```

## Combining queries

`Query.union`, `unionAll`, `intersect` and `except` combine two queries. Of
records, the result is a query like any other, ordered and paged after it is
formed.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let needsAttention customerId =
    Query.union (plasm { for o in tableOf<Order> do where (o.Status = Draft) }, unpaid (Query.from (tableOf<Order>)))
    |> forCustomer customerId
```

A join takes a query where it takes a table: `join c in (plasm { ... }) on (...)`.

## A tree, read in one statement

A record that refers to its own kind is a tree: a category with a parent
category.

<!-- from: tests/Plasm.Aot/App.fs -->
```fsharp
type CategoryId = CategoryId of int64

[<Table "categories">]
type Category =
    { Id: CategoryId
      Name: string
      Parent: CategoryId option }
```

`Query.recursive` takes a start and a step. The start is the rows to begin
from, here the categories with no parent. The step joins a table to what has
been found so far, here each category whose parent is among them.

<!-- from: tests/Plasm.Aot/Program.fs -->
```fsharp
            let under =
                Query.recursive (
                    plasm { for c in tableOf<Category> do where (c.Parent = None) },
                    fun found ->
                        plasm {
                            for c in tableOf<Category> do
                            join p in found on (c.Parent = Some p.Id)
                            select c
                        }
                )

            selected (plasm { for c in under do orderBy (Query.depth c); thenBy c.Name; select (c.Name, Query.depth c) }) (fun (name, depth) -> name + " " + string depth))
```

- It is one statement: `WITH RECURSIVE ... UNION ALL`, with `CYCLE` on the
  record's key. A cycle in the data ends.
- The query made from it filters, orders and pages the whole result, never
  a single step.
- `Query.depth c` is 0 for the start's rows.
- Everything above a row is the same form with the join turned round:
  `join c in found on (Some p.Id = c.Parent)`, selecting `p`.
- Each path to a row is a row. In a tree there is one.
- The start and the step neither order nor page. The step joins `found`
  exactly once, and does not group, take distinct rows or lock. A recursive
  query cannot be locked. Each refusal says what to do instead.
- It needs PostgreSQL 14 or later, because of `CYCLE`. On 13 the query
  fails with the database's syntax error (`DbError.Database`, 42601).

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

## Reading rows as a stream

`repo.Stream` gives a query's rows as they are read, for an export or a scan
that should not hold the whole result in memory.

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let stream = repo.Stream(plasm { for p in tableOf<Product> do orderBy p.Sku; select p.Sku })
        let e = stream.GetAsyncEnumerator()
        let skus = ResizeArray<string>()

        while! e.MoveNextAsync() do
            match e.Current with
            | Ok sku -> skus.Add sku
            | Error failure -> printfn "the stream ended with %O" failure

        do! e.DisposeAsync()
```

The loop is inside a `task`, and `while!` needs F# 8. This stream is of a
projection; `repo.Stream` takes a query of records too.

- Nothing is sent until the first row is asked for. From then the stream
  holds one connection.
- It gives the connection back when the rows end, when it is disposed
  early, and when its token is cancelled. Cancellation raises
  `OperationCanceledException`, as every read does.
- Each element is a `Result<'T, DbError>`. A failure is the last element: a
  database failure part-way through, or a value that cannot be read back.
  Nothing is thrown for either.
- A query that cannot be translated is a single `Error`, and opens no
  connection.
- A stream is one command, so a query of records that own or link a list is
  refused, naming the list. Stream a projection of the fields you need, or
  read the records a page at a time with `repo.All`.
- A stream reads outside a transaction. In a sandbox it reads the sandbox's
  rows.
- `OnCommand` and the activity report it once, when it ends, with the rows
  read.
- From C# it is an `IAsyncEnumerable` of `FSharpResult<T, DbError>`, read
  with `await foreach`. Behind a facade, map each to the facade's own type.
