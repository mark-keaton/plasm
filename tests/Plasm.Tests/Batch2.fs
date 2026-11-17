/// Edge paths for aggregates, bulk writes, hand-written SQL and prepared
/// queries (the main behaviour is in features/*.feature).
module Plasm.Tests.Batch2

open System
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

let private projectionSql (p: Projection<'R>) = (ok (offlineRepo.Explain p)).Sql

let private invalidQuery (r: Result<'a, TranslationError>) =
    match r with
    | Error(TranslationError.InvalidQuery m) -> m
    | other -> failwithf "expected InvalidQuery, got %A" other

// -- Aggregates --------------------------------------------------------------

[<Fact>]
let ``countDistinct renders COUNT DISTINCT and sum is zero for no rows`` () =
    plasm {
        for o in tableOf<Order> do
            select (Agg.countDistinct o.CustomerId, Agg.sum o.Total)
    }
    |> projectionSql
    |> contains "SELECT COUNT(DISTINCT \"t0\".\"customer_id\") AS \"c0\", COALESCE(SUM(\"t0\".\"total\"), 0) AS \"c1\""

[<Fact>]
let ``aggregates of domain types come back as the domain type`` () =
    task {
        let! db = freshDefault ()
        let! a = Seed.order db None "draft" 1m (Seed.at 1)
        let! b = Seed.order db None "draft" 2m (Seed.at 2)

        let! r =
            db.Repo.One(
                plasm {
                    for o in tableOf<Order> do
                        select (Agg.min o.Id, Agg.max o.Id, Agg.max o.PlacedAt, Agg.min o.Status)
                }
            )

        Assert.Equal(Some(Some a, Some b, Some(Seed.at 2), Some Draft), ok r)
    }

[<Fact>]
let ``nested aggregates are rejected`` () =
    plasm {
        for o in tableOf<Order> do
            select (Agg.max (Agg.sum o.Total))
    }
    |> offlineRepo.Explain
    |> function
        | Error(TranslationError.UnsupportedExpression(_, reason)) -> Assert.Contains("nested", reason)
        | other -> failwithf "expected rejection, got %A" other

[<Fact>]
let ``a grouped query without select cannot be read as records`` () =
    plasm {
        for o in tableOf<Order> do
            groupBy o.CustomerId
    }
    |> explain
    |> invalidQuery
    |> fun m -> Assert.Contains("finish it with select", m)

[<Fact>]
let ``ordering a grouped query by an ungrouped column is rejected`` () =
    plasm {
        for o in tableOf<Order> do
            groupBy o.CustomerId
            orderBy o.Total
            select (o.CustomerId, Agg.count ())
    }
    |> offlineRepo.Explain
    |> invalidQuery
    |> fun m -> Assert.Contains("o.Total is neither grouped nor aggregated", m)

[<Fact>]
let ``paging groups appends the grouping keys as the tiebreaker`` () =
    plasm {
        for o in tableOf<Order> do
            groupBy (o.CustomerId, o.Status)
            orderByDescending (Agg.count ())
            take 10
            select (o.CustomerId, o.Status, Agg.count ())
    }
    |> projectionSql
    |> contains
        "GROUP BY \"t0\".\"customer_id\", \"t0\".\"status\" ORDER BY COUNT(*) DESC, \"t0\".\"customer_id\", \"t0\".\"status\" LIMIT @p0"

[<Fact>]
let ``count and exists over joins, pages and groups`` () =
    task {
        let! db = freshDefault ()
        let! ada = Seed.customer db "Ada" "a@x"
        let! _ = Seed.order db (Some ada) "draft" 1m (Seed.at 1)
        let! _ = Seed.order db (Some ada) "draft" 2m (Seed.at 2)
        let! _ = Seed.order db None "draft" 3m (Seed.at 3)

        let joined =
            plasm {
                for o in tableOf<Order> do
                    join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                    where (c.Name = "Ada")
            }

        let! n = db.Repo.Count joined
        Assert.Equal(2L, ok n)

        let groups =
            plasm {
                for o in tableOf<Order> do
                    groupBy o.CustomerId
            }

        let! g = db.Repo.Count groups
        Assert.Equal(2L, ok g)

        let! e = db.Repo.Exists(plasm { for o in tableOf<Order> do orderBy o.Id; skip 3 })
        Assert.False(ok e)
    }

// -- Bulk writes -------------------------------------------------------------

[<Fact>]
let ``a bulk update can clear an optional field and filter through a relationship`` () =
    task {
        let! db = freshDefault ()
        let! paid = Seed.order db None "cancelled" 1m (Seed.at 1)
        let! unpaid = Seed.order db None "cancelled" 1m (Seed.at 2)
        let! _ = db.Exec("update orders set cancellation_reason = 'x'")
        let! _ = Seed.payment db paid 1m "succeeded" (Seed.at 1)

        let withPayments =
            plasm {
                for o in tableOf<Order> do
                    where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) }))
            }

        let bulk = Bulk.update (withPayments, fun o -> { o with CancellationReason = None })
        let sql = (ok (offlineRepo.Explain bulk)).Sql
        contains "\"cancellation_reason\" = NULL" sql
        contains "WHERE EXISTS (SELECT 1 FROM \"payments\" AS \"t1\"" sql
        let! n = db.Repo.Transact(Tx.execute bulk: Tx<_, unit>)
        Assert.Equal(1, ok n)
        let (OrderId p), (OrderId u) = paid, unpaid
        let! a = db.Scalar<obj>("select cancellation_reason from orders where id = @i", [ "i", box p ])
        let! b = db.Scalar<obj>("select cancellation_reason from orders where id = @i", [ "i", box u ])
        Assert.Equal(box DBNull.Value, a)
        Assert.Equal(box "x", b)
    }

