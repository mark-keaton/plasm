# Plasm in five minutes

Plasm is a stricter data layer for a C# application, over PostgreSQL or
SQLite. You write the domain in one small F# project. Everything else stays
C#. It stores the domain's own immutable records, reads them with
queries the compiler checks, and writes them in typed transactions. It
compiles ahead of time (native AOT), and every commit is checked natively.

Plasm is new: this is version 0.5, and its API may change before 1.0.
[Status](../status.md) says what is verified and what is missing.

A new application starts from the template: an F# data project on the
packages, a C# minimal API, and C# tests in a sandbox. The build writes the C#
side from the F# project, so each endpoint is one line. It needs the .NET 11
SDK, and its tests need Docker.

```sh
dotnet new install Plasm.Templates
dotnet new plasm-api -n MyApp
```

With `--database sqlite` the project is on SQLite, a file with no server,
and its tests need no Docker:

```sh
dotnet new plasm-api -n MyApp --database sqlite
```

The packages are on NuGet, for an application that exists already:

```sh
dotnet add package Plasm.Postgres
```

For SQLite the package is `Plasm.Sqlite`. The model, the queries and the
transactions are written the same way; [SQLite](../sqlite.md) says how to
connect and where SQLite differs from PostgreSQL. The pages here use
PostgreSQL.

The code on this page is from
[`examples/Plasm.Examples`](../../examples/Plasm.Examples), a runnable tour.

## A record says how it is stored

A stored record names its table. Its key is its field named `Id`, a column is
named after its field in snake_case, and the list of lines that hold the
order's key is owned: read with the order and written with it.

<!-- from: examples/Plasm.Examples/Model.fs -->
```fsharp
[<Table "orders">]
type Order =
    { Id: OrderId
      CustomerId: CustomerId option
      Status: OrderStatus
      Total: decimal
      PlacedAt: DateTime
      [<Version>]
      Version: int
      Lines: OrderLine list }
// ...
and [<Table "order_lines">] OrderLine =
    { Id: OrderLineId
      OrderId: OrderId
      ProductId: ProductId
      Quantity: Quantity
      UnitPrice: decimal }
```

## A query is a value

A `plasm { }` expression builds a query and runs nothing. Reusable pieces are
ordinary functions over queries.

<!-- from: examples/Plasm.Examples/Queries.fs -->
```fsharp
let published = plasm { for o in tableOf<Order> do where (o.Status = Published) }
// ...
let forCustomer customerId (q: Query<Order>) =
    plasm { for o in q do where (o.CustomerId = Some customerId) }
```

The repository runs it. Every `Order` read comes with its lines, in two
commands however many orders there are.

## A write is a transaction

Read what the decision needs, let the domain decide, write what it decided.
`Tx.oneAsSeen` reads the order as the caller saw it, and `Tx.update` writes
only the columns that differ, and the lines by difference.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let changeOrder (id: OrderId) (seen: int) (decide: Order -> Result<Order, OrderError>) : Tx<Order, Refusal> =
    transaction {
        let! order = Tx.oneAsSeen (orderById id, seen) |> Tx.orReject (Missing "order")
        let! changed = decide order |> Tx.ofResult Domain
        return! Tx.update (order, changed)
    }
```

## Run the tour

You need the .NET 11 SDK and Docker. The tour starts a throwaway PostgreSQL,
creates the tables from the DDL Plasm prints for the model, and prints each
command it sends.

```sh
dotnet run --project examples/Plasm.Examples
```

## Where next

- [The one way](one-way.md) to build an application with Plasm.
- [From C#](../csharp.md): what the build writes, and the one type a C# host calls.
- The koans teach all of this one blank at a time: `dotnet run --project koans/Plasm.Koans`.
