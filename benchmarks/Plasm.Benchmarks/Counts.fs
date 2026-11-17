/// `counts` mode: commands, rows transferred, allocations and median time per
/// workload and variant, with an equivalence check of the returned graph.
module Plasm.Benchmarks.Counts

open System
open System.Diagnostics
open System.Threading.Tasks
open Npgsql
open Plasm
open Plasm.TestDomain.Domain
open Plasm.Benchmarks.Workloads
type EfOrder = Plasm.Benchmarks.EfModel.Order
type EfContext = Plasm.Benchmarks.EfModel.ShopContext
type EfCapture = Plasm.Benchmarks.EfModel.CaptureInterceptor

type Measurement =
    { Workload: string
      Variant: string
      Commands: int
      Rows: int
      AllocatedKb: float
      MedianMs: float
      Shape: Shape }

let private iterations = 21
let private warmups = 60

/// Rows a captured EF command returns, by re-running it wrapped in count(*).
let private countRows (cs: string) (sql: string) (ps: (string * obj) list) =
    task {
        use conn = new NpgsqlConnection(cs)
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"select count(*) from ({sql}) as counted"

        for name, v in ps do
            cmd.Parameters.AddWithValue(name, v) |> ignore

        let! n = cmd.ExecuteScalarAsync()
        return int (unbox<int64> n)
    }

let private measure (op: unit -> Task<Shape>) =
    task {
        // Warm up past tiered JIT (Tier-0 -> Tier-1 needs ~30 calls), plan
        // caches, the connection pool and EF model building.
        for _ in 1..warmups do
            let! _ = op ()
            ()

        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        let before = GC.GetTotalAllocatedBytes(true)
        let! shape = op ()
        let allocated = GC.GetTotalAllocatedBytes(true) - before
        let times = ResizeArray<float>()

        for _ in 1..iterations do
            let sw = Stopwatch.StartNew()
            let! _ = op ()
            times.Add sw.Elapsed.TotalMilliseconds

        let sorted = times |> Seq.sort |> Seq.toArray
        return shape, float allocated / 1024.0, sorted.[sorted.Length / 2]
    }

let private plasmVariant workload variant (op: Plasm.Repo -> Task<Order list * Payment list>) =
    task {
        let! db = database.Force()
        let repo = db.RepoWith()
        let! shape, kb, ms = measure (fun () -> task { let! r = op repo in return plasmShape r })
        db.Recorder.Clear()
        let! _ = op repo
        let events = db.Recorder.Events

        return
            { Workload = workload
              Variant = variant
              Commands = events.Length
              Rows = events |> List.sumBy (fun e -> e.Rows)
              AllocatedKb = kb
              MedianMs = ms
              Shape = shape }
    }

let mutable private efSql: (string * string list) list = []

let private efVariant workload variant (op: string -> EfCapture -> Task<Shape>) =
    task {
        let! db = database.Force()
        let cs = db.ConnectionString
        let! shape, kb, ms = measure (fun () -> op cs null)
        let capture = EfCapture()
        let! _ = op cs capture
        let mutable rows = 0

        for c in capture.Commands do
            let! n = countRows cs c.Sql [ for p in c.Parameters -> p.Name, p.Value ]
            rows <- rows + n

        efSql <- efSql @ [ $"{workload} / {variant}", [ for c in capture.Commands -> c.Sql ] ]

        return
            { Workload = workload
              Variant = variant
              Commands = capture.Commands.Count
              Rows = rows
              AllocatedKb = kb
              MedianMs = ms
              Shape = shape }
    }

let all () =
    task {
        let results = ResizeArray<Measurement>()

        for n in [ 20; 100; 1000 ] do
            let name = $"page of {n} orders (lines, successful payments)"
            let page = plasmPageWithPayments n
            let! p = plasmVariant name "Plasm" (fun repo -> runPage repo page)
            results.Add p

            for split, label in [ false, "EF single query"; true, "EF split query" ] do
                let! e = efVariant name label (fun cs cap -> task { let! r = efPage cs cap n split in return efShape r })
                results.Add e

        let name = "1 order, 10 lines + 10 payments (siblings)"
        let! p = plasmVariant name "Plasm, queries built per call" (fun repo -> runSiblings repo (OrderId siblingOrderId))
        results.Add p
        let! p = plasmVariant name "Plasm, prepared queries" (fun repo -> runSiblingsPrepared repo (OrderId siblingOrderId))
        results.Add p

        for split, label in [ false, "EF single query"; true, "EF split query" ] do
            let! e = efVariant name label (fun cs cap -> task { let! r = efSiblings cs cap split in return efShape r })
            results.Add e

        return List.ofSeq results
    }

