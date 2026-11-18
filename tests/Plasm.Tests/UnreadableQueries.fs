/// A query value whose module cannot be read (in a native binary, a quotation
/// naming a member the binary does not keep, docs/aot.md) is a problem the
/// start-up check reports by the query's name, not an exception out of the
/// application's module.
module Plasm.Tests.UnreadableQueries

open Xunit
open Plasm
open Plasm.TestDomain.Domain

/// Its module's initialisation fails as it is read.
module Unreadable =
    let customers: Query<Customer> = failwith "this module cannot be read"

[<Fact>]
let ``a query whose module cannot be read is reported by its name`` () =
    let problems = Helpers.offlineRepo.CheckQueries(System.Reflection.Assembly.GetExecutingAssembly(), "Plasm.Tests.UnreadableQueries")
    let p = Assert.Single problems
    Assert.Equal("Plasm.Tests.UnreadableQueries.Unreadable.customers", p.Query)
    Assert.Contains("could not be read", p.Error.ToString())
    Assert.Contains("this module cannot be read", p.Error.ToString())
