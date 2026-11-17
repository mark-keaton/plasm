/// `dotnet run -- sql`: runs every website example against a throwaway database,
/// records the SQL Plasm actually sends, and writes `website/sql.js`.
///
/// Each example declares the fragments that make it distinctive. They must occur
/// in the captured SQL, or generation fails, so the site cannot show highlights
/// the library no longer produces.
module Shop.SqlCapture

open System
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

        let! repo =
            Repo.Start(
                Db.model,
                Postgres.connect connectionString,
                queriesIn = typeof<Order>.Assembly,
                options =
                    { RepoOptions.Default with
                        OnCommand = Some events.Add
                        IncludeParameterValues = true }
            )

        // The start-up check's own commands are not examples.
        events.Clear()

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

        let qty n = Quantity.create n |> ok

        /// A transaction that should succeed.
        let transact (tx: Tx<'T, Refusal>) =
            task {
                let! r = repo.Transact tx
                return ok r
            }

        let at minutes = DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc).AddMinutes(float minutes)

        // -- Setup (not captured) ---------------------------------------------
        let! products =
            transact (Tx.execute (Bulk.insert [ { Id = ProductId 0L; Sku = "W-1"; Name = "Widget" }; { Id = ProductId 0L; Sku = "G-1"; Name = "Gadget" } ]))

        let widget, gadget = products.[0], products.[1]

        // 7 · Transactions first: it creates the customer and the first order.
        let mutable ada = Unchecked.defaultof<Customer>
        let mutable first = Unchecked.defaultof<Order>

        do!
            capture
                "transactions"
                "One database transaction. Each insert gives back the record as stored, with its generated key, and the next step uses it; the order's lines are inserted with the order and carry its key."
                [ "INSERT INTO \"customers\""; "INSERT INTO \"orders\""; "INSERT INTO \"order_lines\""; "RETURNING" ]
                None
                (fun () ->
                    task {
                        let! c, o =
                            transact (checkout { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" } [ Order.line widget.Id (qty 2) 9.99m ] (at 0))

                        ada <- c
                        first <- o
                    })

        for amount, status, minutes in [ 19.98m, Succeeded, 5; 5m, Failed, 1; 2m, Pending, 3 ] do
            let! _ =
                transact (
                    Tx.insert
                        { Id = PaymentId 0L
                          OrderId = first.Id
                          Amount = amount
                          Status = status
                          CreatedAt = at minutes }
                )

            ()

        // A second order for Ada with two lines and no successful payment, so "overdue" finds one.
        let! second = transact (Tx.insert (Order.create ada.Id [ Order.line widget.Id (qty 1) 9.99m; Order.line gadget.Id (qty 2) 3m ] (at 10) |> ok))

        // An order nobody has claimed yet, so the left join has a None to show.
        let! _ = transact (Tx.insert { (Order.create ada.Id [ Order.line gadget.Id (qty 1) 4m ] (at 20) |> ok) with CustomerId = None })

        // 1 · Model
        do!
            capture
                "model"
                "Field names become snake_case columns, and every field is read. The lines are not a column: they are the order's own rows, read in one more command for every order the first one found, in the order of their key."
                [ "\"customer_id\""; "\"placed_at\""; "FROM \"order_lines\""; "\"order_lines\".\"order_id\" = ANY(@p0)" ]
                None
                (fun () -> task { let! _ = repo.All(Query.from (tableOf<Order>)) in () })

        // 2 · Scalars
        do!
            capture
                "scalars"
                "OrderId 1L is sent as the int64 1, and Draft as the text 'draft': conventions, with nothing registered. OrderStatus has a case with a field, so it is read from two columns, status and status_reason. The parameter values are listed below each statement."
                [ "\"o\".\"id\" = @p0"; "\"o\".\"status\" = @p1"; "\"o\".\"status_reason\"" ]
                None
                (fun () -> task { let! _ = repo.All(lookup second.Id) in () })

        // 3 · Queries
        do!
            capture
                "queries"
                "Both functions' filters reach the database. The payments query becomes a correlated NOT EXISTS, so each order appears at most once. List.exists over the order's own lines is an EXISTS over order_lines, and the domain's IsOpen member is written out from its definition. Each query's orders bring their lines in one more command."
                [ "\"o\".\"customer_id\" = @p0"; "NOT EXISTS"; "FROM \"payments\""; "FROM \"order_lines\"" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All(overdue ada.Id)
                        let! _ = repo.All(openWithProduct gadget.Id)
                        ()
                    })

        // 4 · Owned records
        do!
            capture
                "owned"
                "The page is chosen first (ORDER BY with the key as tiebreaker, LIMIT, OFFSET). The lines of exactly those orders follow in one command, their keys as one array parameter: two commands for a page of 20 or of 1,000."
                [ "LIMIT @p1"; "OFFSET @p2"; "\"o\".\"placed_at\" DESC, \"o\".\"id\""; "\"order_lines\".\"order_id\" = ANY(@p0)" ]
                None
                (fun () -> task { let! _ = repo.All(Query.from (tableOf<Order>) |> forCustomer ada.Id |> orderPage 0 20) in () })

        // 5 · Other aggregates, by id
        do!
            capture
                "references"
                "The orders, their lines, then one command for the payments of every order on the page: the ids are one array parameter, and the status filter and the ordering run in SQL. No orders query is sent again."
                [ "\"p\".\"order_id\" = ANY(@p0)"; "\"p\".\"status\" = @p1"; "ORDER BY \"p\".\"created_at\"" ]
                None
                (fun () -> task { let! _ = repo.All(withSucceededPayments (Query.from (tableOf<Order>) |> orderPage 0 20)) in () })

        // 6 · Projections
        do!
            capture
                "projections"
                "Only the three projected columns are selected, and no Order is built, so no lines are read. Explain itself sends nothing."
                [ "\"o\".\"id\" AS \"Id\""; "\"o\".\"total\" AS \"Total\""; "\"o\".\"version\" AS \"Version\"" ]
                None
                (fun () -> task { let! _ = repo.All totals in () })

        // 8 · Changes
        do!
            capture
                "changes"
                "The order is read with its lines. The update sets only what differs from the record read, here the total, and requires the version the caller saw. The line left out is checked to belong to the order, then deleted. Cancelling writes the status and its reason, the two columns of the case."
                [ "SET \"total\" = @p0, \"version\" = \"orders\".\"version\" + 1"
                  "\"orders\".\"version\" = @p"
                  "DELETE FROM \"order_lines\""
                  "SET \"status\" = @p0, \"status_reason\" = @p1" ]
                None
                (fun () ->
                    task {
                        let! trimmed = transact (changeOrder second.Id second.Version (Order.removeLines (fun l -> l.ProductId = gadget.Id)))
                        let! _ = transact (cancel trimmed.Id trimmed.Version "changed my mind")
                        ()
                    })

        // 9 · Failures. Publishing the first order moves it to version 2.
        let! _ = transact (changeOrder first.Id first.Version Order.publish)
        let isLucky (d: decimal) = d = 7m

        do!
            capture
                "errors"
                "The duplicate email reaches the database, which refuses it by customers_email_key; that is the Email field's error. The stale cancel requires version 1, but the stored row is at version 2, so no row matches: a Conflict. The untranslatable query sends nothing at all."
                [ "INSERT INTO \"customers\""; "\"orders\".\"version\" = @p" ]
                (Some "isLucky o.Total: no command was sent; translation failed first.")
                (fun () ->
                    task {
                        let! dup = repo.Transact(Tx.insert { ada with Id = CustomerId 0L; Name = "Eve" }: Tx<Customer, Refusal>)

                        match dup with
                        | Error { Failure = TxFailure.InvalidChanges(_, [ { Field = "Email" } ]) } -> ()
                        | other -> failwithf "expected the Email field's error, got %A" other

                        let! stale = repo.Transact(cancel first.Id first.Version "too late")

                        match stale with
                        | Error { Failure = TxFailure.Conflict _ } -> ()
                        | other -> failwithf "expected a conflict, got %A" other

                        let! _ = repo.All(plasm { for o in tableOf<Order> do where (isLucky o.Total) })
                        ()
                    })

        // 10 · Joins
        do!
            capture
                "joins"
                "Each join is part of one command. The inner join drops the order without a customer, and its whole Orders come with their lines, in one more command; the left join keeps it, and its customer columns read as NULL, which becomes None. Chained joins add one JOIN each, and the projection reads only the three columns it names."
                [ "INNER JOIN \"customers\" AS \"c\" ON \"o\".\"customer_id\" = \"c\".\"id\""
                  "LEFT JOIN \"customers\" AS \"c\""
                  "\"c\".\"email\" AS \"c1\""
                  "INNER JOIN \"products\" AS \"p\" ON \"l\".\"product_id\" = \"p\".\"id\"" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All ordersWithCustomers
                        let! _ = repo.All customerEmails
                        let! _ = repo.All soldProducts
                        ()
                    })

        let! _ =
            transact (
                Tx.insert
                    { Id = PaymentId 0L
                      OrderId = second.Id
                      Amount = 3m
                      Status = Succeeded
                      CreatedAt = at 30 }
            )

        // 11 · Compared with EF Core's AsSplitQuery: the same page of published
        // orders with their lines, and the successful payments of those orders.
        do!
            capture
                "split"
                "The page's filter, ordering and LIMIT run once, in the first command. The lines and then the payments receive the selected order ids as one array parameter; the parent query is never repeated."
                [ "LIMIT @p1"; "\"order_lines\".\"order_id\" = ANY(@p0)" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All(efComparisonPage 20)
                        ()
                    })

        // 12 · Counts and aggregates
        do!
            capture
                "aggregates"
                "Each call is one command that returns one row per result: the count, one row of statistics, and one row per group. The sum of no rows is COALESCE'd to 0; HAVING filters groups after GROUP BY, and the groups are ordered by their aggregate."
                [ "SELECT COUNT(*) AS \"n\""
                  "COALESCE(SUM(\"o\".\"total\"), 0)"
                  "AVG(\"o\".\"total\")"
                  "GROUP BY \"c\".\"name\""
                  "HAVING COUNT(*) > @p0"
                  "ORDER BY COALESCE(SUM(\"o\".\"total\"), 0) DESC" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.Count published
                        let! _ = repo.One orderStats
                        let! _ = repo.All revenueByCustomer
                        ()
                    })

        // 13 · Bulk writes
        do!
            capture
                "bulk"
                "One UPDATE changes every matching draft. Only the assigned field is in the SET clause, as its two columns, and the version is incremented. The upsert is one INSERT; a row whose SKU already exists is updated from the incoming row (excluded) instead, and both kinds come back through RETURNING."
                [ "SET \"status\" = @p0, \"status_reason\" = @p1, \"version\" = \"orders\".\"version\" + 1"
                  "\"orders\".\"placed_at\" < @p"
                  "ON CONFLICT (\"sku\") DO UPDATE"
                  "\"name\" = \"excluded\".\"name\"" ]
                None
                (fun () ->
                    task {
                        let! _ = transact (Tx.execute (expireDrafts (at 60)))

                        let! _ =
                            transact (
                                Tx.execute (
                                    syncCatalogue
                                        [ { widget with Name = "Widget (2026)" }
                                          { Id = ProductId 0L; Sku = "S-1"; Name = "Sprocket" } ]
                                )
                            )

                        ()
                    })

        // 14 · Hand-written SQL
        do!
            capture
                "raw"
                "The interpolated minimum became @p0: hand-written SQL never has values spliced into its text. The fragment's SQL is inserted as written, with its arguments translated (a column) or sent as parameters (the pattern)."
                [ "o.total >= @p0"; "(\"c\".\"name\" ILIKE @p0)" ]
                None
                (fun () ->
                    task {
                        let minimum = 5m

                        let! _ =
                            repo.Sql<string * int64>(
                                $"SELECT c.name, count(o.id) FROM customers c JOIN orders o ON o.customer_id = c.id WHERE o.total >= {minimum} GROUP BY c.name ORDER BY c.name"
                            )

                        let! _ = repo.All(customersMatching "a%")
                        ()
                    })

        // 15 · Prepared queries
        let! bob = transact (Tx.insert { Id = CustomerId 0L; Name = "Bob"; Email = "bob@example.com" })

        do!
            capture
                "prepared"
                "Two calls with different arguments send the same SQL text: the customer and the page size are parameters bound per call, and the query was built and planned once."
                [ "\"o\".\"customer_id\" = @p0"; "LIMIT @p1" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All(latestOrdersOf ada.Id 5)
                        let! _ = repo.All(latestOrdersOf bob.Id 20)
                        ()
                    })

        // 16 · Linked records (setup is not captured)
        let! intro = transact (Tx.insert { Id = ArticleId 0L; Title = "Plasm in ten minutes"; Tags = [] })
        let! intro = transact (retag intro [ "fsharp"; "orm" ])
        let! windows = transact (Tx.insert { Id = ArticleId 0L; Title = "Window functions"; Tags = [] })
        let! _ = transact (retag windows [ "sql" ])

        do!
            capture
                "linked"
                "Tags are read with their articles in one more command, joined through article_tags, which also supplies each row's article. Retagging inserts only the tags not yet stored, then writes only join-table rows: links outside the new set are deleted, missing ones inserted. The filter is an EXISTS through the join table."
                [ "INNER JOIN \"article_tags\" ON \"article_tags\".\"tag_id\" = \"tags\".\"id\""
                  "\"article_tags\".\"article_id\" = ANY(@p0)"
                  "ON CONFLICT (\"name\") DO NOTHING"
                  "DELETE FROM \"article_tags\""
                  "unnest(@p1)" ]
                None
                (fun () ->
                    task {
                        let! _ = repo.All articles
                        let! _ = transact (retag intro [ "fsharp"; "sql" ])
                        let! _ = repo.All(taggedWith "fsharp")
                        ()
                    })

        // 17 · Checks and the sandbox
        do!
            capture
                "testing"
                "Starting a repository translates every query, which sends nothing, and reads the catalogue for the schema check; it found no differences for this model. Printing the DDL sends nothing. The sandbox's insert and count run inside a transaction that is rolled back afterwards. Its BEGIN, savepoints and ROLLBACK are sent by the connection, not as commands, so they do not appear here."
                [ "information_schema.columns"; "INSERT INTO \"customers\"" ]
                None
                (fun () ->
                    task {
                        let! started = Repo.Start(Db.model, Postgres.connect connectionString, queriesIn = typeof<Order>.Assembly, options = { RepoOptions.Default with OnCommand = Some events.Add })
                        started.Ddl() |> ignore
                        let! issues = repo.CheckSchema()

                        match ok issues with
                        | [] -> ()
                        | found -> failwithf "the example model does not match the database: %A" found

                        let! inside =
                            repo.Sandbox(fun repo ->
                                task {
                                    let! _ = repo.Transact(Tx.insert { Id = CustomerId 0L; Name = "Test"; Email = "test@example.com" }: Tx<Customer, Refusal>)
                                    let! n = repo.Count(Query.from (tableOf<Customer>))
                                    return ok n
                                })

                        let! after = repo.Count(Query.from (tableOf<Customer>))

                        if ok after <> inside - 1L then
                            failwith "the sandbox was not rolled back"
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