let print (results: Measurement list) =
    printfn ""
    printfn "| Workload | Variant | Commands | Rows transferred | Allocated (KB) | Median (ms) | Orders/Lines/Payments |"
    printfn "|---|---|---:|---:|---:|---:|---|"

    for r in results do
        printfn
            "| %s | %s | %d | %d | %.0f | %.2f | %d/%d/%d |"
            r.Workload
            r.Variant
            r.Commands
            r.Rows
            r.AllocatedKb
            r.MedianMs
            r.Shape.Orders
            r.Shape.Lines
            r.Shape.Payments

    // Equivalence: every variant must return the same graph for a workload.
    for workload, rs in results |> List.groupBy (fun r -> r.Workload) do
        let shapes = rs |> List.map (fun r -> r.Shape.Orders, r.Shape.Lines, r.Shape.Payments) |> List.distinct

        if shapes.Length > 1 then
            printfn "WARNING: variants disagree for %s: %A" workload shapes

    printfn ""
    printfn "EF Core SQL:"

    for label, sqls in efSql do
        printfn "--- %s" label

        for s in sqls do
            printfn "%s\n" s

/// `profile` mode: database time (per command, including reading rows) vs total
/// wall time, for the 1000-parent page.
let profile () =
    task {
        let! db = database.Force()
        let repo = db.RepoWith()
        let q = plasmPageWithPayments 1000

        for _ in 1..60 do
            let! _ = runPage repo q
            ()

        for _ in 1..3 do
            db.Recorder.Clear()
            let sw = Stopwatch.StartNew()
            let! _ = runPage repo q
            let total = sw.Elapsed.TotalMilliseconds
            let cmds = db.Recorder.Events |> List.map (fun e -> e.Duration.TotalMilliseconds)
            printfn "Plasm  total %.1f ms; commands %s (sum %.1f)" total (cmds |> List.map (sprintf "%.1f") |> String.concat " + ") (List.sum cmds)

        for _ in 1..3 do
            let timing = Plasm.Benchmarks.EfModel.TimingInterceptor()
            let sw = Stopwatch.StartNew()
            let! _ = efPage db.ConnectionString timing 1000 true
            let total = sw.Elapsed.TotalMilliseconds
            let cmds = timing.Durations |> Seq.map (fun d -> d.TotalMilliseconds) |> List.ofSeq
            printfn "EF split total %.1f ms; readers %s (sum %.1f)" total (cmds |> List.map (sprintf "%.1f") |> String.concat " + ") (List.sum cmds)
    }

/// Driver floor: Plasm's 1000-parent line query read with raw Npgsql
/// (GetFieldValue per column, no materialisation).
let floor () =
    task {
        let! db = database.Force()
        use conn = new NpgsqlConnection(db.ConnectionString)
        do! conn.OpenAsync()

        let ids =
            use c = conn.CreateCommand()
            c.CommandText <- "select id from orders where status = 'published' order by placed_at desc, id limit 1000"
            use r = c.ExecuteReader()
            [| while r.Read() do yield r.GetInt64 0 |]

        let sql =
            "SELECT \"t0\".\"id\", \"t0\".\"order_id\", \"t0\".\"product_id\", \"t0\".\"quantity\", \"t0\".\"unit_price\" FROM \"order_lines\" AS \"t0\" WHERE (\"t0\".\"order_id\" = ANY(@p0)) ORDER BY \"t0\".\"id\" ASC"

        for i in 1..8 do
            use c = conn.CreateCommand()
            c.CommandText <- sql
            c.Parameters.AddWithValue("p0", ids) |> ignore
            let sw = Stopwatch.StartNew()
            let before = GC.GetTotalAllocatedBytes(true)
            use! r = c.ExecuteReaderAsync()
            let mutable n = 0

            while r.Read() do
                r.GetFieldValue<int64> 0 |> ignore
                r.GetFieldValue<int64> 1 |> ignore
                r.GetFieldValue<int64> 2 |> ignore
                r.GetFieldValue<int> 3 |> ignore
                r.GetFieldValue<decimal> 4 |> ignore
                n <- n + 1

            if i > 3 then
                printfn "raw Npgsql: %d rows in %.1f ms, %.0f KB" n sw.Elapsed.TotalMilliseconds (float (GC.GetTotalAllocatedBytes(true) - before) / 1024.0)
    }

