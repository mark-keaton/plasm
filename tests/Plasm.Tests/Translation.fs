/// SQL-shape contract tests. These use Explain only: no database is contacted.
module Plasm.Tests.Translation

open System
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.Tests.Helpers

let isBig (d: decimal) = d > 100m

let unsupported (q: Query<'T>) =
    match err (explain q) with
    | TranslationError.UnsupportedExpression(e, reason) -> e, reason
    | other -> failwithf "expected UnsupportedExpression, got %O" other

let invalidQuery (q: Query<'T>) =
    match err (explain q) with
    | TranslationError.InvalidQuery m -> m
    | other -> failwithf "expected InvalidQuery, got %O" other

// -- NULL semantics ----------------------------------------------------------

[<Fact>]
let ``= None translates to IS NULL`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason = None) }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" IS NULL"

[<Fact>]
let ``<> None translates to IS NOT NULL`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason <> None) }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" IS NOT NULL"

[<Fact>]
let ``captured None value translates to IS NULL, not = NULL`` () =
    let reason: string option = None
    let sql = plasm { for o in tableOf<Order> do where (o.CancellationReason = reason) } |> sqlOf
    contains "IS NULL" sql
    notContains "= NULL" sql

[<Fact>]
let ``= Some x is plain equality`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason = Some "late") }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" = @p0"

[<Fact>]
let ``<> Some x on an option includes NULL rows via IS DISTINCT FROM`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason <> Some "late") }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" IS DISTINCT FROM @p0"

[<Fact>]
let ``option to option column comparison uses IS NOT DISTINCT FROM`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason = o.CancellationReason) }
    |> sqlOf
    |> contains "IS NOT DISTINCT FROM"

[<Fact>]
let ``x.IsSome translates to IS NOT NULL`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason.IsSome) }
    |> sqlOf
    |> contains "IS NOT NULL"

[<Fact>]
let ``Option.isNone and Option.isSome translate to null tests`` () =
    plasm { for o in tableOf<Order> do where (Option.isNone o.CustomerId) }
    |> sqlOf
    |> contains "\"t0\".\"customer_id\" IS NULL"

    plasm { for o in tableOf<Order> do where (Option.isSome o.CustomerId) }
    |> sqlOf
    |> contains "\"t0\".\"customer_id\" IS NOT NULL"

// -- Membership --------------------------------------------------------------

[<Fact>]
let ``List.contains translates to = ANY with one array parameter`` () =
    let ids = [ OrderId 1L; OrderId 2L; OrderId 3L ]
    let sql = plasm { for o in tableOf<Order> do where (List.contains o.Id ids) } |> sqlOf
    contains "\"t0\".\"id\" = ANY(@p0)" sql
    notContains "@p1" sql

[<Fact>]
let ``empty membership translates to FALSE`` () =
    let ids: OrderId list = []
    plasm { for o in tableOf<Order> do where (List.contains o.Id ids) } |> sqlOf |> contains "WHERE FALSE"

// -- Relationship predicates -------------------------------------------------

[<Fact>]
let ``exists translates to a correlated EXISTS`` () =
    let sql =
        plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })) }
        |> sqlOf

    contains "EXISTS (SELECT 1 FROM \"payments\" AS \"t1\" WHERE" sql
    contains "\"t1\".\"order_id\" = \"t0\".\"id\"" sql

[<Fact>]
let ``List.exists over an owned list translates to EXISTS`` () =
    plasm { for o in tableOf<Order> do where (o.Lines |> List.exists (fun l -> l.UnitPrice > 5m)) }
    |> sqlOf
    |> contains "EXISTS ("

[<Fact>]
let ``List.forall over an owned list translates to NOT EXISTS of the negation`` () =
    let sql = plasm { for o in tableOf<Order> do where (o.Lines |> List.forall (fun l -> l.UnitPrice > 5m)) } |> sqlOf
    contains "NOT EXISTS (" sql

// -- Strings -----------------------------------------------------------------

[<Fact>]
let ``StartsWith escapes LIKE metacharacters`` () =
    let sql = plasm { for c in tableOf<Customer> do where (c.Name.StartsWith "50%_off\\") } |> sqlOf
    contains "LIKE @p0" sql
    notContains "ESCAPE" sql
    notContains "50%" sql

[<Fact>]
let ``Contains and EndsWith translate to LIKE`` () =
    plasm { for c in tableOf<Customer> do where (c.Name.Contains "x" && c.Email.EndsWith "@example.com") }
    |> sqlOf
    |> contains "LIKE"

