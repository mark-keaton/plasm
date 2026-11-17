/// Queries for query-check.feature that all translate: an assembly of its own,
/// so Repo.Start can be shown starting over it.
namespace Plasm.Specs.Queries

open Plasm
open Plasm.TestDomain.Domain

module Good =
    /// A type to find this assembly by.
    type Marker = class end

    let everyone = Query.from (tableOf<Customer>)

    let byName = Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value) })

    let paid = plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })) }

    let namesOf = Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value); select c.Name })

    /// A function returning a query: not a value, so not checked, only counted.
    let namedLike (prefix: string) = plasm { for c in tableOf<Customer> do where (c.Name.StartsWith prefix) }

    /// Loads bound with let! (let-loads.feature): translated at start-up like any query.
    let withPayments =
        plasm {
            for o in tableOf<Order> do
            orderBy o.Total
            let! payments = referring<Payment> o
            return {| Order = o; Payments = payments |}
        }