/// Upper bound on per-execution planning cost: Explain = plan + render.
let planning () =
    task {
        let! db = database.Force()
        let repo = db.RepoWith()

        let siblings, _ = plasmSiblingQueries (OrderId siblingOrderId)

        for name, q in [ "page 20", plasmPage 20; "siblings (the order query)", siblings ] do
            for _ in 1..200 do
                repo.Explain q |> ignore

            let n = 2000
            let before = GC.GetTotalAllocatedBytes(true)
            let sw = Stopwatch.StartNew()

            for _ in 1..n do
                repo.Explain q |> ignore

            printfn
                "Explain %s: %.1f us, %.1f KB per call"
                name
                (sw.Elapsed.TotalMilliseconds * 1000.0 / float n)
                (float (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / float n)
    }

/// Counts large-object allocation ticks (each about 100 KB) and the reasons
/// collections ran, from the runtime's own events.
type private GcListener() =
    inherit Diagnostics.Tracing.EventListener()
    member val LargeTicks = 0 with get, set
    member val LargeBytes = 0L with get, set
    member val Reasons = Collections.Generic.Dictionary<string, int>()
    member this.Reset() =
        this.LargeTicks <- 0
        this.LargeBytes <- 0L
        this.Reasons.Clear()
    override this.OnEventSourceCreated(source) =
        if source.Name = "Microsoft-Windows-DotNETRuntime" then
            this.EnableEvents(source, Diagnostics.Tracing.EventLevel.Verbose, LanguagePrimitives.EnumOfValue<int64, Diagnostics.Tracing.EventKeywords> 1L)
    override this.OnEventWritten(e) =
        let get (n: string) = let i = e.PayloadNames.IndexOf n in if i >= 0 then e.Payload.[i] else null
        if e.EventName <> null && e.EventName.StartsWith "GCAllocationTick" then
            if Convert.ToInt32(get "AllocationKind") = 1 then
                lock this (fun () ->
                    this.LargeTicks <- this.LargeTicks + 1
                    this.LargeBytes <- this.LargeBytes + Convert.ToInt64(get "AllocationAmount64")
                    let tn = get "TypeName"
                    let t = $"LOH {tn}"
                    this.Reasons.[t] <- (match this.Reasons.TryGetValue t with | true, n -> n + 1 | _ -> 1))
        elif e.EventName <> null && e.EventName.StartsWith "GCStart" then
            let depth, reason = get "Depth", get "Reason"
            let key = $"gen{depth}:{reason}"
            lock this (fun () -> this.Reasons.[key] <- (match this.Reasons.TryGetValue key with | true, n -> n + 1 | _ -> 1))

/// `spread` mode: the page of 1,000 three ways in one process, interleaved,
/// with min/median/p90 and the collections that ran (docs/benchmarks.md).
let spread () =
    task {
        let! db = database.Force()
        let repo = db.RepoWith()
        let inc = plasmPageWithPayments 1000
        let page = plasmPage 1000

        let second =
            Query.prepare (fun (orders: Arg<OrderId list>) ->
                plasm {
                    for p in tableOf<Payment> do
                        where (List.contains p.OrderId orders.Value && p.Status = Succeeded)
                        orderBy p.CreatedAt
                })

        let a () = task { let! r = runPage repo inc in return plasmShape r }

        let b () =
            task {
                match! repo.All page with
                | Ok orders ->
                    match! repo.All(second (orders |> List.map (fun o -> o.Id))) with
                    | Ok ps -> return plasmShape (orders, ps)
                    | Error e -> return failwithf "%O" e
                | Error e -> return failwithf "%O" e
            }

        let c () = task { let! r = efPage db.ConnectionString null 1000 true in return efShape r }
        let variants = [ "let!", a; "second query", b; "EF split", c ]

        for _, op in variants do
            for _ in 1..60 do
                let! _ = op ()
                ()

        for name, op in variants |> List.take 2 do
            db.Recorder.Clear()
            let! s = op ()
            printfn "== %s %A" name s
            for e in db.Recorder.Events do
                printfn "  %.1f ms, %d rows: %s" e.Duration.TotalMilliseconds e.Rows (e.Sql.Substring(0, min 400 e.Sql.Length))

        use gc = new GcListener()
        printfn "| round | variant | min | median | p90 | max | gen0 | gen1 | gen2 | KB/call | LOH ticks | reasons |"
        for round in 1..8 do
            for name, op in variants do
                let g0, g1, g2 = GC.CollectionCount 0, GC.CollectionCount 1, GC.CollectionCount 2
                let before = GC.GetTotalAllocatedBytes(true)
                let times = ResizeArray<float>()
                gc.Reset()
                for _ in 1..31 do
                    let sw = Stopwatch.StartNew()
                    let! _ = op ()
                    times.Add sw.Elapsed.TotalMilliseconds
                let kb = float (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 31.0
                let t = times |> Seq.sort |> Seq.toArray
                let reasons = lock gc (fun () -> gc.Reasons |> Seq.map (fun kv -> $"{kv.Key}x{kv.Value}") |> String.concat " ")
                printfn "| %d | %s | %.1f | %.1f | %.1f | %.1f | %d | %d | %d | %.0f | %d | %s |" round name t.[0] t.[15] t.[27] t.[30] (GC.CollectionCount 0 - g0) (GC.CollectionCount 1 - g1) (GC.CollectionCount 2 - g2) kb gc.LargeTicks reasons
    }
