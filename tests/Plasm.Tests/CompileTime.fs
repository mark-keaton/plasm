/// Compile-time acceptance and rejection checks for the typed API. Each
/// rejection is paired with a minimally different control snippet that must
/// compile, so a rejection cannot pass because of an unrelated error.
module Plasm.Tests.CompileTime

open Xunit
open Plasm.TestDomain

let accepts (code: string) =
    task {
        let! r = CompileCheck.check code
        Assert.True(r.Compiles, "expected to compile:\n" + code + "\n" + String.concat "\n" r.Errors)
    }

let rejects (control: string) (bad: string) (expected: string) (mentions: string) =
    task {
        do! accepts control
        let! r = CompileCheck.check bad
        Assert.False(r.Compiles, "expected a compile error:\n" + bad)

        Assert.True(
            r.Errors |> List.exists (fun e -> e.StartsWith expected && e.Contains mentions),
            $"expected {expected} mentioning '{mentions}', got:\n" + String.concat "\n" r.Errors
        )
    }

// -- Accepted ----------------------------------------------------------------

[<Fact>]
let ``accepts a typical filtered, ordered, paged query`` () =
    accepts
        """
let page (customer: CustomerId) =
    plasm {
        for o in tableOf<Order> do
        where (o.CustomerId = Some customer && o.Status = Published && o.Total > 10m)
        orderByDescending o.PlacedAt
        thenBy o.Id
        skip 20
        take 20
    }
"""

[<Fact>]
let ``accepts queries that reach other aggregates`` () =
    accepts
        """
let paid = plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })) }
let big = plasm { for o in tableOf<Order> do where (o.Lines |> List.exists (fun l -> l.UnitPrice > 10m)) }
let paymentsOf = Query.prepare (fun (ids: Arg<OrderId list>) -> plasm { for p in tableOf<Payment> do where (List.contains p.OrderId ids.Value) })
"""
[<Fact>]
let ``accepts projections to tuples and anonymous records`` () =
    accepts
        """
let a : Projection<OrderId * decimal> = plasm { for o in tableOf<Order> do select (o.Id, o.Total) }
let b = plasm { for o in tableOf<Order> do where (o.Total > 0m); select {| Id = o.Id; Reason = o.CancellationReason |} }
"""

[<Fact>]
let ``accepts writes of records`` () =
    accepts
        """
let change (o: Order) : Tx<Order, unit> = Tx.update (o, { o with Status = Published; CancellationReason = None; Lines = [] })
let stale (o: Order) : Tx<Order, unit> = Tx.update (o, { o with Total = 1m }, expectVersion = 3)
"""
[<Fact>]
let ``accepts transactions with typed dependent steps`` () =
    accepts
        """
let place (o: Order) (l: OrderLine) =
    transaction {
        let! saved = Tx.insert o
        let! line = Tx.insert { l with OrderId = saved.Id }
        return saved.Id, line.Id
    }
"""

// -- Rejected ----------------------------------------------------------------

[<Fact>]
let ``rejects comparing a decimal column with a string`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (o.Total > 100m) }"
        "let q = plasm { for o in tableOf<Order> do where (o.Total > \"100\") }"
        "FS0001"
        "string"

[<Fact>]
let ``rejects comparing a CustomerId with an OrderId`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (o.CustomerId = Some (CustomerId 1L)) }"
        "let q = plasm { for o in tableOf<Order> do where (o.CustomerId = Some (OrderId 1L)) }"
        "FS0001"
        "OrderId"

[<Fact>]
let ``rejects ordering an owned list by a field its records do not have`` () =
    rejects
        "[<Table \"orders\">]\ntype Ordered = { Id: OrderId; [<OrderedBy(nameof the<OrderLine>.UnitPrice)>] Lines: OrderLine list }"
        "[<Table \"orders\">]\ntype Ordered = { Id: OrderId; [<OrderedBy(nameof the<OrderLine>.Price)>] Lines: OrderLine list }"
        "FS0039"
        "Price"
[<Fact>]
let ``rejects an exists query compared with a value`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) })) }"
        "let q = plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id) }) = 1) }"
        "FS0001"
        "bool"
[<Fact>]
let ``rejects where after select`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (o.Total > 1m); select o.Id }"
        "let q = plasm { for o in tableOf<Order> do select o.Id; where (o.Total > 1m) }"
        "FS0193"
        "Projection<OrderId>"

[<Fact>]
let ``rejects an update of two different records`` () =
    rejects
        "let f (o: Order) : Tx<Order, unit> = Tx.update (o, { o with Total = 1m })"
        "let f (o: Order) (c: Customer) : Tx<Order, unit> = Tx.update (o, c)"
        "FS0001"
        "Customer"
[<Fact>]
let ``rejects List.exists over a field that is not a list`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (o.Lines |> List.exists (fun l -> l.UnitPrice > 1m)) }"
        "let q = plasm { for o in tableOf<Order> do where (o.Total |> List.exists (fun l -> l > 1m)) }"
        "FS0001"
        "list"
[<Fact>]
let ``rejects a nested join-style for (joins are not silently ignored)`` () =
    rejects
        "let q = plasm { for o in tableOf<Order> do where (o.Total > 1m) }"
        "let q = plasm { for o in tableOf<Order> do for p in tableOf<Payment> do where (p.Amount > 1m) }"
        "FS0041"
        "For"