[<Fact>]
let ``a bulk delete blocked by a foreign key is a classified failure`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.payment db o 1m "pending" (Seed.at 1)
        let! r = db.Repo.Transact(Tx.execute (Bulk.delete (Query.from (tableOf<Order>))): Tx<_, unit>)

        match r with
        | Error { Failure = TxFailure.Failed(DbError.Database f) } -> Assert.Equal(DbFailureKind.ForeignKeyViolation, f.Kind)
        | other -> failwithf "expected a foreign key violation, got %A" other
    }

[<Fact>]
let ``bulk writes reject versions, relationships and empty changes`` () =
    let all = Query.from (tableOf<Order>)

    let message (b: BulkWrite<int>) = offlineRepo.Explain b |> invalidQuery

    Assert.Contains("version", message (Bulk.update (all, fun o -> { o with Version = 7 })))
    Assert.Contains("owned or linked list", message (Bulk.update (all, fun o -> { o with Lines = [] })))
    Assert.Contains("must return a copy of the row", message (Bulk.update (all, fun o -> o)))
    Assert.Contains("assigns no field", message (Bulk.update (all, fun o -> { o with Total = o.Total })))
    Assert.Contains("joins are not supported", message (Bulk.delete (plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.CustomerId = Some c.Id); where (c.Name = "x") })))

[<Fact>]
let ``an empty bulk insert sends nothing`` () =
    task {
        let! db = freshDefault ()
        db.Recorder.Clear()
        let! r = db.Repo.Transact(Tx.execute (Bulk.insert ([]: Product list)): Tx<_, unit>)
        Assert.Equal<Product list>([], ok r)
        Assert.Equal(0, db.Recorder.Count)
    }

[<Fact>]
let ``a large bulk insert is split into commands inside one transaction`` () =
    task {
        let! db = freshDefault ()
        // Two columns per product: at most 32,500 rows per command.
        let products = [ for i in 1..40_000 -> { Id = ProductId 0L; Sku = $"S{i}"; Name = "n" } ]
        db.Recorder.Clear()
        let! r = db.Repo.Transact(Tx.execute (Bulk.insert products): Tx<_, unit>)
        Assert.Equal(40_000, (ok r).Length)
        Assert.Equal(2, db.Recorder.Events |> List.filter (fun e -> e.Sql.StartsWith "INSERT") |> List.length)

        // A duplicate in the second command rolls back the first.
        let clash = [ for i in 1..40_000 -> { Id = ProductId 0L; Sku = $"T{i}"; Name = "n" } ] @ [ { Id = ProductId 0L; Sku = "S1"; Name = "dup" } ]
        let! failed = db.Repo.Transact(Tx.execute (Bulk.insert clash): Tx<_, unit>)
        Assert.True(Result.isError failed)
        let! n = db.Scalar<int64>("select count(*) from products where sku like 'T%'")
        Assert.Equal(0L, n)
    }

[<Fact>]
let ``upserts accept composite conflict targets and computed updates`` () =
    let bulk =
        Bulk.upsert (
            [ { Id = ProductId 0L; Sku = "A"; Name = "n" } ],
            (fun p -> (p.Sku, p.Name)),
            fun current incoming -> { current with Name = current.Name + incoming.Name }
        )

    let sql = (ok (offlineRepo.Explain bulk)).Sql
    contains "ON CONFLICT (\"sku\", \"name\") DO UPDATE SET \"name\" = \"products\".\"name\" || \"excluded\".\"name\"" sql
    contains "RETURNING \"id\", \"sku\", \"name\"" sql

// -- Hand-written SQL ----------------------------------------------------------

[<Fact>]
let ``hand-written SQL takes lists, options and domain values as parameters`` () =
    task {
        let! db = freshDefault ()
        let! a = Seed.order db None "draft" 1m (Seed.at 1)
        let! b = Seed.order db None "published" 2m (Seed.at 2)
        let! _ = Seed.order db None "published" 3m (Seed.at 3)
        let ids = [ a; b ]
        let status = Some Published
        let! r = db.Repo.Sql<OrderId>($"SELECT id FROM orders WHERE id = ANY({ids}) AND status = {status} ORDER BY id")
        Assert.Equal<OrderId list>([ b ], ok r)
        let! none = db.Repo.Sql<int64 option>($"SELECT max(id) FROM orders WHERE total > {100m}")
        Assert.Equal<int64 option list>([ None ], ok none)
        let! braces = db.Repo.Sql<string>($"SELECT '{{literal}}'")
        Assert.Equal<string list>([ "{literal}" ], ok braces)
    }

[<Fact>]
let ``a tuple with the wrong arity is a materialization error`` () =
    task {
        let! db = freshDefault ()
        let! r = db.Repo.Sql<int * int>($"SELECT 1")

        match r with
        | Error(DbError.Materialization m) -> Assert.Contains("1 columns for a 2-element tuple", m.Message)
        | other -> failwithf "expected a materialization error, got %A" other
    }

[<Fact>]
let ``fragments can be projected and escape percent signs`` () =
    plasm {
        for o in tableOf<Order> do
            select (o.Id, Raw.fragment<int> "{0} %% 7" o.Total)
    }
    |> projectionSql
    |> contains "(\"t0\".\"total\" % 7) AS \"c1\""

// -- Prepared queries ----------------------------------------------------------

[<Fact>]
let ``prepared string patterns are escaped per call`` () =
    task {
        let! db = freshDefault ()
        let! _ = Seed.customer db "50% off" "a@x"
        let! _ = Seed.customer db "500 club" "b@x"

        let named =
            Query.prepare (fun (prefix: Arg<string>) ->
                plasm {
                    for c in tableOf<Customer> do
                        where (c.Name.StartsWith prefix.Value)
                })

        let! r = db.Repo.All(named "50%")
        Assert.Equal<string list>([ "50% off" ], ok r |> List.map (fun c -> c.Name))
        let! r = db.Repo.All(named "500")
        Assert.Equal<string list>([ "500 club" ], ok r |> List.map (fun c -> c.Name))
    }

[<Fact>]
let ``prepared queries run in transactions and with three arguments`` () =
    task {
        let! db = freshDefault ()
        let! a = Seed.order db None "draft" 5m (Seed.at 1)
        let! _ = Seed.order db None "draft" 50m (Seed.at 2)

        let between =
            Query.prepare3 (fun (lo: Arg<decimal>) (hi: Arg<decimal>) (status: Arg<OrderStatus>) ->
                plasm {
                    for o in tableOf<Order> do
                        where (o.Total >= lo.Value && o.Total <= hi.Value && o.Status = status.Value)
                })

        let! r = db.Repo.Transact(Tx.all (between 0m 10m Draft))
        Assert.Equal<OrderId list>([ a ], ok r |> List.map (fun o -> o.Id))
        let! one = db.Repo.One(between 0m 10m Draft)
        Assert.Equal(Some a, ok one |> Option.map (fun o -> o.Id))
    }

[<Fact>]
let ``prepared paging rejects negative counts when bound`` () =
    let page =
        Query.prepare (fun (n: Arg<int>) ->
            plasm {
                for o in tableOf<Order> do
                    orderBy o.Id
                    take n.Value
            })

    Assert.Throws<ArgumentException>(fun () -> page -1 |> ignore) |> ignore

[<Fact>]
let ``take accepts computed counts and rejects expressions of arguments`` () =
    let size, pageNo = 10, 3

    plasm {
        for o in tableOf<Order> do
            orderBy o.Id
            take (size * pageNo)
    }
    |> sqlOf
    |> contains "LIMIT @p0"

    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            Query.prepare (fun (n: Arg<int>) ->
                plasm {
                    for o in tableOf<Order> do
                        orderBy o.Id
                        take (n.Value * 2)
                })
            |> ignore)

    Assert.Contains("takes an argument directly", ex.Message)

[<Fact>]
let ``expressions of arguments are compiled into bound parameters`` () =
    task {
        let! db = freshDefault ()
        let! _ = Seed.order db None "draft" 5m (Seed.at 1)
        let! big = Seed.order db None "draft" 50m (Seed.at 2)

        let over =
            Query.prepare (fun (n: Arg<int>) ->
                plasm {
                    for o in tableOf<Order> do
                        where (o.Total > decimal (n.Value * 10))
                })

        contains "WHERE \"t0\".\"total\" > @p0" (sqlOf (over 1))
        let! r = db.Repo.All(over 1)
        Assert.Equal<OrderId list>([ big ], ok r |> List.map (fun o -> o.Id))
    }

// -- Batch 2b: many-to-many, schema check, sandbox -------------------------------

module BlankJoin =
    [<Table "articles">]
    type ArticleBlankJoin =
        { Id: ArticleId
          Title: string
          [<JoinTable "">]
          Tags: Tag list }

[<Fact>]
let ``links requires a join table name`` () =
    // An empty join table name is a configuration error naming the list.
    let r =
        Model.build Schemas.scalars [ typeof<BlankJoin.ArticleBlankJoin>; typeof<Tag> ]

    match r with
    | Error es -> Assert.Contains(es, fun e -> e.Property = Some "Tags" && e.Message.Contains "join table")
    | Ok _ -> failwith "expected a configuration error"

[<Fact>]
let ``linked lists are written as join-table rows by difference`` () =
    task {
        let! db = freshDefault ()
        let! a = db.Scalar<int64>("insert into articles(title) values ('A') returning id")
        let! t = db.Scalar<int64>("insert into tags(name) values ('x') returning id")
        let tag = { Id = TagId t; Name = "x" }
        let article = { Id = ArticleId a; Title = "A"; Tags = [] }
        let write (tx: Tx<Article, unit>) = db.Repo.Transact tx

        // The same Tag twice is one link.
        let! r = write (Tx.update (article, { article with Tags = [ tag; tag ] }))
        let linked = ok r
        let! n = db.Scalar<int64>("select count(*) from article_tags")
        Assert.Equal(1L, n)

        // An empty list clears the links.
        let! cleared = write (Tx.update (linked, { linked with Tags = [] }))
        Assert.Equal<Tag list>([], (ok cleared).Tags)
        let! n = db.Scalar<int64>("select count(*) from article_tags")
        Assert.Equal(0L, n)
    }

[<Fact>]
let ``a links-only update of an identity table without a version sends no row update`` () =
    task {
        let! db = freshDefault ()
        let! a = db.Scalar<int64>("insert into articles(title) values ('A') returning id")
        let! t = db.Scalar<int64>("insert into tags(name) values ('x') returning id")
        db.Recorder.Clear()
        let article = { Id = ArticleId a; Title = "A"; Tags = [] }
        let! r = db.Repo.Transact(Tx.update (article, { article with Tags = [ { Id = TagId t; Name = "x" } ] }): Tx<Article, unit>)
        ok r |> ignore
        Assert.DoesNotContain(db.Recorder.Events, fun e -> e.Sql.StartsWith "UPDATE")
    }

[<Fact>]
let ``a failed write inside a sandbox transaction step leaves the sandbox usable`` () =
    task {
        let! db = freshDefault ()

        let! names =
            db.Repo.Sandbox(fun repo ->
                task {
                    let! _ = repo.Transact(Tx.insert (New.customer "Ada" "a@x"): Tx<_, unit>)

                    let! failed =
                        repo.Transact(
                            transaction {
                                let! _ = Tx.insert (New.customer "Dup" "a@x")
                                return ()
                            }
                        )

                    Assert.True(Result.isError failed)
                    let! all = repo.All(Query.from (tableOf<Customer>))
                    return ok all |> List.map (fun c -> c.Name)
                })

        Assert.Equal<string list>([ "Ada" ], names)
        let! n = db.Scalar<int64>("select count(*) from customers")
        Assert.Equal(0L, n)
    }

[<Fact>]
let ``the schema check reports a type mismatch on a join table column`` () =
    task {
        let! db = freshDefault ()
        let! _ = db.Exec("alter table article_tags alter column tag_id type integer")
        let! r = db.Repo.CheckSchema()
        let issues = ok r
        Assert.Contains(issues, fun i -> i.Kind = SchemaIssueKind.IncompatibleType && i.Column = Some "tag_id" && i.Field = Some "Tags")
    }
