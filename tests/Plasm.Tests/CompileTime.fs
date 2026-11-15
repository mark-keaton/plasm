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
        for o in orders do
        where (o.CustomerId = Some customer && o.Status = Published && o.Total > 10m)
        orderByDescending o.PlacedAt
        thenBy o.Id
        skip 20
        take 20
    }
"""

[<Fact>]
let ``accepts preloads with reusable child queries`` () =
    accepts
        """
let lines = plasm { for l in orderLines do preload l.Product }
let ok = plasm { for p in payments do where (p.Status = Succeeded); orderBy p.CreatedAt; limitPerParent 3 }
let q = plasm { for o in orders do preload o.Lines lines; preload o.Payments ok; preload o.Customer }
let byFn (src: Query<Order>) = plasm { for o in src do preload o.Payments }
"""

[<Fact>]
let ``accepts projections to tuples and anonymous records`` () =
    accepts
        """
let a : Projection<OrderId * decimal> = plasm { for o in orders do select (o.Id, o.Total) }
let b = plasm { for o in orders do where (o.Total > 0m); select {| Id = o.Id; Reason = o.CancellationReason |} }
"""

[<Fact>]
let ``accepts changesets`` () =
    accepts
        """
let change (o: Order) (input: System.Collections.Generic.IReadOnlyDictionary<string, string>) =
    o
    |> Changeset.edit (fun o -> { o with Status = Published })
    |> Changeset.set((fun o -> o.Total), 12m)
    |> Changeset.clear(fun o -> o.CancellationReason)
    |> Changeset.cast(input, fun o -> o.Total)
    |> Changeset.validateRange((fun o -> o.Total), min = 0m)
    |> Changeset.validateOptionalLength((fun o -> o.CancellationReason), max = 100)
    |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.allChildren, [])
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
        "let q = plasm { for o in orders do where (o.Total > 100m) }"
        "let q = plasm { for o in orders do where (o.Total > \"100\") }"
        "FS0001"
        "string"

[<Fact>]
let ``rejects comparing a CustomerId with an OrderId`` () =
    rejects
        "let q = plasm { for o in orders do where (o.CustomerId = Some (CustomerId 1L)) }"
        "let q = plasm { for o in orders do where (o.CustomerId = Some (OrderId 1L)) }"
        "FS0001"
        "OrderId"

[<Fact>]
let ``rejects preloading a non-relationship field`` () =
    rejects
        "let q = plasm { for o in orders do preload o.Payments }"
        "let q = plasm { for o in orders do preload o.Total }"
        "FS0001"
        "Assoc"

[<Fact>]
let ``rejects preloading a field Order does not have`` () =
    rejects
        "let q = plasm { for o in orders do preload o.Payments }"
        "let q = plasm { for o in orders do preload o.Product }"
        "FS0039"
        "Product"

[<Fact>]
let ``rejects a child query of the wrong type`` () =
    rejects
        "let q = plasm { for o in orders do preload o.Payments (plasm { for p in payments do where (p.Amount > 0m) }) }"
        "let q = plasm { for o in orders do preload o.Payments (plasm { for l in orderLines do where (l.UnitPrice > 0m) }) }"
        "FS0041"
        "Query<OrderLine>"

[<Fact>]
let ``rejects where after select`` () =
    rejects
        "let q = plasm { for o in orders do where (o.Total > 1m); select o.Id }"
        "let q = plasm { for o in orders do select o.Id; where (o.Total > 1m) }"
        "FS0193"
        "Projection<OrderId>"

[<Fact>]
let ``rejects setting a field to a value of the wrong type`` () =
    rejects
        "let f (o: Order) = Changeset.update o |> Changeset.set((fun o -> o.Total), 1m)"
        "let f (o: Order) = Changeset.update o |> Changeset.set((fun o -> o.Total), \"1\")"
        "FS0001"
        "string"

[<Fact>]
let ``rejects clearing a non-optional field`` () =
    rejects
        "let f (o: Order) = Changeset.update o |> Changeset.clear(fun o -> o.CancellationReason)"
        "let f (o: Order) = Changeset.update o |> Changeset.clear(fun o -> o.Total)"
        "FS0001"
        "option"

[<Fact>]
let ``rejects replacing children without stating the replacement scope`` () =
    rejects
        "let f (o: Order) = Changeset.update o |> Changeset.replaceChildren((fun o -> o.Lines), ReplaceScope.allChildren, [])"
        "let f (o: Order) = Changeset.update o |> Changeset.replaceChildren((fun o -> o.Lines), [])"
        "FS0501"
        "scope: ReplaceScope"

[<Fact>]
let ``rejects a hasMany foreign key selected from the wrong record type`` () =
    rejects
        "let s = schema<Order> \"o\" { identityKey (fun o -> o.Id); hasMany (fun o -> o.Lines) (fun l -> l.OrderId) }"
        "let s = schema<Order> \"o\" { identityKey (fun o -> o.Id); hasMany (fun o -> o.Lines) (fun (p: Payment) -> p.OrderId) }"
        "FS0001"
        "Payment"

[<Fact>]
let ``rejects Rel.any on a single-valued relationship`` () =
    rejects
        "let q = plasm { for o in orders do where (Rel.any (fun p -> p.Amount > 1m) o.Payments) }"
        "let q = plasm { for o in orders do where (Rel.any (fun (c: Customer) -> c.Name = \"x\") o.Customer) }"
        "FS0001"
        "list"

[<Fact>]
let ``rejects a nested join-style for (joins are not silently ignored)`` () =
    rejects
        "let q = plasm { for o in orders do where (o.Total > 1m) }"
        "let q = plasm { for o in orders do for p in payments do where (p.Amount > 1m) }"
        "FS0041"
        "For"
