# Transactions

Every write is a step of a typed transaction, `Tx<'T, 'E>`, where `'E` is
your own error type. A transaction is a value; `repo.Transact` runs it. The
examples are from
[`examples/Plasm.Examples/Writes.fs`](../../examples/Plasm.Examples/Writes.fs).

## Your own refusals

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
/// Why a transaction stopped without a database failure.
type Refusal =
    | Domain of OrderError
    | Missing of what: string
```

## Steps

Steps pass their results with `let!`. Each insert gives the record back as
stored, with its generated key. The domain decides with `Tx.ofResult`; a
refusal rolls everything back.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let checkout (customer: Customer) (lines: OrderLine list) (now: DateTime) : Tx<Customer * Order, Refusal> =
    transaction {
        let! stored = Tx.insert customer
        let! order = Order.create stored.Id lines now |> Tx.ofResult Domain
        let! placed = Tx.insert order
        return stored, placed
    }
```

## Read, decide, write

`Tx.update (original, changed)` writes only the columns that differ, and what
the record owns by difference from what was read. A change made from a copy a
caller saw earlier reads the record with `Tx.oneAsSeen (query, seen)`: if its
version is no longer `seen`, the read is a conflict, before anything is
decided, and the update checks the version again.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let changeOrder (id: OrderId) (seen: int) (decide: Order -> Result<Order, OrderError>) : Tx<Order, Refusal> =
    transaction {
        let! order = Tx.oneAsSeen (orderById id, seen) |> Tx.orReject (Missing "order")
        let! changed = decide order |> Tx.ofResult Domain
        return! Tx.update (order, changed)
    }

let cancel id seen reason = changeOrder id seen (Order.cancel reason)
```

A record without a version field is checked by every column of the record
the change was made from, in the same command.

## What comes back

A failure says what failed and which steps were rolled back.

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let! empty = repo.Transact(checkout { ada with Email = "empty@example.com" } [] DateTime.UtcNow)

        match empty with
        | Error { Failure = TxFailure.Rejected r; RolledBack = steps } -> show "domain rejection" (r, steps |> List.map string)
        | other -> show "unexpected" other
```

A constraint with PostgreSQL's default name is that field's error:

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let! dup = repo.Transact(Tx.insert { ada with Id = CustomerId 0L; Name = "Eve" }: Tx<Customer, Refusal>)

        match dup with
        | Error { Failure = TxFailure.InvalidChanges(entity, errors) } -> show "unique email" (entity, errors)
        | other -> show "unexpected" other
```

And a stale copy is a conflict:

<!-- from: examples/Plasm.Examples/Program.fs -->
```fsharp
        let! stale = repo.Transact(cancel order.Id order.Version "changed my mind")

        match stale with
        | Error { Failure = TxFailure.Conflict entity } -> show "stale copy" entity
        | other -> show "unexpected" other
```

## Linked records

Making a list the article's complete set of tags inserts the missing tags
once; the article then writes only its links.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let retag (article: Article) (names: string list) : Tx<Article, Refusal> =
    transaction {
        let! _ = Tx.execute (Bulk.insertMissing ([ for n in names -> { Id = TagId 0L; Name = n } ], fun t -> t.Name))
        let! tags = Tx.all (tagsNamed names)
        return! Tx.update (article, { article with Tags = tags })
    }
```

## Set-based writes

A bulk write is one command for every row its query selects. It is a
transaction step too, run with `Tx.execute`.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let expireDrafts (before: DateTime) =
    Bulk.update (
        plasm { for o in tableOf<Order> do where (o.Status = Draft && o.PlacedAt < before) },
        fun o -> { o with Status = Cancelled "expired" }
    )

/// Insert new products and rename existing ones, matched on the SKU.
let syncCatalogue (products: Product list) =
    Bulk.upsert (products, (fun p -> p.Sku), fun current incoming -> { current with Name = incoming.Name })
```
