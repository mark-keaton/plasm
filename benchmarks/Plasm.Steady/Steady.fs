/// Each operation run 2,000 times to warm up, then 5,000 times timed one by
/// one: the median and 95th percentile of those, and the bytes allocated per
/// operation (GC.GetTotalAllocatedBytes over the timed loop). Each read and
/// the insert have a twin sending the same SQL through Npgsql alone, reading
/// every column as an object and building nothing: what is left is Plasm's.
/// Two splits, with no database, place a query built per call: building its
/// value (FSharp.Core decoding the builder's quotations) and translating it.
/// Arguments: a connection string to the seeded database.
module Plasm.Steady.Program

open System
open System.Diagnostics
open System.Threading.Tasks
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain

let warmUp = 2000
let timed = 5000

type PaymentsOf = { Order: Order; Payments: Payment list }

let customerById = Query.prepare (fun (id: Arg<CustomerId>) -> plasm { for c in tableOf<Customer> do where (c.Id = id.Value) })
let orderById = Query.prepare (fun (id: Arg<OrderId>) -> plasm { for o in tableOf<Order> do where (o.Id = id.Value) })

let products = plasm { for p in tableOf<Product> do where (p.Sku.StartsWith "sku1"); orderBy p.Id }

let orderWithPayments (id: OrderId) =
    plasm {
        for o in tableOf<Order> do
            where (o.Id = id)
            let! payments = plasm { for p in referring<Payment> o do orderBy p.Id }
            return { Order = o; Payments = payments }
    }

let orderWithPaymentsPrepared =
    Query.prepare (fun (id: Arg<OrderId>) ->
        plasm {
            for o in tableOf<Order> do
                where (o.Id = id.Value)
                let! payments = plasm { for p in referring<Payment> o do orderBy p.Id }
                return { Order = o; Payments = payments }
        })

