namespace Plasm.Koans.Browser.Specs

open System
open System.Text.Json
open System.Threading.Tasks
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// F# snippets run both in the page (PGlite) and on a server (PostgreSQL):
/// each is the body of `fun (repo: Repo) -> Task<string>`.
module Snippets =
    let queries =
        """    task {
        let! joined = repo.All(plasm { for o in tableOf<Order> do join c in tableOf<Customer> on (o.CustomerId = Some c.Id); orderBy o.Id; select (o.Id, c.Name) })
        let! totals = repo.All(plasm { for o in tableOf<Order> do groupBy o.CustomerId; orderBy o.CustomerId; select (o.CustomerId, Agg.count (), Agg.sum o.Total) })
        let! loaded = repo.All(plasm { for o in tableOf<Order> do orderBy o.Id })
        let paymentsOf = Query.prepare (fun (ids: Arg<OrderId list>) -> plasm { for p in tableOf<Payment> do where (List.contains p.OrderId ids.Value); orderBy p.Id })
        let pairs = match joined with Ok rows -> rows | Error e -> failwithf "%A" e
        let orders = match loaded with Ok os -> os | Error e -> failwithf "%A" e
        let! payments = repo.All(paymentsOf (orders |> List.map (fun o -> o.Id)))
        let payments = match payments with Ok ps -> ps | Error e -> failwithf "%A" e
        let graph =
            orders |> List.map (fun o ->
                o.Id,
                (o.Lines |> List.map (fun l -> l.Quantity.Value, l.ProductId)),
                (payments |> List.filter (fun p -> p.OrderId = o.Id) |> List.map (fun p -> p.Status, p.Amount)))
        return sprintf "%A\n%A\n%A" pairs totals graph
    }"""

    let uniqueViolation =
        """    task {
        let! r = repo.Transact(Tx.insert (New.customer "Ada again" "ada@example.com"): Tx<Customer, unit>)
        return
            (match r with
             | Error e ->
                 match e.Failure with
                 | TxFailure.InvalidChanges(_, errors) -> errors |> List.map (fun f -> sprintf "%s %A" f.Field f.Kind) |> String.concat ", "
                 | other -> sprintf "%A" other
             | Ok _ -> "saved")
    }"""

    let transactions =
        """    task {
        let failing =
            transaction {
                let! dan = Tx.insert (New.customer "Dan" "dan@example.com")
                let! _ = Tx.insert (New.order (Some dan.Id) Draft -1m)
                return ()
            }
        let! t = repo.Transact failing
        let! inside =
            repo.Sandbox(fun r ->
                task {
                    let! _ = r.Transact(Tx.insert (New.customer "Eve" "eve@example.com"): Tx<Customer, unit>)
                    let! n = r.Count(Query.from (tableOf<Customer>))
                    return n
                })
        let! after = repo.Count(Query.from (tableOf<Customer>))
        let step = match t with Error e -> sprintf "failed at %s, rolled back %A" e.Step.Action (e.RolledBack |> List.map (fun s -> s.Action)) | Ok _ -> "committed"
        return sprintf "%s; inside the sandbox %A; afterwards %A" step inside after
    }"""

    let overlapping =
        """    task {
        let reads = [ for s in [ Published; Draft; Cancelled; Published ] -> repo.Count(plasm { for o in tableOf<Order> do where (o.Status = s) }) ]
        let! counts = Task.WhenAll reads
        return sprintf "%A" (counts |> Array.map (function Ok n -> n | Error e -> failwithf "%A" e))
    }"""

    let values =
        """    task {
        let placed = DateTime(2026, 3, 4, 5, 6, 7, 891, DateTimeKind.Utc)
        let! o = repo.Transact(Tx.insert { New.order (Some(CustomerId 2L)) Cancelled 12345.67m with PlacedAt = placed; CancellationReason = Some "naïve ✓ 'quoted'" }: Tx<Order, unit>)
        let o = match o with Ok o -> o | Error e -> failwithf "%A" e
        let! back = repo.One(plasm { for x in tableOf<Order> do where (x.Id = o.Id) })
        let back = match back with Ok(Some b) -> { b with Id = OrderId 0L } | other -> failwithf "%A" other
        let ids = [| 1L; 3L |]
        let! raw = repo.Sql<decimal * DateTime * string option * CustomerId option * bool>($"SELECT total, placed_at, cancellation_reason, customer_id, total > 50 FROM orders WHERE id = any({ids}) ORDER BY id")
        let! none = repo.Sql<decimal option * int64[]>($"SELECT NULL::numeric, ARRAY[1, 2, 3]::bigint[]")
        return sprintf "%A\n%A\n%A" back raw none
    }"""

    let readsAndWrites =
        """    task {
        let adaEmail = email "ada@example.com"
        let! ada = repo.One(plasm { for c in tableOf<Customer> do where (c.Email = adaEmail) })
        let ada = match ada with Ok(Some a) -> a | other -> failwithf "%A" other
        let! _ = repo.Transact(Tx.update (ada, { ada with Name = "Ada L." }): Tx<Customer, unit>)
        let! _ = repo.Transact(Tx.insert (New.product "SPROCKET" "Sprocket"): Tx<Product, unit>)
        let! _ = repo.Transact(Tx.execute (Bulk.update (plasm { for o in tableOf<Order> do where (o.Status = Draft) }, fun o -> { o with Total = o.Total + 1m })): Tx<int, unit>)
        let! _ = repo.Count(plasm { for o in tableOf<Order> do where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })) })
        return "done"
    }"""

    /// A read started while a transaction holds PGlite's one connection.
    let singleConnection =
        """    task {
        let started = Diagnostics.Stopwatch.StartNew()
        let tx =
            repo.Transact(
                transaction {
                    let! _ = Tx.insert (New.customer "Pia" "pia@example.com")
                    let! _ =
                        Tx.run (fun _ ->
                            task {
                                do! Task.Delay 500
                                return (Ok() : Result<unit, string>)
                            })
                    return ()
                })
        let! seen = repo.Count(plasm { for c in tableOf<Customer> do where (c.Name = "Pia") })
        let readDone = started.ElapsedMilliseconds
        let! t = tx
        let! _ = repo.Transact(Tx.executeSql $"delete from customers where name = 'Pia'": Tx<int, unit>)
        return sprintf "%A|%b|%d" seen (Result.isOk t) readDone
    }"""

