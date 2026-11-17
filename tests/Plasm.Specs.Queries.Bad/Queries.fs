/// Queries for query-check.feature that Plasm cannot translate: an assembly of
/// its own, so Repo.Start can be shown failing over it.
namespace Plasm.Specs.Queries

open Plasm
open Plasm.TestDomain.Domain

module Bad =
    /// A type to find this assembly by.
    type Marker = class end

    // String.Normalize has no SQL translation.
    let normalized = plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = "ada") }

module BadPrepared =
    let normalizedNamed =
        Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = name.Value) })

module BadProjection =
    let normalizedNames =
        Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name.Normalize() = name.Value); select c.Name })

module BadLoads =
    // A field computed from a load: return names each load once (let-loads.feature).
    let counted =
        plasm {
            for o in tableOf<Order> do
            let! payments = referring<Payment> o
            return {| Order = o; Count = List.length payments |}
        }
