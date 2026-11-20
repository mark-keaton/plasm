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

## A step that may fail

`Tx.attempt` runs a step under a savepoint. Its failure is a value, the
step's writes are rolled back, and the transaction goes on.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let register (customer: Customer) : Tx<Customer, Refusal> =
    transaction {
        let! tried = Tx.attempt (Tx.insert customer)

        match tried with
        | Ok stored -> return stored
        | Error _ ->
            return!
                Tx.one (plasm { for c in tableOf<Customer> do where (c.Email = customer.Email) })
                |> Tx.orReject (Missing "customer")
    }
```

A serialization failure, a deadlock or a lost connection is never caught by
an attempt: it fails the whole transaction, which `repo.Transact(tx, retry =
RetryPolicy.OnSerializationFailure 3)` runs again from the start, so a step
of your own (`Tx.run`) must be safe to run twice.

## Claiming a row

`locking` reads rows locked until the transaction ends. With `SkipLocked`,
workers each claim a different row.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let claimDraft: Tx<Order option, Refusal> =
    Tx.one (
        plasm {
            for o in tableOf<Order> do
                where (o.Status = Draft)
                orderBy o.PlacedAt
                take 1
                locking RowLock.ForUpdate.SkipLocked
        }
    )
```

## Many writes at once

`Tx.traverse` makes one step of a list of them. Each of these orders is one
insert, so their statements travel together: two round trips however many
orders, the orders and then their lines, which need the generated keys.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let placeAll (orders: Order list) : Tx<Order list, Refusal> =
    orders |> Tx.traverse (fun o -> Tx.insert o)
```

`Tx.traverse` sends its items' writes together when each item is one write;
an item of more steps, and steps a transaction sequences, run one after
another.

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
        // The conflict names the stale record: its type, and its key, read with a type test.
        | Error { Failure = TxFailure.Conflict(entity, (:? OrderId as id)) } -> show "stale copy" (entity, id)
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

`Bulk.updateReturning` gives back the rows it changed, as they are now and
read whole; `Bulk.deleteReturning` gives back the rows it removed, and is
refused for a record that owns or links others, whose rows would go first.

<!-- from: examples/Plasm.Examples/Writes.fs -->
```fsharp
let expireDraftsReturning (before: DateTime) =
    Bulk.updateReturning (
        plasm { for o in tableOf<Order> do where (o.Status = Draft && o.PlacedAt < before) },
        fun o -> { o with Status = Cancelled "expired" }
    )
```
