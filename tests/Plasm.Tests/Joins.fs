/// SQL-shape and error-path tests for joins (no database access; the
/// end-to-end behaviour is covered by features/joins.feature).
module Plasm.Tests.Joins

open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.Tests.Helpers

let private projectionSql (p: Projection<'R>) = (ok (offlineRepo.Explain p)).Sql

[<Fact>]
let ``inner join renders an ON condition from both key selectors`` () =
    plasm {
        for o in stored<Order> do
            join c in stored<Customer> on (o.CustomerId = Some c.Id)
            orderBy o.Id
    }
    |> sqlOf
    |> contains "FROM \"orders\" AS \"t0\" INNER JOIN \"customers\" AS \"j1\" ON (\"t0\".\"customer_id\" = \"j1\".\"id\")"

[<Fact>]
let ``a joined query selects every column of every source`` () =
    let sql =
        plasm {
            for o in stored<Order> do
                leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
        }
        |> sqlOf

    contains "LEFT JOIN \"customers\" AS \"j1\"" sql
    contains "\"t0\".\"placed_at\"" sql
    contains "\"j1\".\"email\"" sql

[<Fact>]
let ``composite keys compare pairwise`` () =
    plasm {
        for l in stored<OrderLine> do
            join p in stored<Payment> on ((l.OrderId, l.UnitPrice) = (p.OrderId, p.Amount))
            orderBy l.Id
    }
    |> sqlOf
    |> contains "ON ((\"t0\".\"order_id\" = \"j1\".\"order_id\") AND (\"t0\".\"unit_price\" = \"j1\".\"amount\"))"

[<Fact>]
let ``filters written before a join take the root record`` () =
    let sql =
        plasm {
            for o in stored<Order> do
                where (o.Total > 5m)
                join c in stored<Customer> on (o.CustomerId = Some c.Id)
                where (c.Name = "Ada")
        }
        |> sqlOf

    contains "WHERE ((\"t0\".\"total\" > @p0) AND (\"j1\".\"name\" = @p1))" sql

[<Fact>]
let ``Option.forall over a left-joined source keeps unmatched rows`` () =
    plasm {
        for o in stored<Order> do
            leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
            where (c |> Option.forall (fun c -> c.Name = "Ada"))
    }
    |> sqlOf
    |> contains "WHERE ((\"j1\".\"id\" IS NULL) OR (\"j1\".\"name\" = @p0))"

[<Fact>]
let ``comparing a left-joined source with None tests its key`` () =
    let sql =
        plasm {
            for o in stored<Order> do
                leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
                where (c <> None)
        }
        |> sqlOf

    contains "WHERE (\"j1\".\"id\" IS NOT NULL)" sql

[<Fact>]
let ``ordering by a left-joined column puts missing rows first, like F# options`` () =
    plasm {
        for o in stored<Order> do
            leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
            orderBy (c |> Option.map (fun c -> c.Name))
    }
    |> sqlOf
    |> contains "ORDER BY \"j1\".\"name\" ASC NULLS FIRST"

[<Fact>]
let ``a left-joined field equal to Some value compares the present value`` () =
    plasm {
        for o in stored<Order> do
            leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
            where ((c |> Option.map (fun c -> c.Name)) = Some "Ada")
    }
    |> sqlOf
    |> contains "WHERE (\"j1\".\"name\" = @p0)"

[<Fact>]
let ``projections over joins read only the projected columns`` () =
    let sql =
        plasm {
            for o in stored<Order> do
                join c in stored<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
                select {| Order = o.Id; Customer = c.Name |}
        }
        |> projectionSql

    // Anonymous-record fields are ordered by name (Customer, Order).
    contains "SELECT \"j1\".\"name\" AS \"c0\", \"t0\".\"id\" AS \"c1\" FROM" sql

[<Fact>]
let ``Option.map over an optional field is rejected in favour of Option.bind`` () =
    let r =
        plasm {
            for c in stored<Customer> do
                leftJoin o in stored<Order> on (Some c.Id = o.CustomerId)
                where ((o |> Option.map (fun o -> o.CancellationReason)) = Some(Some "late"))
        }
        |> explain

    match err r with
    | TranslationError.UnsupportedExpression(_, reason) -> Assert.Contains("use Option.bind", reason)
    | other -> failwithf "expected UnsupportedExpression, got %A" other

[<Fact>]
let ``a left-joined source compared with a value is rejected`` () =
    let r =
        plasm {
            for o in stored<Order> do
                leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
                where (c = Some { Id = CustomerId 1L; Name = "Ada"; Email = "a" })
        }
        |> explain

    match err r with
    | TranslationError.UnsupportedExpression(_, reason) -> Assert.Contains("only be compared with None", reason)
    | other -> failwithf "expected UnsupportedExpression, got %A" other

[<Fact>]
let ``relationship predicates work on the root of a joined query`` () =
    let sql =
        plasm {
            for o in stored<Order> do
                leftJoin c in stored<Customer> on (o.CustomerId = Some c.Id)
                where (not (exists (plasm { for p in stored<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })))
                orderBy o.PlacedAt
                select (o.Id, o.Total, c |> Option.map (fun c -> c.Email))
        }
        |> projectionSql

    contains "NOT EXISTS (SELECT 1 FROM \"payments\" AS \"t1\" WHERE ((\"t1\".\"order_id\" = \"t0\".\"id\")" sql
    contains "SELECT \"t0\".\"id\" AS \"c0\", \"t0\".\"total\" AS \"c1\", \"j1\".\"email\" AS \"c2\"" sql
