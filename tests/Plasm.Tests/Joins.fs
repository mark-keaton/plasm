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
        for o in tableOf<Order> do
            join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            orderBy o.Id
    }
    |> sqlOf
    |> contains "FROM \"orders\" AS \"o\"\nINNER JOIN \"customers\" AS \"c\" ON \"o\".\"customer_id\" = \"c\".\"id\""

[<Fact>]
let ``a joined query selects every column of every source`` () =
    let sql =
        plasm {
            for o in tableOf<Order> do
                leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
        }
        |> sqlOf

    contains "LEFT JOIN \"customers\" AS \"c\"" sql
    contains "\"o\".\"placed_at\"" sql
    contains "\"c\".\"email\"" sql

[<Fact>]
let ``composite keys compare pairwise`` () =
    plasm {
        for l in tableOf<OrderLine> do
            join p in tableOf<Payment> on ((l.OrderId, l.UnitPrice) = (p.OrderId, p.Amount))
            orderBy l.Id
    }
    |> sqlOf
    |> contains "ON \"l\".\"order_id\" = \"p\".\"order_id\" AND \"l\".\"unit_price\" = \"p\".\"amount\""

[<Fact>]
let ``filters written before a join take the root record`` () =
    let sql =
        plasm {
            for o in tableOf<Order> do
                where (o.Total > 5m)
                join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                where (c.Name = "Ada")
        }
        |> sqlOf

    contains "WHERE \"o\".\"total\" > @p0 AND \"c\".\"name\" = @p1" sql

[<Fact>]
let ``Option.forall over a left-joined source keeps unmatched rows`` () =
    plasm {
        for o in tableOf<Order> do
            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            where (c |> Option.forall (fun c -> c.Name = "Ada"))
    }
    |> sqlOf
    |> contains "WHERE \"c\".\"id\" IS NULL OR \"c\".\"name\" = @p0"

[<Fact>]
let ``comparing a left-joined source with None tests its key`` () =
    let sql =
        plasm {
            for o in tableOf<Order> do
                leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                where (c <> None)
        }
        |> sqlOf

    contains "WHERE \"c\".\"id\" IS NOT NULL" sql

[<Fact>]
let ``ordering by a left-joined column puts missing rows first, like F# options`` () =
    plasm {
        for o in tableOf<Order> do
            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            orderBy (c |> Option.map (fun c -> c.Name))
    }
    |> sqlOf
    |> contains "ORDER BY \"c\".\"name\" NULLS FIRST"

[<Fact>]
let ``a left-joined field equal to Some value compares the present value`` () =
    plasm {
        for o in tableOf<Order> do
            leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
            where ((c |> Option.map (fun c -> c.Name)) = Some "Ada")
    }
    |> sqlOf
    |> contains "WHERE \"c\".\"name\" = @p0"

[<Fact>]
let ``projections over joins read only the projected columns`` () =
    let sql =
        plasm {
            for o in tableOf<Order> do
                join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                orderBy o.Id
                select {| Order = o.Id; Customer = c.Name |}
        }
        |> projectionSql

    // Anonymous-record fields are ordered by name (Customer, Order).
    contains "SELECT \"c\".\"name\" AS \"Customer\", \"o\".\"id\" AS \"Order\"\nFROM" sql

[<Fact>]
let ``Option.map over an optional field is rejected in favour of Option.bind`` () =
    let r =
        plasm {
            for c in tableOf<Customer> do
                leftJoin o in tableOf<Order> on (Some c.Id = o.CustomerId)
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
            for o in tableOf<Order> do
                leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
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
            for o in tableOf<Order> do
                leftJoin c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                where (not (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })))
                orderBy o.PlacedAt
                select (o.Id, o.Total, c |> Option.map (fun c -> c.Email))
        }
        |> projectionSql

    contains "NOT EXISTS (SELECT 1 FROM \"payments\" AS \"p\" WHERE \"p\".\"order_id\" = \"o\".\"id\"" sql
    contains "SELECT \"o\".\"id\" AS \"c0\", \"o\".\"total\" AS \"c1\", \"c\".\"email\" AS \"c2\"" sql