[<Fact>]
let ``ToLower translates to lower()`` () =
    plasm { for c in tableOf<Customer> do where (c.Name.ToLower() = "ada") } |> sqlOf |> contains "lower(\"t0\".\"name\")"

// -- Ordering and paging -----------------------------------------------------

[<Fact>]
let ``option ordering matches F# (None first ascending, last descending)`` () =
    plasm { for o in tableOf<Order> do orderBy o.CancellationReason } |> sqlOf |> contains "\"t0\".\"cancellation_reason\" NULLS FIRST"
    plasm { for o in tableOf<Order> do orderByDescending o.CancellationReason } |> sqlOf |> contains "DESC NULLS LAST"

[<Fact>]
let ``pagination appends the primary key as tiebreaker`` () =
    plasm {
        for o in tableOf<Order> do
            orderBy o.PlacedAt
            take 10
    }
    |> sqlOf
    |> contains "ORDER BY \"t0\".\"placed_at\", \"t0\".\"id\" LIMIT @p0"

[<Fact>]
let ``pagination already ordered by key adds no tiebreaker`` () =
    let sql =
        plasm {
            for o in tableOf<Order> do
                orderByDescending o.Id
                skip 5
        }
        |> sqlOf

    contains "ORDER BY \"t0\".\"id\" DESC OFFSET" sql

[<Fact>]
let ``take without ordering is rejected`` () =
    let m = plasm { for o in tableOf<Order> do take 10 } |> invalidQuery
    Assert.Contains("ordering", m)

[<Fact>]
let ``skip without ordering is rejected`` () =
    plasm { for o in tableOf<Order> do skip 10 } |> invalidQuery |> ignore

// -- Loads -------------------------------------------------------------------

[<Fact>]
let ``load steps report key batching`` () =
    let plan = Query.from (tableOf<Order>) |> explain |> ok
    let step = plan.Loads.Head
    Assert.Equal(LoadStrategy.BatchedByKeys 5000, step.Strategy)
    contains "\"t0\".\"order_id\" = ANY(@p0)" step.Sql

// -- Unsupported expressions -------------------------------------------------

[<Fact>]
let ``calling an arbitrary F# function on a column is unsupported and identified`` () =
    let e, reason = plasm { for o in tableOf<Order> do where (isBig o.Total) } |> unsupported
    Assert.Contains("isBig", e)
    Assert.Contains("o.Total", e)
    Assert.Contains("will not be evaluated in application memory", reason)

[<Fact>]
let ``ordering comparison on an option is unsupported`` () =
    let e, _ = plasm { for o in tableOf<Order> do where (o.CancellationReason < Some "m") } |> unsupported
    Assert.Contains("CancellationReason", e)

[<Fact>]
let ``string method with a column argument is unsupported`` () =
    plasm { for c in tableOf<Customer> do where (c.Email.StartsWith c.Name) } |> unsupported |> ignore

[<Fact>]
let ``using an owned list as a value is unsupported`` () =
    plasm { for o in tableOf<Order> do where (o.Lines = []) } |> unsupported |> ignore

[<Fact>]
let ``unsupported ordering key is identified`` () =
    let e, _ = plasm { for o in tableOf<Order> do orderBy (string o.Total) } |> unsupported
    Assert.Contains("Total", e)

// -- Captured values are parameters ------------------------------------------

[<Fact>]
let ``captured strings never appear in the SQL text`` () =
    let name = "x'); DROP TABLE customers; --"
    let sql = plasm { for c in tableOf<Customer> do where (c.Name = name) } |> sqlOf
    notContains "DROP" sql
    contains "\"t0\".\"name\" = @p0" sql

[<Fact>]
let ``captured expressions are evaluated once as parameters`` () =
    let threshold = 40m
    let sql = plasm { for o in tableOf<Order> do where (o.Total > threshold * 2m + 1m) } |> sqlOf
    contains "\"t0\".\"total\" > @p0" sql

[<Fact>]
let ``SQL injection attempt is inert against a real database`` () =
    task {
        let! db = Plasm.TestDomain.Database.freshDefault ()
        let! _ = Plasm.TestDomain.Database.Seed.customer db "Ada" "ada@example.com"
        let name = "x' OR '1'='1"
        let! r = db.Repo.All(plasm { for c in tableOf<Customer> do where (c.Name = name) })
        Assert.Empty(ok r)
        let! n = db.Scalar<int64>("select count(*) from customers")
        Assert.Equal(1L, n)
    }

