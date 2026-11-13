/// `dotnet run -- sql`: runs every website example against a throwaway database,
/// records the SQL Plasm actually sends, and writes `website/sql.js`.
///
/// Each example declares the fragments that make it distinctive. They must occur
/// in the captured SQL, or generation fails, so the site cannot show highlights
/// the library no longer produces.
module Shop.SqlCapture

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Plasm
open Plasm.Postgres
open Shop.Model
open Shop.Queries
open Shop.Writes

type Command =
    { Sql: string
      Parameters: string list
      Rows: int }

type Example =
    { Id: string
      Caption: string
      Commands: Command list
      Marks: string list
      Empty: string option }

/// Reformats whitespace only: top-level clauses start new lines and long
/// select lists put one column per line. Text inside parentheses is untouched.
let format (sql: string) =
    let clauses =
        [ " FROM "; " INNER JOIN "; " LEFT JOIN "; " WHERE "; " GROUP BY "; " HAVING "; " ORDER BY "; " LIMIT "; " OFFSET "; " RETURNING "; " ON CONFLICT "; " SET "; " VALUES "; " FOR UPDATE" ]

    let sb = StringBuilder()
    let mutable depth = 0
    let mutable inQuote = false
    let mutable i = 0
    let isSelect = sql.StartsWith "SELECT "
    let fromAt = sql.IndexOf " FROM "

    let selectColumns =
        if isSelect && fromAt > 0 then
            let list = sql.Substring(7, fromAt - 7)
            // count top-level commas
            let mutable d = 0
            let mutable n = 1

            for c in list do
                if c = '(' then d <- d + 1
                elif c = ')' then d <- d - 1
                elif c = ',' && d = 0 then n <- n + 1

            n
        else
            0

    let oneColumnPerLine = selectColumns > 4

    if oneColumnPerLine then
        sb.Append("SELECT\n    ") |> ignore
        i <- 7

    while i < sql.Length do
        let c = sql.[i]

        if c = '"' then
            inQuote <- not inQuote
            sb.Append c |> ignore
            i <- i + 1
        elif inQuote then
            sb.Append c |> ignore
            i <- i + 1
        else
            if c = '(' then depth <- depth + 1
            if c = ')' then depth <- depth - 1

            let clause =
                if depth = 0 then
                    clauses |> List.tryFind (fun k -> String.CompareOrdinal(sql, i, k, 0, k.Length) = 0)
                else
                    None

            match clause with
            | Some k ->
                sb.Append('\n').Append(k.TrimStart()) |> ignore
                i <- i + k.Length
            | None when oneColumnPerLine && c = ',' && depth = 0 && i < fromAt ->
                sb.Append(",\n    ") |> ignore
                i <- i + 2
            | None ->
                sb.Append c |> ignore
                i <- i + 1

    // `DELETE FROM` reads as one clause.
    sb.ToString().Replace("DELETE\nFROM ", "DELETE FROM ")

