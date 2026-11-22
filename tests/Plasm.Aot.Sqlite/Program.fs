/// Plasm.Sqlite under native AOT: what it adds to Plasm (decimals exact as
/// text, the window sum, the connection that hears its token, busy tries),
/// each step printing what it read, so the runtime's run and the native
/// binary's can be compared line by line (run.sh).
module Plasm.Aot.Sqlite.Program

open System
open System.Threading
open Plasm
open Plasm.Sqlite

type CustomerId = CustomerId of int64
type OrderId = OrderId of int64

[<Table "customers">]
type Customer =
    { Id: CustomerId
      Name: string
      [<Unique>]
      Email: string }

[<Table "orders">]
type Order =
    { Id: OrderId
      CustomerId: CustomerId
      Total: decimal
      Note: string option
      PlacedAt: DateTime }

module Queries =
    let overNineAndAHalf =
        plasm {
            for o in tableOf<Order> do
                where (o.Total > 9.4m)
                orderBy o.Total
                select o.Total
        }

    let byTotal =
        plasm {
            for o in tableOf<Order> do
                groupBy o.Total
                orderBy o.Total
                select (o.Total, Agg.count ())
        }

    let summary =
        plasm {
            for o in tableOf<Order> do
                select (Agg.count (), Agg.sum o.Total, Agg.min o.Total, Agg.max o.Total, Agg.avg o.Total)
        }

    let running =
        plasm {
            for o in tableOf<Order> do
                orderBy o.Id
                select (Win.runningSum o.Total (Win.whole |> Win.orderBy o.Id))
        }

    let distinctTotals =
        plasm {
            for o in tableOf<Order> do
                selectDistinct o.Total
        }

    let doubled =
        plasm {
            for o in tableOf<Order> do
                where (o.Total * 2m > 19.5m)
                select o.Total
        }

    let names = plasm { for c in tableOf<Customer> do orderBy c.Name; select c.Name }

let say (line: string) = Console.WriteLine line

let step (name: string) (f: unit -> string) =
    try
        say (name + ": " + f ())
    with ex ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        let i = inner ex
        say (name + ": threw " + i.GetType().FullName + ": " + i.Message)

let text (d: decimal) = d.ToString(Globalization.CultureInfo.InvariantCulture)
let texts (ds: decimal list) = String.Join(", ", ds |> List.map text)

let refusal (e: DbError) =
    match e with
    | DbError.Database f -> "database: " + f.Message
    | DbError.Translation t -> "translation: " + t.ToString()
    | DbError.Materialization m -> "materialization: " + m.Message
    | DbError.MoreThanOneRow -> "more than one row"

/// A count to a hundred million: many seconds.
[<Literal>]
let Slow = "(with recursive n(i) as (select 1 union all select i + 1 from n where i < 100000000) select count(*) from n)"

type AmountId = AmountId of int64

[<Table "amounts">]
type Amount = { Id: AmountId; Value: decimal; Whole: int64 }