// -- Model configuration -----------------------------------------------------

/// A class: no scalar mapping, and not a record (a record would be a value).
type Address(street: string) =
    member _.Street = street

[<Table "w">]
type WithAddress = { [<Assigned>] Id: int; Home: Address }

/// A list of records that name no table: not stored.
type Kid = { Id: int; ParentId: string }

[<Table "parents">]
type Parent =
    { [<Assigned>] Id: int64
      Kids: Kid list }

type KidId = KidId of int64

/// Kids that are stored but hold no parent's key: linked, so a join table is needed.
[<Table "kids">]
type StoredKid = { Id: KidId; Name: string }

[<Table "parents">]
type Guardian =
    { [<Assigned>] Id: int64
      Kids: StoredKid list }

type Mood =
    | Happy
    | Sad

let configErrors scalars types =
    match Model.build scalars types with
    | Ok _ -> failwith "expected configuration errors"
    | Error es -> es

[<Fact>]
let ``unmapped scalar type identifies model and property`` () =
    let e = configErrors [] [ typeof<WithAddress> ] |> List.exactlyOne
    Assert.Equal("WithAddress", e.Model)
    Assert.Equal(Some "Home", e.Property)
    Assert.Contains("Address", e.Message)

[<Fact>]
let ``a list of records that are not stored is a configuration error`` () =
    let es = configErrors [] [ typeof<Parent> ]
    Assert.Contains(es, fun e -> e.Model = "Parent" && e.Property = Some "Kids" && e.Message.Contains "names no table")

[<Fact>]
let ``a list of stored records without the owner's key needs a join table`` () =
    let es = configErrors [] [ typeof<Guardian>; typeof<StoredKid> ]
    Assert.Contains(es, fun e -> e.Property = Some "Kids" && e.Message.Contains "names no join table")

[<Fact>]
let ``duplicate scalar registration is a configuration error`` () =
    let es =
        configErrors [ Scalar.wrap OrderId (fun (OrderId v) -> v); Scalar.wrap OrderId (fun (OrderId v) -> v) ] []

    Assert.Contains(es, fun e -> e.Model = "OrderId")

[<Fact>]
let ``Scalar.enum with a missing case is rejected`` () =
    Assert.Throws<PlasmConfigurationException>(fun () -> Scalar.enum [ Happy, "happy" ] |> ignore) |> ignore

[<Fact>]
let ``Scalar.enum with duplicate stored names is rejected`` () =
    Assert.Throws<PlasmConfigurationException>(fun () -> Scalar.enum [ Happy, "x"; Sad, "x" ] |> ignore)
    |> ignore

[<Fact>]
let ``the test domain model is valid`` () =
    Assert.Equal(9, Schemas.model.Types.Length)

// -- Option accessors and boolean operators ------------------------------------

[<Fact>]
let ``x.Value on an option column compares the present value`` () =
    plasm { for o in tableOf<Order> do where (o.CancellationReason.Value = "late") }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" = @p0"

[<Fact>]
let ``Option.get on an option column compares the present value`` () =
    plasm { for o in tableOf<Order> do where (Option.get o.CancellationReason = "late") }
    |> sqlOf
    |> contains "\"t0\".\"cancellation_reason\" = @p0"

[<Fact>]
let ``not, || and && translate`` () =
    let sql = plasm { for o in tableOf<Order> do where (not (o.Total > 1m) || (o.Status = Draft && o.Total < 5m)) } |> sqlOf
    // total cannot be NULL, so plain NOT (index-friendly) is exact.
    contains "\"t0\".\"total\" <= @p0" sql
    contains " OR " sql
    contains " AND " sql

[<Fact>]
let ``string concatenation translates to ||`` () =
    plasm { for c in tableOf<Customer> do where (c.Name + "!" = "Ada!") } |> sqlOf |> contains "\"t0\".\"name\" || @p0 = @p1"

[<Fact>]
let ``nested relationship predicates get distinct aliases`` () =
    let sql =
        plasm {
            for c in tableOf<Customer> do
                where (
                    exists (
                        plasm {
                            for o in tableOf<Order> do
                                where (o.CustomerId = Some c.Id && exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Amount > 1m) }))
                        }
                    )
                )
        }
        |> sqlOf

    contains "\"t2\".\"order_id\" = \"t1\".\"id\"" sql
    contains "\"t1\".\"customer_id\" IS NOT DISTINCT FROM \"t0\".\"id\"" sql