let ok (r: Result<'a, DbError>) = match r with Ok v -> v | Error e -> failwith (string e)

/// One operation, run as described above; its key changes with each run.
let measure (name: string) (op: int -> Task) =
    task {
        for i in 1..warmUp do
            do! op i
        // Lets tiered compilation finish its background work under the runtime.
        do! Task.Delay 1000
        for i in 1..200 do
            do! op i
        let ticks = Array.zeroCreate<int64> timed
        let before = GC.GetTotalAllocatedBytes true
        for i in 0 .. timed - 1 do
            let t = Stopwatch.GetTimestamp()
            do! op i
            ticks.[i] <- Stopwatch.GetTimestamp() - t
        let bytes = (GC.GetTotalAllocatedBytes true - before) / int64 timed
        Array.sortInPlace ticks
        let us (t: int64) = float t * 1_000_000.0 / float Stopwatch.Frequency
        let f (x: float) = x.ToString("F1", Globalization.CultureInfo.InvariantCulture)
        Console.WriteLine(name + "\t" + f (us ticks.[timed / 2]) + "\t" + f (us ticks.[timed * 95 / 100]) + "\t" + string bytes)
    }

/// Sends `sql` with `parameters`, reads every column of every row as an
/// object, and returns the values of column `keyColumn` (for the next
/// statement's array parameter).
let raw (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (sql: string) (parameters: obj list) (keyColumn: int) =
    task {
        use cmd = new NpgsqlCommand(sql, conn, tx)
        parameters |> List.iteri (fun i v -> cmd.Parameters.Add(NpgsqlParameter("p" + string i, v)) |> ignore)
        use! r = cmd.ExecuteReaderAsync()
        let keys = ResizeArray<int64>()
        let values = Array.zeroCreate<obj> r.FieldCount
        while! r.ReadAsync() do
            r.GetValues values |> ignore
            if keyColumn >= 0 then keys.Add(r.GetInt64 keyColumn)
        return keys.ToArray()
    }

let sqlOf (plan: Result<QueryPlan, TranslationError>) =
    match plan with
    | Ok p -> p.Sql :: [ for l in p.Loads -> l.Sql ]
    | Error e -> failwith (string e)

[<EntryPoint>]
let main argv =
    let cs = argv.[0]
    let mode = if Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported then "runtime" else "native"
    Console.WriteLine("mode\t" + mode)
    let ds = NpgsqlDataSource.Create cs
    let repo = Repo.Start(Schemas.model, PostgresProvider ds, typeof<Order>.Assembly, { RepoOptions.Default with Queries = StartupCheck.Ignore }).Result
    let customer i = CustomerId(int64 (i % 100 + 1))
    let order i = OrderId(int64 (i % 10_000 + 1))

    let work =
        task {
            do! measure "read by key, query built per call" (fun i ->
                task {
                    let id = customer i
                    let! r = repo.One(plasm { for c in tableOf<Customer> do where (c.Id = id) })
                    ok r |> ignore
                })
            do! measure "read by key, prepared" (fun i -> task { let! r = repo.One(customerById (customer i)) in ok r |> ignore })
            do! measure "list of 111 rows" (fun _ -> task { let! r = repo.All products in ok r |> ignore })
            do! measure "read with two loads" (fun i -> task { let! r = repo.All(orderWithPayments (order i)) in ok r |> ignore })
            do! measure "read with two loads, prepared" (fun i -> task { let! r = repo.All(orderWithPaymentsPrepared (order i)) in ok r |> ignore })
            // Translation alone, no database: what a query built per call costs before it is sent.
            do! measure "translate only: read by key, built per call" (fun i ->
                let id = customer i
                match repo.Explain(plasm { for c in tableOf<Customer> do where (c.Id = id) }) with
                | Ok _ -> Task.CompletedTask
                | Error e -> failwith (string e))
            // Building the value alone: FSharp.Core decodes the quotations the builder takes.
            do! measure "build only: read by key, built per call" (fun i ->
                let id = customer i
                let q = plasm { for c in tableOf<Customer> do where (c.Id = id) }
                if isNull (box q) then failwith "no query"
                Task.CompletedTask)
            do! measure "build only: read with two loads, built per call" (fun i ->
                let q = orderWithPayments (order i)
                if isNull (box q) then failwith "no query"
                Task.CompletedTask)
            do! measure "translate only: read with two loads, built per call" (fun i ->
                match repo.Explain(orderWithPayments (order i)) with
                | Ok _ -> Task.CompletedTask
                | Error e -> failwith (string e))
            do! measure "read with a load, prepared" (fun i -> task { let! r = repo.One(orderById (order i)) in ok r |> ignore })
            do! measure "insert with three owned rows, in a transaction" (fun i ->
                task {
                    let lines =
                        [ for n in 1..3 ->
                              { Id = OrderLineId New.unsaved; OrderId = OrderId New.unsaved; ProductId = ProductId(int64 n)
                                Quantity = (match Quantity.create n with Ok q -> q | Error e -> failwith e); UnitPrice = 2.5m } ]
                    let o =
                        { Id = OrderId New.unsaved; CustomerId = Some(customer i); Status = Draft; Total = 7.5m
                          CancellationReason = None; PlacedAt = DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc); Version = 1; Lines = lines }
                    let! r = repo.Transact(Tx.insert o)
                    match r with
                    | Ok _ -> ()
                    | Error e -> failwith (string e)
                })

            // The same SQL through Npgsql alone.
            let customerSql = sqlOf (repo.Explain(customerById (CustomerId 1L))) |> List.head
            let productSql = sqlOf (repo.Explain products) |> List.head
            let orderSql = sqlOf (repo.Explain(orderById (OrderId 1L)))
            let withPaymentsSql =
                match repo.Explain(orderWithPayments (OrderId 1L)) with
                | Ok p -> p.Sql :: [ for l in p.Loads -> l.Sql ]
                | Error e -> failwith (string e)
            let call (f: NpgsqlConnection -> Task) = task { use! conn = ds.OpenConnectionAsync() in do! f conn }
            do! measure "raw: read by key" (fun i -> call (fun c -> task { let! _ = raw c null customerSql [ box (int64 (i % 100 + 1)) ] -1 in () }))
            do! measure "raw: list of 111 rows" (fun _ -> call (fun c -> task { let! _ = raw c null productSql [ box "sku1%" ] -1 in () }))
            do! measure "raw: read with two loads" (fun i ->
                call (fun c ->
                    task {
                        let! ids = raw c null withPaymentsSql.[0] [ box (int64 (i % 10_000 + 1)) ] 0
                        for sql in withPaymentsSql.Tail do
                            let! _ = raw c null sql [ box ids ] -1
                            ()
                    }))
            do! measure "raw: read with a load" (fun i ->
                call (fun c ->
                    task {
                        let! ids = raw c null orderSql.[0] [ box (int64 (i % 10_000 + 1)) ] 0
                        let! _ = raw c null orderSql.[1] [ box ids ] -1
                        ()
                    }))
            do! measure "raw: insert with three owned rows, in a transaction" (fun i ->
                call (fun c ->
                    task {
                        use! tx = c.BeginTransactionAsync()
                        use insert = new NpgsqlCommand("INSERT INTO orders (customer_id, status, total, cancellation_reason, placed_at, version) VALUES ($1, $2, $3, $4, $5, $6) RETURNING id", c, tx)
                        for v in [ box (int64 (i % 100 + 1)); box "draft"; box 7.5m; box DBNull.Value; box (DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc)); box 1 ] do
                            insert.Parameters.Add(NpgsqlParameter(Value = v)) |> ignore
                        let! id = insert.ExecuteScalarAsync()
                        use batch = new NpgsqlBatch(c, tx)
                        for n in 1..3 do
                            let b = NpgsqlBatchCommand("INSERT INTO order_lines (order_id, product_id, quantity, unit_price) VALUES ($1, $2, $3, $4) RETURNING id")
                            for v in [ id; box (int64 n); box n; box 2.5m ] do
                                b.Parameters.Add(NpgsqlParameter(Value = v)) |> ignore
                            batch.BatchCommands.Add b
                        let! _ = batch.ExecuteNonQueryAsync()
                        do! tx.CommitAsync()
                    }))
        }

    work.GetAwaiter().GetResult()
    0