/// The owed measurements (docs/status.md), timed, not compared: ordering and
/// summing n decimals stored as text against n integers, and a running sum
/// of decimals over a window of n rows (Plasm.Sqlite's window function).
let measure (file: string) =
    let provider = Sqlite.connect ("Data Source=" + file)
    let repo = Repo.Start(Model.buildOrFail [] [ typeof<Amount> ], provider, typeof<Amount>.Assembly, RepoOptions.Unchecked).Result
    let conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
    let exec (sql: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQueryAsync().Result |> ignore
    exec (repo.Ddl())
    let ms (f: unit -> unit) =
        f ()
        let w = Diagnostics.Stopwatch.StartNew()
        for _ in 1..3 do f ()
        (w.Elapsed.TotalMilliseconds / 3.0).ToString("F1", Globalization.CultureInfo.InvariantCulture)
    for n in [ 1000; 10000; 30000; 100000 ] do
        exec "delete from amounts"
        exec ("with recursive k(k) as (select 1 union all select k + 1 from k where k < " + string n + ") insert into amounts (id, value, whole) select k, printf('%d.%02d', (k * 7919) % 100000, k % 100), (k * 7919) % 100000 from k")
        let ordered = plasm { for a in tableOf<Amount> do orderBy a.Value; select a.Value }
        let orderedWhole = plasm { for a in tableOf<Amount> do orderBy a.Whole; select a.Whole }
        let summed = plasm { for a in tableOf<Amount> do select (Agg.sum a.Value) }
        let summedWhole = plasm { for a in tableOf<Amount> do select (Agg.sum a.Whole) }
        let running = plasm { for a in tableOf<Amount> do orderBy a.Id; select (Win.runningSum a.Value (Win.whole |> Win.orderBy a.Id)) }
        let all (q: Projection<'T>) () = repo.All(q).Result |> Result.defaultWith (fun e -> failwith (string e)) |> ignore
        Console.WriteLine(
            string n + " rows: order decimals " + ms (all ordered) + " ms, integers " + ms (all orderedWhole)
            + " ms; sum decimals " + ms (all summed) + " ms, integers " + ms (all summedWhole)
            + " ms; running sum of decimals " + ms (all running) + " ms")
    conn.Dispose()
    0

[<EntryPoint>]
let main argv =
    if argv.[0] = "measure" then
        exit (measure argv.[1])

    if argv.[0] = "mode" then
        Console.WriteLine(if Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported then "dynamic code on" else "dynamic code off")
        exit 0

    let file = argv.[0]
    let provider = Sqlite.connect ("Data Source=" + file)
    let model = Model.buildOrFail [] [ typeof<Customer>; typeof<Order> ]
    let mutable started = None

    step "start, checking every query" (fun () ->
        let r = Repo.Start(model, provider, typeof<Order>.Assembly, { RepoOptions.Unchecked with Queries = StartupCheck.Crash }).Result
        started <- Some r
        "started")

    match started with
    | None -> 1
    | Some repo ->
        step "tables from the Ddl" (fun () ->
            let conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
            use _ = conn
            use cmd = conn.CreateCommand()
            cmd.CommandText <- repo.Ddl()
            cmd.ExecuteNonQueryAsync().Result |> ignore
            "made, journal " + provider.JournalMode)

        step "the schema check" (fun () ->
            match repo.CheckSchema().Result with
            | Ok [] -> "the tables are the model's"
            | Ok problems -> string problems.Length + " problem(s)"
            | Error e -> refusal e)

        let mutable ada = None

        step "insert" (fun () ->
            match repo.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }: Tx<_, unit>).Result with
            | Ok c ->
                ada <- Some c.Id

                for total in [ 2.00m; 9.50m; 10.10m; 2.0m ] do
                    repo.Transact(Tx.insert { Id = OrderId 0L; CustomerId = c.Id; Total = total; Note = None; PlacedAt = DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc) }: Tx<_, unit>).Result
                    |> Result.mapError (fun e -> e.Display)
                    |> Result.defaultWith failwith
                    |> ignore

                "a customer and four orders"
            | Error e -> "failed: " + e.Display)

        step "compared by value, ordered by value" (fun () ->
            match repo.All(Queries.overNineAndAHalf).Result with
            | Ok ts -> texts ts
            | Error e -> refusal e)

        step "grouped by value" (fun () ->
            match repo.All(Queries.byTotal).Result with
            | Ok gs -> String.Join("; ", gs |> List.map (fun (t, n) -> text t + " x" + string n))
            | Error e -> refusal e)

        step "aggregates exact" (fun () ->
            match repo.One(Queries.summary).Result with
            | Ok(Some(n, sum, mn, mx, avg)) -> String.Join(", ", [ string n; text sum; text mn.Value; text mx.Value; text avg.Value ])
            | Ok None -> "none"
            | Error e -> refusal e)

        step "a running sum over a window" (fun () ->
            match repo.All(Queries.running).Result with
            | Ok ts -> texts ts
            | Error e -> refusal e)

        step "distinct by value" (fun () ->
            match repo.All(Queries.distinctTotals).Result with
            | Ok ts -> string ts.Length + " totals"
            | Error e -> refusal e)

        step "arithmetic exact" (fun () ->
            match repo.All(Queries.doubled).Result with
            | Ok ts -> texts ts
            | Error e -> refusal e)

        step "a stream" (fun () ->
            let rows = repo.Stream(Queries.names).GetAsyncEnumerator()
            let read = Collections.Generic.List<string>()

            while rows.MoveNextAsync().AsTask().Result do
                match rows.Current with
                | Ok n -> read.Add n
                | Error e -> read.Add(refusal e)

            rows.DisposeAsync().AsTask().Wait()
            String.Join(", ", read))

        step "a broken constraint is the field's error" (fun () ->
            match repo.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada again"; Email = "ada@example.com" }: Tx<_, unit>).Result with
            | Ok _ -> "inserted"
            | Error e -> "refused: " + e.Display)

        step "the SqliteConnection under a Tx.run step" (fun () ->
            let counted =
                Tx.run (fun ctx ->
                    task {
                        use cmd = ctx.CreateCommand()
                        let sqlite = Sqlite.connectionOf cmd.Connection
                        use own = sqlite.CreateCommand()
                        own.CommandText <- "select plasm_decimal_sum(total) from orders"
                        let! v = own.ExecuteScalarAsync()
                        return Ok(string v)
                    })

            match repo.Transact(counted: Tx<string, unit>).Result with
            | Ok v -> "its own command sums " + v
            | Error e -> "failed: " + e.Display)

        // Plasm.Sqlite's window functions (Windows.fs), through our own
        // P/Invoke and callbacks, natively as under the runtime.
        let overFrame (sql: string) =
            Tx.run (fun ctx ->
                task {
                    use cmd = ctx.CreateCommand()
                    cmd.CommandText <- sql
                    use! r = cmd.ExecuteReaderAsync(ctx.CancellationToken)
                    let read = Collections.Generic.List<string>()

                    while r.Read() do
                        read.Add(if r.IsDBNull 0 then "NULL" else r.GetString 0)

                    return Ok(String.Join(", ", read))
                })

        let frame (f: string) (values: string) =
            $"select {f}(v) over (order by i rows between 1 preceding and current row) from (select column1 as i, column2 as v from (values {values})) order by i"

        step "a window sum and average over a frame rows leave and that empties" (fun () ->
            let values = "(1, '1.10'), (2, null), (3, '2.205'), (4, null), (5, null)"

            match repo.Transact(overFrame (frame "plasm_decimal_sum" values): Tx<string, unit>).Result, repo.Transact(overFrame (frame "plasm_decimal_avg" values): Tx<string, unit>).Result with
            | Ok sums, Ok averages -> $"sums {sums}; averages {averages}"
            | Error e, _
            | _, Error e -> "failed: " + e.Display)

        step "a value the window sum cannot read is its query's error, and the connection goes on" (fun () ->
            let failed =
                match repo.Transact(overFrame (frame "plasm_decimal_sum" "(1, '1.10'), (2, 'not a decimal')"): Tx<string, unit>).Result with
                | Ok v -> "read " + v
                | Error e ->
                    match e.Failure with
                    | TxFailure.Failed(DbError.Database f) -> "Database " + string f.Kind + ", naming the value: " + string (f.Message.Contains "not a decimal")
                    | other -> "another failure: " + string other

            match repo.Transact(overFrame "select '1'": Tx<string, unit>).Result with
            | Ok next -> failed + "; next query reads " + next
            | Error e -> failed + "; next query failed: " + e.Display)

        step "no window total left after a failed and a cancelled window" (fun () ->
            let conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
            use _ = conn
            use cts = new CancellationTokenSource(300)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "select max(s) from (select plasm_decimal_sum(printf('%d.5', i)) over (order by i) as s from (with recursive n(i) as (select 1 union all select i + 1 from n where i < 100000000) select i from n))"

            let ended =
                try
                    cmd.ExecuteScalarAsync(cts.Token).Result |> ignore
                    "ran to its end"
                with _ ->
                    "cancelled"

            $"{ended}; window totals left: {Plasm.Sqlite.Windows.live ()}")

        // A foreign key failure is run again to find its key (ForeignKeys):
        // in a transaction, whatever the re-run meets, the transaction is as
        // the failure left it. A wrong state throws, which fails run.sh.
        for case, meets in
            [ "as it was", (fun (_: CancellationTokenSource) (_: Microsoft.Data.Sqlite.SqliteCommand) -> ())
              "failing again", (fun _ c -> (use p = c.Connection.CreateCommand() in p.Transaction <- c.Transaction; p.CommandText <- "PRAGMA defer_foreign_keys = OFF"; p.ExecuteNonQuery() |> ignore))
              "adding no violation", (fun _ c -> (use p = c.Connection.CreateCommand() in p.Transaction <- c.Transaction; p.CommandText <- "insert into customers (id, name, email) values (999, 'Late', 'late@example.com')"; p.ExecuteNonQuery() |> ignore))
              "its token cancelled", (fun cts _ -> cts.Cancel()) ] do
            step $"a foreign key failure run again, the re-run {case}, leaves the transaction as the failure did" (fun () ->
                let conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
                use _ = conn
                use tx = conn.BeginTransaction()
                use cts = new CancellationTokenSource()
                let command (sql: string) =
                    let c = conn.CreateCommand()
                    c.Transaction <- tx
                    c.CommandText <- sql
                    c
                let orphan = "insert into orders (customer_id, total, note, placed_at) values (999, '1.00', null, '2026-01-01 00:00:00')"

                Plasm.Sqlite.ForeignKeys.rerunning <- meets cts

                let failure =
                    try
                        use c = command orphan
                        c.ExecuteNonQueryAsync(cts.Token).Result |> ignore
                        failwith "the orphan was written"
                    with ex ->
                        Plasm.Sqlite.ForeignKeys.rerunning <- ignore

                        match (provider :> IProvider).Classify ex with
                        | Some f when f.Kind = DbFailureKind.ForeignKeyViolation ->
                            match f.Table with
                            | Some t -> $"""names {t} ({String.Join(", ", f.Columns)})"""
                            | None -> "unnamed"
                        | other -> failwith $"not a foreign key failure: %A{other}"

                Plasm.Sqlite.ForeignKeys.rerunning <- ignore
                use deferred = command "PRAGMA defer_foreign_keys"
                let deferred = Convert.ToInt64(deferred.ExecuteScalar())
                if deferred <> 0L then failwith "defer_foreign_keys left on"

                let savepoint =
                    try
                        use r = command "RELEASE plasm_foreign_key"
                        r.ExecuteNonQuery() |> ignore
                        "left"
                    with _ ->
                        "none"

                if savepoint <> "none" then failwith "the savepoint was left"

                let again =
                    try
                        use c = command orphan
                        c.ExecuteNonQuery() |> ignore
                        failwith "a second orphan was written: foreign keys are not checked at once"
                    with ex when ((provider :> IProvider).Classify ex |> Option.map (fun f -> f.Kind)) = Some DbFailureKind.ForeignKeyViolation -> "refused at once"

                use orphans = command "select count(*) from orders where customer_id = 999"
                let orphans = Convert.ToInt64(orphans.ExecuteScalar())
                if orphans <> 0L then failwith $"{orphans} orphans written"
                tx.Rollback()
                $"{failure}; defer_foreign_keys 0, no savepoint, the next orphan {again}, no orphan written")

        step "a running statement cancelled by its token" (fun () ->
            let conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
            use _ = conn
            use cts = new CancellationTokenSource(300)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "select " + Slow
            let watch = Diagnostics.Stopwatch.StartNew()

            let ended =
                try
                    cmd.ExecuteScalarAsync(cts.Token).Result |> ignore
                    "ran to its end"
                with ex ->
                    // Thrown as the call returns, or as its task's fault.
                    (match ex with
                     | :? AggregateException as a -> a.InnerException
                     | e -> e)
                        .GetType()
                        .Name

            use next = conn.CreateCommand()
            next.CommandText <- "select 41 + 1"
            let n = Convert.ToInt64(next.ExecuteScalarAsync().Result)
            ended + (if watch.ElapsedMilliseconds < 5000L then " in time" else " late") + ", then " + string n)

        0
