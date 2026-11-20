/// 3. Writes are transactions, and transactions are values: read what the
///    decision needs, let the domain decide, write what it decided.
module Shop.Writes

open System
open Plasm
open Shop.Model
open Shop.Queries

/// Why a transaction stopped without a database failure.
type Refusal =
    | Domain of OrderError
    | Missing of what: string

/// A customer and their first order, with its lines: all of it, or none.
/// Each insert gives the record back as stored, with its generated key.
let checkout (customer: Customer) (lines: OrderLine list) (now: DateTime) : Tx<Customer * Order, Refusal> =
    transaction {
        let! stored = Tx.insert customer
        let! order = Order.create stored.Id lines now |> Tx.ofResult Domain
        let! placed = Tx.insert order
        return stored, placed
    }

/// Changes an order the way `decide` says, if the caller's copy (`seen`) is
/// the stored version: a stale copy is a conflict before anything is decided.
/// Only the columns that differ are written, and the lines by difference.
let changeOrder (id: OrderId) (seen: int) (decide: Order -> Result<Order, OrderError>) : Tx<Order, Refusal> =
    transaction {
        let! order = Tx.oneAsSeen (orderById id, seen) |> Tx.orReject (Missing "order")
        let! changed = decide order |> Tx.ofResult Domain
        return! Tx.update (order, changed)
    }

let cancel id seen reason = changeOrder id seen (Order.cancel reason)

/// Makes `names` the article's complete set of tags. A tag is a shared row:
/// the missing ones are inserted once; the article then writes only its links.
let retag (article: Article) (names: string list) : Tx<Article, Refusal> =
    transaction {
        let! _ = Tx.execute (Bulk.insertMissing ([ for n in names -> { Id = TagId 0L; Name = n } ], fun t -> t.Name))
        let! tags = Tx.all (tagsNamed names)
        return! Tx.update (article, { article with Tags = tags })
    }

/// Registers a customer, or gives the one who already has the email. The
/// insert is tried under a savepoint, so its failure is a value and the
/// transaction goes on.
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

/// The oldest draft no other worker holds, locked until the transaction ends.
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

// -- Bulk writes ----------------------------------------------------------------

/// One UPDATE for every stale draft; versions are incremented.
let expireDrafts (before: DateTime) =
    Bulk.update (
        plasm { for o in tableOf<Order> do where (o.Status = Draft && o.PlacedAt < before) },
        fun o -> { o with Status = Cancelled "expired" }
    )

/// As expireDrafts, giving back the orders as they are now, read whole.
let expireDraftsReturning (before: DateTime) =
    Bulk.updateReturning (
        plasm { for o in tableOf<Order> do where (o.Status = Draft && o.PlacedAt < before) },
        fun o -> { o with Status = Cancelled "expired" }
    )

/// Insert new products and rename existing ones, matched on the SKU.
let syncCatalogue (products: Product list) =
    Bulk.upsert (products, (fun p -> p.Sku), fun current incoming -> { current with Name = incoming.Name })