/// Features: The koans database runs in the browser; Plasm runs against PGlite
[<Binding>]
type DatabaseSteps(w: BrowserWorld) =

    let mutable page: string * string list = "", []
    let mutable server: string * string list = "", []

    /// Evaluates a snippet in the page.
    let inPage (code: string) (sandboxed: bool) =
        task {
            let! json = w.Page.EvaluateAsync<string>("async ([c, s]) => JSON.stringify(await window.koans.evaluate(c, s))", box [| box code; box sandboxed |])
            use doc = JsonDocument.Parse json

            match doc.RootElement.TryGetProperty "error" with
            | true, e -> return failwithf "the snippet failed in the page:\n%s" (e.GetString())
            | _ ->
                let sql = doc.RootElement.GetProperty("sql").EnumerateArray() |> Seq.map (fun s -> s.GetString()) |> List.ofSeq
                return doc.RootElement.GetProperty("result").GetString(), sql
        }

    let both (code: string) =
        task {
            let! p = inPage code true
            let! s = ServerSide.evaluate code
            page <- p
            server <- s
        }

    let sameResult () =
        check (fst page = fst server) $"the page returned:\n{fst page}\n\nthe server returned:\n{fst server}"

    let sameSql () =
        check (snd page = snd server) $"the page sent:\n%A{snd page}\n\nthe server sent:\n%A{snd server}"

    let runNamed (name: string) =
        task {
            let! koan = koanNamed w.Page name
            do! show w.Page koan
            do! typeCode w.Page (fill koan.Body answers.Value.[koan.Name])
            let! _ = run w.Page
            let! status, text = result w.Page
            return status, text
        }

    // The data is the same as the terminal version's

    [<StepDefinition("the learner runs the koan \"(.*)\" with its reference answer")>]
    member _.RunNamed(name: string) =
        task {
            let! status, text = runNamed name
            w.Koan <- Some { Lesson = 0; Index = 0; Name = name; Body = ""; Discovers = text }
            check (status = "passed") $"{name}: {status}\n{text}"
        }
        |> unit

    [<StepDefinition("it passes, reading the same customers as the terminal version")>]
    member _.PassesSameCustomers() = ()

    [<StepDefinition("the learner runs a koan that inserts, updates and deletes rows, twice")>]
    member _.Twice() =
        task {
            let outcomes = ResizeArray()

            for _ in 1..2 do
                for name in [ "Inserting returns the stored record"; "Stale updates are refused"; "Deleting what a query matches" ] do
                    let! status, text = runNamed name
                    outcomes.Add(name, status, text)

            page <- "", (outcomes |> Seq.map (fun (n, s, t) -> $"{n}: {s} {t}") |> List.ofSeq)
        }
        |> unit

    [<StepDefinition("it passes both times")>]
    member _.PassedBoth() =
        let failing = snd page |> List.filter (fun line -> not (line.Contains ": passed"))
        check ((snd page).Length = 6) $"%A{snd page}"
        check failing.IsEmpty (String.concat "\n" failing)

    [<StepDefinition("the next koan sees the starting data")>]
    member _.StartingData() =
        task {
            let! status, text = runNamed "Counting transfers no rows"
            check (status = "passed") $"{status}\n{text}"
        }
        |> unit

    // Plasm behaves as it does against a PostgreSQL server

    [<StepDefinition("a query with joins, aggregates and relationship loads is run in the page")>]
    member _.Queries() = both Snippets.queries |> unit

    [<StepDefinition("the same SQL text is sent as against a PostgreSQL server")>]
    [<StepDefinition("the commands they report are the same")>]
    member _.SameSqlStep() = sameSql ()

    [<StepDefinition("the same rows come back")>]
    member _.SameRows() = sameResult ()

    [<StepDefinition("the learner runs a koan that violates a unique constraint")>]
    member _.Violation() = both Snippets.uniqueViolation |> unit

    [<StepDefinition("the failure is a unique violation naming the constraint")>]
    member _.UniqueNamed() =
        sameResult ()
        check (fst page = "Email UniqueConstraint \"customers_email_key\"") (fst page)

    [<StepDefinition("a transaction with a failing step and a nested sandbox run in the page")>]
    member _.Transactions() = both Snippets.transactions |> unit

    [<StepDefinition("the failing step's changes are rolled back and the sandbox's changes are undone")>]
    member _.RolledBack() =
        sameResult ()
        check (fst page = "failed at insert Order, rolled back [\"insert Customer\"]; inside the sandbox Ok 4L; afterwards Ok 3L") (fst page)

    [<StepDefinition("several reads start at once in the page")>]
    member _.Overlapping() = both Snippets.overlapping |> unit

    [<StepDefinition("they all complete with correct results, one after another")>]
    member _.OverlappingCorrect() =
        sameResult ()
        check (fst page = "[|2L; 1L; 1L; 2L|]") (fst page)

    // Plasm runs against PGlite

    [<StepDefinition("rows with every column type the koans use are written and read through the PGlite provider and through Npgsql")>]
    member _.Values() = both Snippets.values |> unit

    [<StepDefinition("every value reads back the same, including decimals, timestamps, options and arrays")>]
    member _.SameValues() = sameResult ()

    [<StepDefinition("the same reads and writes run through the PGlite provider and through Npgsql")>]
    member _.ReadsAndWrites() = both Snippets.readsAndWrites |> unit

    [<StepDefinition("a read starts in the page while a transaction is still open")>]
    member _.SingleConnection() =
        task {
            let! p = inPage Snippets.singleConnection false
            page <- p
        }
        |> unit

    [<StepDefinition("the read completes after the transaction ends")>]
    member _.ReadWaited() =
        let readDone = int64 ((fst page).Split('|').[2])
        check (readDone >= 450L) $"the read finished after {readDone} ms, before the 500 ms transaction ended: {fst page}"

    [<StepDefinition("it sees the transaction's committed rows")>]
    member _.SeesCommitted() =
        let parts = (fst page).Split('|')
        check (parts.[0] = "Ok 1L" && parts.[1] = "true") $"the read saw {parts.[0]} (transaction ok: {parts.[1]})"