let run (connectionString: string) (outputPath: string) =
    task {
        let events = ResizeArray<CommandEvent>()

        let repo =
            Repo(
                Db.model,
                Postgres.connect connectionString,
                { OnCommand = Some events.Add
                  IncludeParameterValues = true }
            )

        let examples = ResizeArray<Example>()

        let capture id caption (marks: string list) (empty: string option) (work: unit -> System.Threading.Tasks.Task<unit>) =
            task {
                events.Clear()
                do! work ()

                let commands =
                    events
                    |> Seq.map (fun e ->
                        { Sql = format e.Sql
                          Parameters = e.Parameters |> List.map (fun (n, v) -> $"""{n} = {defaultArg v "?"}""")
                          Rows = e.Rows })
                    |> List.ofSeq

                for m in marks do
                    if not (commands |> List.exists (fun c -> c.Sql.Contains m)) then
                        let sent = commands |> List.map (fun c -> c.Sql + "  -- " + String.concat ", " c.Parameters + $" -> {c.Rows} rows") |> String.concat "\n\n"
                        failwith $"example '{id}': highlight not found in the generated SQL: {m}\n\n{sent}"

                examples.Add
                    { Id = id
                      Caption = caption
                      Commands = commands
                      Marks = marks
                      Empty = empty }
            }

        let ok r =
            match r with
            | Ok v -> v
            | Error e -> failwithf "unexpected error: %A" e

        // -- Setup (not captured) ---------------------------------------------
        let! widget = repo.Insert(Changeset.insert { Id = ProductId 0L; Sku = "W-1"; Name = "Widget" })
        let widget = ok widget
        let! gadget = repo.Insert(Changeset.insert { Id = ProductId 0L; Sku = "G-1"; Name = "Gadget" })
        let gadget = ok gadget

        // 9 · Transactions first: it creates the customer and the first order.
        let mutable ada = Unchecked.defaultof<Customer>
        let mutable firstOrder = Unchecked.defaultof<Order>

        do!
            capture
                "transactions"
                "Three named steps, one database transaction. Each insert returns the generated key, and the next step uses it as a parameter."
                [ "INSERT INTO \"customers\""; "INSERT INTO \"orders\""; "INSERT INTO \"order_lines\""; "RETURNING" ]
                None
                (fun () ->
                    task {
                        let! r = repo.Transact(checkout "Ada" "ada@example.com" widget.Id)
                        let c, o = ok r
                        ada <- c
                        firstOrder <- o
                    })

        for amount, status, minutes in [ 19.98m, Succeeded, 5; 5m, Failed, 1; 2m, Pending, 3 ] do
            let! _ =
                repo.Insert(
                    Changeset.insert
                        { Id = PaymentId 0L
                          OrderId = firstOrder.Id
                          Amount = amount
                          Status = status
                          CreatedAt = DateTime.UtcNow.AddMinutes(float minutes) }
                )

            ()

        // A second order for Ada with no successful payment, so "overdue" finds one.
        let! second = repo.Insert(placeOrder ada.Id [ (newLine gadget.Id 1 3m |> Result.toOption).Value ])
        let second = ok second

        // An order nobody has claimed yet, so the left join has a None to show.
        let! _ = repo.Insert(Changeset.insert { newOrder ada.Id with CustomerId = None; Total = 4m })

        // 1 · Model
        do!
            capture
                "model"
                "Field names become snake_case columns, and every mapped field is read. Relationship fields are not columns: they stay NotLoaded until requested."
                [ "\"customer_id\""; "\"cancellation_reason\""; "\"placed_at\"" ]
                None
                (fun () -> task { let! _ = repo.All(Query.from Db.orders) in () })

        // 2 · Scalars
        do!
            capture
                "scalars"
                "OrderId 1L is sent as the int64 1, and Draft as the text 'draft', using the mappings you registered. The parameter values are listed below the statement."
                [ "\"t0\".\"id\" = @p0"; "\"t0\".\"status\" = @p1" ]
                None
                (fun () -> task { let! _ = repo.All(lookup firstOrder.Id) in () })

        // 3 · Queries
        do!
            capture
                "queries"
                "Both functions' filters reach the database. The relationship predicate is a correlated NOT EXISTS, so each order appears at most once."
                [ "\"t0\".\"customer_id\" = @p0"; "NOT EXISTS" ]
                None
                (fun () -> task { let! _ = repo.All(overdue ada.Id) in () })

        // 11 · Joins
        do!
            capture
                "joins"
                "Each join is part of one command. The inner join drops the order without a customer; the left join keeps it, and its customer columns read as NULL, which becomes None. Chained joins add one JOIN each, and the projection reads only the three columns it names."
                [ "INNER JOIN \"customers\" AS \"j1\" ON (\"t0\".\"customer_id\" = \"j1\".\"id\")"
                  "LEFT JOIN \"customers\" AS \"j1\""
                  "\"j1\".\"email\" AS \"c1\""
                  "INNER JOIN \"products\" AS \"j2\" ON (\"j1\".\"product_id\" = \"j2\".\"id\")" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All ordersWithCustomers
                        let! _ = repo.All customerEmails
                        let! _ = repo.All soldProducts
                        ()
                    })

        // 4 · Loads
        do!
            capture
                "loads"
                "The page is chosen first (ORDER BY with the id tiebreaker, LIMIT, OFFSET). Each relationship then gets the selected keys as one array parameter, and the payments filter and ordering run in SQL. The last command is the per-parent limit, a window function partitioned by order."
                [ "LIMIT @p1"
                  "OFFSET @p2"
                  "\"t0\".\"id\" ASC"
                  "\"t0\".\"order_id\" = ANY(@p0)"
                  "\"t0\".\"id\" = ANY(@p0)"
                  "\"t0\".\"status\" = @p1"
                  "ROW_NUMBER() OVER (PARTITION BY \"t0\".\"order_id\""
                  "\"s\".\"__rank\" <= @p1" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All(Query.from Db.orders |> forCustomer ada.Id |> orderPage 0 20)
                        let! _ = repo.All(recentPayments 3)
                        ()
                    })

        // 5 · Existing values
        let! held = repo.All(Query.from Db.orders)
        let held = ok held

        do!
            capture
                "existing"
                "Only the payments are queried. The orders you already hold supply the keys, and no orders query is sent."
                [ "\"t0\".\"order_id\" = ANY(@p0)" ]
                None
                (fun () -> task { let! _ = repo.Preload(withPayments, held) in () })

        // 6 · Projections
        do!
            capture
                "projections"
                "Only the three projected columns are selected, and no Order is built. Explain itself sends nothing."
                [ "\"t0\".\"id\" AS \"c0\""; "\"t0\".\"total\" AS \"c1\""; "\"t0\".\"version\" AS \"c2\"" ]
                None
                (fun () -> task { let! _ = repo.All totals in () })

        // 7 · Changesets
        let form = Dictionary(dict [ "cancellationReason", "changed my mind"; "id", "999"; "version", "7" ])
        do!
            capture
                "changesets"
                "The insert omits the generated id and returns it. The form update sets only the two changed fields plus the version, and only if the stored version still matches; the id and version in the form were ignored. The key-only update writes the status alone."
                [ "INSERT INTO \"customers\" (\"name\", \"email\")"
                  "SET \"cancellation_reason\" = @p0, \"status\" = @p1, \"version\" = (\"orders\".\"version\" + @p2)"
                  "\"orders\".\"version\" = @p4"
                  "SET \"status\" = @p0, \"version\"" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.Insert(registerCustomer "Bob" "bob@example.com")
                        let! _ = repo.Update(firstOrder |> cancelFromForm form)
                        let! _ = repo.Update(publishByKey second.Id second.Version)
                        ()
                    })

        // 8 · Child writes
        let lines =
            [ newLine widget.Id 1 9.99m; newLine gadget.Id 2 3m ]
            |> List.map (fun r -> (Result.toOption r).Value)

        let mutable placed = Unchecked.defaultof<Order>

        do!
            capture
                "children"
                "The parent is inserted first; its generated id becomes each line's order_id. Replacing the lines locks the order's current lines with FOR UPDATE, then deletes the ones that were left out (the schema declares OnReplace.Delete)."
                [ "INSERT INTO \"order_lines\""; "FOR UPDATE"; "DELETE FROM \"order_lines\"" ]
                None
                (fun () ->
                    task {
                        let! o = repo.Insert(placeOrder ada.Id lines)
                        placed <- ok o
                        let kept = Assoc.get placed.Lines |> List.take 1
                        let! r = repo.Update(keepOnly placed kept)
                        ok r |> ignore
                    })

        let! _ =
            repo.Insert(
                Changeset.insert
                    { Id = PaymentId 0L
                      OrderId = second.Id
                      Amount = 3m
                      Status = Succeeded
                      CreatedAt = DateTime.UtcNow }
            )

        // 12 · Compared with EF Core's AsSplitQuery: the same page of published
        // orders with lines, products and successful payments.
        do!
            capture
                "split"
                "Plasm's relationship commands receive the selected order ids as one array parameter. The parent filter, ordering and LIMIT run once, in the first command, and are never repeated."
                [ "LIMIT @p1"
                  "\"t0\".\"order_id\" = ANY(@p0)"
                  "\"t0\".\"id\" = ANY(@p0)" ]
                None
                (fun () -> task { let! _ = repo.All efComparisonPage in () })

        // 13 · Counts and aggregates
        do!
            capture
                "aggregates"
                "Each call is one command that returns one row per result: the count, one row of statistics, and one row per group. The sum of no rows is COALESCE'd to 0; HAVING filters groups after GROUP BY, and the groups are ordered by their aggregate."
                [ "SELECT COUNT(*) AS \"n\""
                  "COALESCE(SUM(\"t0\".\"total\"), 0)"
                  "AVG(\"t0\".\"total\")"
                  "GROUP BY \"j1\".\"name\""
                  "HAVING (COUNT(*) > @p0)"
                  "ORDER BY COALESCE(SUM(\"t0\".\"total\"), 0) DESC" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.Count published
                        let! _ = repo.One orderStats
                        let! _ = repo.All revenueByCustomer
                        ()
                    })

        // 14 · Bulk writes
        do!
            capture
                "bulk"
                "One UPDATE changes every matching draft. Only the assigned fields are in the SET clause, and the version is incremented. The upsert is one INSERT; a row whose SKU already exists is updated from the incoming row (excluded) instead, and both kinds come back through RETURNING."
                [ "SET \"status\" = @p0, \"cancellation_reason\" = @p1, \"version\" = (\"orders\".\"version\" + @p2)"
                  "\"orders\".\"placed_at\" < @p"
                  "ON CONFLICT (\"sku\") DO UPDATE"
                  "\"name\" = \"excluded\".\"name\"" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.Execute(expireDrafts (DateTime.UtcNow.AddMinutes 1.0))

                        let! _ =
                            repo.Execute(
                                syncCatalogue
                                    [ { widget with Name = "Widget (2026)" }
                                      { Id = ProductId 0L; Sku = "S-1"; Name = "Sprocket" } ]
                            )

                        ()
                    })

        // 15 · Hand-written SQL
        do!
            capture
                "raw"
                "The interpolated minimum became @p0: hand-written SQL never has values spliced into its text. The fragment's SQL is inserted as written, with its arguments translated (a column) or sent as parameters (the pattern)."
                [ "o.total >= @p0"; "(\"t0\".\"name\" ILIKE @p0)" ]
                None
                (fun () ->
                    task {
                        let minimum = 5m

                        let! _ =
                            repo.Sql<string * int>(
                                $"SELECT c.name, count(o.id) FROM customers c JOIN orders o ON o.customer_id = c.id WHERE o.total >= {minimum} GROUP BY c.name ORDER BY c.name"
                            )

                        let! _ = repo.All(customersMatching "a%")
                        ()
                    })

        // 16 · Prepared queries
        let! bob = repo.One(plasm { for c in Db.customers do where (c.Name = "Bob") })
        let bob = (ok bob).Value

        do!
            capture
                "prepared"
                "Two calls with different arguments send the same SQL text: the customer and the page size are parameters bound per call, and the query was built and planned once."
                [ "\"t0\".\"customer_id\" = @p0"; "LIMIT @p1" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All(latestOrdersOf ada.Id 5)
                        let! _ = repo.All(latestOrdersOf bob.Id 20)
                        ()
                    })

        // 17 · Many-to-many (setup is not captured)
        let! tags = repo.Execute(Bulk.insert [ { Id = TagId 0L; Name = "fsharp" }; { Id = TagId 0L; Name = "sql" }; { Id = TagId 0L; Name = "orm" } ])
        let tags = ok tags |> List.map (fun t -> t.Name, t) |> Map.ofList

        let! intro =
            repo.Insert(
                Changeset.insert { Id = ArticleId 0L; Title = "Plasm in ten minutes"; Tags = NotLoaded }
                |> Changeset.putLinks ((fun a -> a.Tags), [ tags.["fsharp"]; tags.["orm"] ])
            )

        let intro = ok intro

        let! _ =
            repo.Insert(
                Changeset.insert { Id = ArticleId 0L; Title = "Window functions"; Tags = NotLoaded }
                |> Changeset.putLinks ((fun a -> a.Tags), [ tags.["sql"] ])
            )

        do!
            capture
                "manytomany"
                "Tags load with one command per batch of articles, joined through article_tags, which also supplies each row's article. Retagging writes only join-table rows: links outside the new set are deleted, missing ones inserted. The filter is an EXISTS through the join table."
                [ "INNER JOIN \"article_tags\" AS \"j\" ON (\"j\".\"tag_id\" = \"t0\".\"id\")"
                  "(\"j\".\"article_id\" = ANY(@p0))"
                  "DELETE FROM \"article_tags\""
                  "unnest(@p1)"
                  "INNER JOIN \"article_tags\" AS \"t1j\"" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All articlesWithTags
                        let! _ = repo.Update(retag intro [ tags.["fsharp"]; tags.["sql"] ])
                        let! _ = repo.All(taggedWith "fsharp")
                        ()
                    })

        // 18 · Schema check and sandbox
        do!
            capture
                "testing"
                "The schema check is one catalogue query; it found no differences for this model. The sandbox's insert and count run inside a transaction that is rolled back afterwards. Its BEGIN, savepoints and ROLLBACK are sent by the connection, not as commands, so they do not appear here."
                [ "information_schema.columns"; "INSERT INTO \"customers\"" ]
                None
                (fun () ->
                    task {
                        let! issues = repo.CheckSchema()

                        match ok issues with
                        | [] -> ()
                        | found -> failwithf "the example model does not match the database: %A" found

                        let! inside =
                            repo.Sandbox(fun repo ->
                                task {
                                    let! _ = repo.Insert(registerCustomer "Test" "test@example.com")
                                    let! n = repo.Count(Query.from Db.customers)
                                    return ok n
                                })

                        let! after = repo.Count(Query.from Db.customers)

                        if ok after <> inside - 1L then
                            failwith "the sandbox was not rolled back"
                    })

        // 10 · Failures
        let isLucky (d: decimal) = d = 7m

        do!
            capture
                "errors"
                "The stale update's WHERE requires version 1, but the stored row is at version 2, so 0 rows match and the result is WriteError.Stale. The untranslatable query sends nothing at all."
                [ "\"orders\".\"version\" = @p3" ]
                (Some "isLucky o.Total: no command was sent; translation failed first.")
                (fun () ->
                    task {
                        let! _ = repo.Update(firstOrder |> Changeset.edit (fun o -> { o with Total = 1m }))
                        let! _ = repo.All(plasm { for o in Db.orders do where (isLucky o.Total) })
                        ()
                    })

        let payload =
            examples
            |> Seq.map (fun e ->
                e.Id,
                {| caption = e.Caption
                   marks = e.Marks
                   empty = e.Empty |> Option.toObj
                   commands =
                    e.Commands
                    |> List.map (fun c ->
                        {| sql = c.Sql
                           parameters = c.Parameters
                           rows = c.Rows |}) |})
            |> dict

        let json = JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))

        let header =
            "// Generated by `dotnet run --project examples/Plasm.Examples -- sql`. Do not edit.\n"
            + "// SQL captured from Plasm's diagnostics while running each example against PostgreSQL.\n"

        File.WriteAllText(outputPath, header + "window.PLASM_SQL = " + json + ";\n")
        printfn "wrote %d examples, %d commands to %s" examples.Count (examples |> Seq.sumBy (fun e -> e.Commands.Length)) outputPath
    }
