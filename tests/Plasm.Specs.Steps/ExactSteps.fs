namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A union with a decimal in a case, for the exact hooks' scenarios: only
/// explained, so it has no table.
module ExactUnion =
    type Pricing =
        | Fixed of amount: decimal
        | Free

    type ListingId = ListingId of int64

    [<Table "listings">]
    type Listing = { Id: ListingId; Price: Pricing }

    let model = Model.buildOrFail [] [ typeof<Listing> ]

/// A model keyed by a decimal, which a dialect with exact hooks refuses.
module DecimalKeyed =
    type RateId = RateId of decimal

    [<Table "rates">]
    type Rate = { Id: RateId; Name: string }

    let model = Model.buildOrFail [] [ typeof<Rate> ]

/// Feature: Database-specific SQL comes from the provider (decimals read by
/// value as the dialect's exact hooks read them).
[<Binding>]
type ExactSteps(w: World) =

    /// The dialect whose decimals are exact by its own hooks: on PostgreSQL,
    /// PostgreSQL's with marked hooks of its own; on SQLite, Plasm.Sqlite's.
    let exact () : IProvider =
        match w.Db.Kind with
        | SQLite -> w.Db.Provider
        | PostgreSQL -> ExactDecimalsProvider(w.Db.Provider)

    let events () = w.Get<ResizeArray<CommandEvent>> "exactEvents"
    let repo () = w.Get<Repo> "exactRepo"

    /// The statement that read the Orders (or their aggregates).
    let ordersSql () =
        events () |> Seq.map (fun e -> e.Sql) |> Seq.find (fun s -> s.Contains "FROM \"orders\"")

    let clause (keyword: string) (sql: string) =
        let i = sql.IndexOf keyword
        check (i >= 0) $"no {keyword}: {sql}"
        let rest = sql.Substring(i + keyword.Length)
        let ends = [ "\nORDER BY"; "\nLIMIT"; "\nOFFSET" ] |> List.map rest.IndexOf |> List.filter (fun j -> j >= 0)
        if ends.IsEmpty then rest else rest.Substring(0, List.min ends)

    let count (part: string) (s: string) = (s.Length - s.Replace(part, "").Length) / part.Length

    let query (q: Query<Order>) =
        task {
            events().Clear()
            let! r = repo().All q
            w.Set("exactFound", ok r |> List.map (fun o -> o.Total))
        }
        :> Task

    [<StepDefinition("Orders totalling 2.00, 9.50 and 10.10, through a dialect whose decimals are exact by its own hooks")>]
    member _.Orders() =
        task {
            for total in [ 2.00m; 9.50m; 10.10m ] do
                let! _ = Seed.order w.Db None "published" total (Seed.at 0)
                ()

            let events = ResizeArray<CommandEvent>()
            w.Set("exactEvents", events)

            w.Set(
                "exactRepo",
                Repo(Schemas.model, exact (), { RepoOptions.Default with OnCommand = Some events.Add; IncludeParameterValues = true })
            )
        }
        :> Task

    [<StepDefinition("I query through it the Orders whose total is more than 9.4 and not less than 2.0, whose id is not 0")>]
    member _.Compared() =
        query (plasm { for o in tableOf<Order> do where (o.Total > 9.4m && not (o.Total < 2.0m) && o.Id <> OrderId 0L); orderBy o.Id })

    [<StepDefinition("I query through it the Orders whose total is not 2.0")>]
    member _.NotEqual() =
        query (plasm { for o in tableOf<Order> do where (o.Total <> 2.0m); orderBy o.Id })

    [<StepDefinition("I query through it the Orders by total, then by id")>]
    member _.Ordered() =
        query (plasm { for o in tableOf<Order> do orderBy o.Total; thenBy o.Id })

    [<StepDefinition("I query through it the Orders whose total doubled is more than 19.5")>]
    member _.Doubled() =
        query (plasm { for o in tableOf<Order> do where (o.Total * 2m > 19.5m); orderBy o.Id })

    [<StepDefinition("I query through it the Orders whose total is 9.50")>]
    member _.Equal() =
        query (plasm { for o in tableOf<Order> do where (o.Total = 9.50m); orderBy o.Id })

    [<StepDefinition("each comparison of a total is the dialect's exact comparison, and the id's is as it is")>]
    member _.ComparedExactly() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "\"total\" AS numeric) > CAST(" && where.Contains "\"total\" AS numeric) >= CAST(") where
        check (count "/* exact */" where = 2 && count "\"total\"" where = 2) where
        check (where.Contains "\"id\" <> @p" && not (where.Contains "\"id\" AS numeric")) where

    [<StepDefinition("the comparison of the total is the dialect's exact comparison, not equal")>]
    member _.NotEqualExactly() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "\"total\" AS numeric) <> CAST(" && count "/* exact */" where = 1) where

    [<StepDefinition("the order by total is the dialect's exact order, and the order by id is as it is")>]
    member _.OrderedExactly() =
        let order = (ordersSql ()).Split("ORDER BY ").[1]
        check (order.StartsWith "\"o\".\"total\" /* exact */, \"o\".\"id\"") order

    [<StepDefinition("the doubling is the dialect's exact arithmetic, compared by its exact comparison")>]
    member _.DoubledExactly() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "CAST((CAST(\"o\".\"total\" AS numeric) * CAST(" && count "/* exact */" where = 2) where

    [<StepDefinition("the total is sent as 9.50000")>]
    member _.Sent() =
        let e = events () |> Seq.find (fun e -> e.Sql.Contains "FROM \"orders\"")
        check (e.Parameters |> List.exists (fun (_, v) -> v = Some "9.50000")) $"%A{e.Parameters}"

    [<StepDefinition("^the Orders found total (.*)$")>]
    member _.Found(totals: string) =
        let expected = totals.Replace(" and ", ", ").Split(", ") |> Array.map (fun t -> Decimal.Parse(t, Globalization.CultureInfo.InvariantCulture)) |> List.ofArray
        let found: decimal list = w.Get "exactFound"
        equal expected found "the totals found"

    [<StepDefinition("I select through it the count, sum, minimum, maximum and average of the Order totals")>]
    member _.Aggregates() =
        task {
            events().Clear()
            let! r = repo().One(plasm { for o in tableOf<Order> do select (Agg.count (), Agg.sum o.Total, Agg.min o.Total, Agg.max o.Total, Agg.avg o.Total) })
            w.Set("exactAggregates", (ok r).Value)
        }
        :> Task

    [<StepDefinition("the sum, minimum, maximum and average are the dialect's exact aggregates, and the count is as it is")>]
    member _.AggregatedExactly() =
        let sql = ordersSql ()
        let select = sql.Substring(0, sql.IndexOf "FROM")

        for name in [ "SUM"; "MIN"; "MAX"; "AVG" ] do
            check (select.Contains $"{name}(CAST(\"o\".\"total\" AS numeric) /* exact */)") select

        check (select.Contains "COUNT(*)" && count "/* exact */" select = 4) select

    [<StepDefinition("they are 3, 21.60, 2.00, 10.10 and 7.20")>]
    member _.AggregateValues() =
        let (n: int64), (sum: decimal), (min: decimal option), (max: decimal option), (avg: decimal option) = w.Get "exactAggregates"
        equal (3L, 21.60m, Some 2.00m, Some 10.10m) (n, sum, min, max) "count, sum, minimum and maximum"
        equal 7.20m (Math.Round(avg.Value, 2)) "the average"

    [<StepDefinition("I count through it the Orders of each total, with a fourth of 2.0")>]
    member _.Grouped() =
        task {
            let! _ = Seed.order w.Db None "published" 2.0m (Seed.at 0)
            events().Clear()
            let! r = repo().All(plasm { for o in tableOf<Order> do groupBy o.Total; orderBy o.Total; select (o.Total, Agg.count ()) })
            w.Set("exactGroups", ok r |> List.map (fun (t: decimal, n: int64) -> t, n))
        }
        :> Task

    [<StepDefinition("the group key is the dialect's exact group key")>]
    member _.GroupedExactly() =
        let sql = ordersSql ()
        check (sql.Contains "GROUP BY \"o\".\"total\" /* exact */") sql

    [<StepDefinition("there are three groups, the first of two Orders")>]
    member _.Groups() =
        let groups: (decimal * int64) list = w.Get "exactGroups"
        equal [ 2m, 2L; 9.50m, 1L; 10.10m, 1L ] groups "the groups"

    [<StepDefinition("I join through it the Orders to the Payments of their total, a Payment of 2.0 for the first")>]
    member _.Joined() =
        task {
            let! first = w.Db.Scalar<int64>("select min(id) from orders")
            let! _ = Seed.payment w.Db (OrderId first) 2.0m "pending" (Seed.at 0)
            events().Clear()

            let! r =
                repo().All(
                    plasm {
                        for o in tableOf<Order> do
                            join p in tableOf<Payment> on (o.Total = p.Amount)
                            select (o.Total, p.Amount)
                    }
                )

            w.Set("exactJoined", ok r)
        }
        :> Task

    [<StepDefinition("the join compares the total and the amount by the dialect's exact comparison")>]
    member _.JoinedExactly() =
        let sql = ordersSql ()
        let on = sql.Substring(sql.IndexOf " ON ")
        check (on.StartsWith " ON CAST(\"o\".\"total\" AS numeric) = CAST(\"p\".\"amount\" AS numeric) /* exact */") sql

    [<StepDefinition("the Order of 2.00 is joined to the Payment of 2.0, and no other")>]
    member _.JoinedPair() =
        let joined: (decimal * decimal) list = w.Get "exactJoined"
        equal [ 2m, 2m ] joined "the Orders and Payments joined"

    [<StepDefinition("I number through it the Orders within each total, with a fourth of 2.0")>]
    member _.Partitioned() =
        task {
            let! _ = Seed.order w.Db None "published" 2.0m (Seed.at 0)
            events().Clear()

            let! r =
                repo().All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.Id
                            select (o.Total, Win.rowNumber (Win.partition o.Total |> Win.orderBy o.Id))
                    }
                )

            w.Set("exactNumbered", ok r |> List.map (fun (t: decimal, n: int64) -> t, n))
        }
        :> Task

    [<StepDefinition("the partition key is the dialect's exact group key")>]
    member _.PartitionedExactly() =
        let sql = ordersSql ()
        check (sql.Contains "PARTITION BY \"o\".\"total\" /* exact */ ORDER BY") sql

    [<StepDefinition("the Orders are numbered 1, 1, 1 and 2, the two of 2 in one partition")>]
    member _.Numbered() =
        let numbered: (decimal * int64) list = w.Get "exactNumbered"
        equal [ 2m, 1L; 9.50m, 1L; 10.10m, 1L; 2m, 2L ] numbered "the Orders numbered"

    [<StepDefinition("I explain through it a query of the customers at a place, a value of two decimals")>]
    member _.AtPlace() =
        let values = Repo(Plasm.Specs.ValueRecords.Schemas.model, exact ())
        let place: Plasm.Specs.ValueRecords.Geo = { Lat = 51.5m; Lon = -0.1m }

        let plan =
            values.Explain(plasm { for c in tableOf<Plasm.Specs.ValueRecords.Customer> do where (c.Address.Geo = place) })
            |> ok

        w.Set("exactPlan", plan.Sql)

    [<StepDefinition("each part of the value is compared by the dialect's exact comparison")>]
    member _.PartsExactly() =
        let where = clause "WHERE" (w.Get<string> "exactPlan")
        check (where.Contains "CAST(\"c\".\"address_geo_lat\" AS numeric) = CAST(" && where.Contains "CAST(\"c\".\"address_geo_lon\" AS numeric) = CAST(") where
        check (count "/* exact */" where = 2) where

    [<StepDefinition("I explain through it a query of the listings at a fixed price, a union case holding a decimal")>]
    member _.FixedPrice() =
        let listings = Repo(ExactUnion.model, exact ())

        let plan =
            listings.Explain(plasm { for l in tableOf<ExactUnion.Listing> do where (l.Price = ExactUnion.Fixed 2.0m) })
            |> ok

        w.Set("exactPlan", plan.Sql)

    [<StepDefinition("the case's decimal is compared by the dialect's exact comparison, and its tag as it is")>]
    member _.CaseExactly() =
        let where = clause "WHERE" (w.Get<string> "exactPlan")
        check (where.Contains "AS numeric) = CAST(" && count "/* exact */" where = 1) where
        check (count " = " where = 2) where

    [<StepDefinition("I query through it the Orders whose total is among 2.0 and 10.1")>]
    member _.Among() =
        let totals = [ 2.0m; 10.1m ]
        query (plasm { for o in tableOf<Order> do where (List.contains o.Total totals); orderBy o.Id })

    [<StepDefinition("the total is the operand of the list as the dialect's exact order takes it")>]
    member _.AmongExactly() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "\"o\".\"total\" /* exact */ = ANY(" && count "/* exact */" where = 1) where

    [<StepDefinition("I select through it the distinct Order totals, with a fourth of 2.0")>]
    member _.Distinct() =
        task {
            let! _ = Seed.order w.Db None "published" 2.0m (Seed.at 0)
            events().Clear()
            let! r = repo().All(plasm { for o in tableOf<Order> do selectDistinct o.Total })
            w.Set("exactDistinct", ok r |> List.sort)
        }
        :> Task

    [<StepDefinition("the total is told apart as the dialect's exact order takes it")>]
    member _.DistinctExactly() =
        let sql = ordersSql ()
        check (sql.StartsWith "SELECT DISTINCT \"o\".\"total\" /* exact */" && count "/* exact */" sql = 1) sql

    [<StepDefinition("there are three totals, 2, 9.50 and 10.10")>]
    member _.DistinctTotals() =
        equal [ 2m; 9.50m; 10.10m ] (w.Get<decimal list> "exactDistinct") "the distinct totals"

    [<StepDefinition("I read through it the union of the Order totals and the Payment amounts, a Payment of 2.0 for the first")>]
    member _.Union() =
        task {
            let! first = w.Db.Scalar<int64>("select min(id) from orders")
            let! _ = Seed.payment w.Db (OrderId first) 2.0m "pending" (Seed.at 0)
            events().Clear()
            let totals = plasm { for o in tableOf<Order> do select o.Total }
            let amounts = plasm { for p in tableOf<Payment> do select p.Amount }
            let! r = repo().All(Query.union (totals, amounts))
            w.Set("exactUnion", ok r |> List.sort)
        }
        :> Task

    [<StepDefinition("each operand's total or amount is told apart as the dialect's exact order takes it")>]
    member _.UnionExactly() =
        let sql = ordersSql ()
        check (sql.Contains "\"o\".\"total\" /* exact */" && sql.Contains "\"p\".\"amount\" /* exact */" && sql.Contains "UNION") sql

    [<StepDefinition("there are three values, 2, 9.50 and 10.10")>]
    member _.UnionValues() =
        equal [ 2m; 9.50m; 10.10m ] (w.Get<decimal list> "exactUnion") "the values of the union"

    [<StepDefinition("I select through it each Order's running sum of totals, by id")>]
    member _.RunningSum() =
        task {
            events().Clear()
            let! r = repo().All(plasm { for o in tableOf<Order> do orderBy o.Id; select (Win.runningSum o.Total (Win.whole |> Win.orderBy o.Id)) })
            w.Set("exactRunning", ok r)
        }
        :> Task

    [<StepDefinition("the running sum is the dialect's exact aggregate over the window")>]
    member _.RunningExactly() =
        let sql = ordersSql ()
        check (sql.Contains "SUM(CAST(\"o\".\"total\" AS numeric) /* exact */) OVER (ORDER BY \"o\".\"id\"" && count "/* exact */" sql = 1) sql

    [<StepDefinition("the running sums are 2.00, 11.50 and 21.60")>]
    member _.RunningValues() =
        equal [ 2.00m; 11.50m; 21.60m ] (w.Get<decimal list> "exactRunning") "the running sums"

    [<StepDefinition("a repository is made through it for a model keyed by a decimal")>]
    member _.MakeDecimalKeyed() =
        try
            Repo(DecimalKeyed.model, exact ()) |> ignore
            w.Set("exactMade", "made")
        with :? PlasmConfigurationException as e ->
            w.Set("exactMade", e.Message)

    [<StepDefinition("it is refused when made, naming Rate.Id and saying a key of 2.0 and one of 2.00 would be two")>]
    member _.DecimalKeyRefused() =
        let m = w.Get<string> "exactMade"
        check (m.Contains "Rate.Id" && m.Contains "2.0 and 2.00 would be two keys") m

    [<StepDefinition("a repository is made for it on PostgreSQL, whose numeric keys compare by value")>]
    member _.MakeDecimalKeyedPostgres() =
        Repo(DecimalKeyed.model, w.Db.Provider) |> ignore

    // On SQLite: Plasm.Sqlite's own SQL, its decimal functions and collation.

    [<StepDefinition("each comparison of a total is SQLite's plasm_decimal_compare, and the id's is as it is")>]
    member _.ComparedSqlite() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "plasm_decimal_compare(\"o\".\"total\", @p0) > 0" && where.Contains "plasm_decimal_compare(\"o\".\"total\", @p1) >= 0") where
        check (where.Contains "\"o\".\"id\" <> @p2" && count "plasm_decimal_compare" where = 2) where

    [<StepDefinition("the comparison of the total is SQLite's plasm_decimal_compare, not equal")>]
    member _.NotEqualSqlite() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "plasm_decimal_compare(\"o\".\"total\", @p0) <> 0") where

    [<StepDefinition("the order by total is in SQLite's PLASM_DECIMAL collation, and the order by id is as it is")>]
    member _.OrderedSqlite() =
        let order = (ordersSql ()).Split("ORDER BY ").[1]
        check (order.StartsWith "\"o\".\"total\" COLLATE PLASM_DECIMAL, \"o\".\"id\"") order

    [<StepDefinition("the group key is the total in SQLite's PLASM_DECIMAL collation")>]
    member _.GroupedSqlite() =
        let sql = ordersSql ()
        check (sql.Contains "GROUP BY \"o\".\"total\" COLLATE PLASM_DECIMAL") sql

    [<StepDefinition("the join compares the total and the amount by SQLite's plasm_decimal_compare")>]
    member _.JoinedSqlite() =
        let sql = ordersSql ()
        check (sql.Contains " ON plasm_decimal_compare(\"o\".\"total\", \"p\".\"amount\") = 0") sql

    [<StepDefinition("the partition key is the total in SQLite's PLASM_DECIMAL collation")>]
    member _.PartitionedSqlite() =
        let sql = ordersSql ()
        check (sql.Contains "PARTITION BY \"o\".\"total\" COLLATE PLASM_DECIMAL ORDER BY") sql

    [<StepDefinition("each part of the value is compared by SQLite's plasm_decimal_compare")>]
    member _.PartsSqlite() =
        let where = clause "WHERE" (w.Get<string> "exactPlan")
        check (where.Contains "plasm_decimal_compare(\"c\".\"address_geo_lat\", @p0) = 0" && where.Contains "plasm_decimal_compare(\"c\".\"address_geo_lon\", @p1) = 0") where

    [<StepDefinition("the case's decimal is compared by SQLite's plasm_decimal_compare, and its tag as it is")>]
    member _.CaseSqlite() =
        let where = clause "WHERE" (w.Get<string> "exactPlan")
        check (where.Contains "\"l\".\"price\" = @p0" && where.Contains "plasm_decimal_compare(\"l\".\"price_amount\", @p1) = 0") where

    [<StepDefinition("the total is the operand of the list in SQLite's PLASM_DECIMAL collation")>]
    member _.AmongSqlite() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "\"o\".\"total\" COLLATE PLASM_DECIMAL IN (") where

    [<StepDefinition("the total is told apart in SQLite's PLASM_DECIMAL collation")>]
    member _.DistinctSqlite() =
        let sql = ordersSql ()
        check (sql.StartsWith "SELECT DISTINCT \"o\".\"total\" COLLATE PLASM_DECIMAL") sql

    [<StepDefinition("each operand's total or amount is told apart in SQLite's PLASM_DECIMAL collation")>]
    member _.UnionSqlite() =
        let sql = ordersSql ()
        check (sql.Contains "\"o\".\"total\" COLLATE PLASM_DECIMAL" && sql.Contains "\"p\".\"amount\" COLLATE PLASM_DECIMAL" && sql.Contains "UNION") sql

    [<StepDefinition("the running sum is SQLite's json_group_array over the window, summed by plasm_decimal_json_sum")>]
    member _.RunningSqlite() =
        let sql = ordersSql ()
        check (sql.Contains "plasm_decimal_json_sum(json_group_array(\"o\".\"total\") OVER (ORDER BY \"o\".\"id\" ROWS UNBOUNDED PRECEDING))") sql

    [<StepDefinition("the doubling is SQLite's plasm_decimal_multiply, compared by its plasm_decimal_compare")>]
    member _.DoubledSqlite() =
        let where = clause "WHERE" (ordersSql ())
        check (where.Contains "plasm_decimal_compare(plasm_decimal_multiply(\"o\".\"total\", @p0), @p1) > 0") where

    [<StepDefinition("the sum and average are SQLite's plasm_decimal_sum and plasm_decimal_avg, the minimum and maximum in its PLASM_DECIMAL collation, and the count is as it is")>]
    member _.AggregatedSqlite() =
        let sql = ordersSql ()
        let select = sql.Substring(0, sql.IndexOf "FROM")

        for part in [ "plasm_decimal_sum(\"o\".\"total\")"; "plasm_decimal_avg(\"o\".\"total\")"; "MIN(\"o\".\"total\" COLLATE PLASM_DECIMAL)"; "MAX(\"o\".\"total\" COLLATE PLASM_DECIMAL)"; "COUNT(*)" ] do
            check (select.Contains part) select

    [<StepDefinition("the total is sent as the text 9.50")>]
    member _.SentSqlite() =
        let e = events () |> Seq.find (fun e -> e.Sql.Contains "FROM \"orders\"")
        check (e.Parameters |> List.exists (fun (_, v) -> v = Some "9.50")) $"%A{e.Parameters}"

    [<StepDefinition("the UPDATE compares the amount it was read with by SQLite's plasm_decimal_same, and every other column as it is")>]
    member _.UpdatedSqlite() =
        let update = events () |> Seq.map (fun e -> e.Sql) |> Seq.find (fun s -> s.StartsWith "UPDATE")
        let where = update.Substring(update.IndexOf "WHERE")
        check (where.Contains "plasm_decimal_same(\"payments\".\"amount\", @p" && count "plasm_decimal_same" where = 1) update
        check (where.Contains "\"payments\".\"status\" IS @p") update

    [<StepDefinition("a Payment of 12.50 for the first Order")>]
    member _.Payment() =
        task {
            let! orders = w.Db.Scalar<int64>("select min(id) from orders")
            let! _ = Seed.payment w.Db (OrderId orders) 12.50m "pending" (Seed.at 0)
            ()
        }
        :> Task

    [<StepDefinition("the Payment read through it is marked succeeded")>]
    member _.MarkSucceeded() =
        task {
            let! read = repo().One(plasm { for p in tableOf<Payment> do where (p.Status = Pending) })
            let payment = (ok read).Value
            events().Clear()
            let! marked = repo().Transact(Tx.update (payment, { payment with Status = Succeeded }): Tx<Payment, unit>)
            equal Succeeded (ok marked).Status "the marked Payment"
        }
        :> Task

    [<StepDefinition("the UPDATE compares the amount it was read with by the dialect's exact null-safe comparison, and every other column as it is")>]
    member _.UpdatedExactly() =
        let update = events () |> Seq.map (fun e -> e.Sql) |> Seq.find (fun s -> s.StartsWith "UPDATE")
        let where = update.Substring(update.IndexOf "WHERE")
        check (where.Contains "CAST(\"payments\".\"amount\" AS numeric) IS NOT DISTINCT FROM CAST(" && count "/* exact */" where = 1) update
        check (where.Contains "\"payments\".\"status\" IS NOT DISTINCT FROM") update
