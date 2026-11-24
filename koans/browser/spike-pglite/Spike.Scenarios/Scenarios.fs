/// Representative Plasm operations against PGlite, each recorded as pass or
/// fail with what it saw. Expected values are the koans' reference answers.
module Plasm.Koans.Browser.Scenarios

open System
open System.Diagnostics
open System.Threading.Tasks
open Plasm
open Plasm.PGlite
open Plasm.Koans.Shop

exception Expectation of string

let private expect (what: string) (expected: 'a) (actual: 'a) =
    if actual <> expected then
        raise (Expectation $"{what}: expected %A{expected}, got %A{actual}")

let private ok (r: Result<'a, 'e>) =
    match r with
    | Ok v -> v
    | Error e -> raise (Expectation $"expected Ok, got Error %A{e}")

let private oneLine (s: string) =
    Text.RegularExpressions.Regex.Replace(s, @"\s+", " ")

/// Runs every scenario, calling `report name passed detail` for each.
let run (bridge: IPGliteBridge) (report: Action<string, bool, string>) : Task =
    task {
        let commands = ResizeArray<string>()

        let check (name: string) (body: unit -> Task<string>) =
            task {
                commands.Clear()
                let sw = Stopwatch.StartNew()

                try
                    let! detail = body ()
                    report.Invoke(name, true, $"{detail} [{sw.ElapsedMilliseconds} ms]")
                with
                | Expectation m -> report.Invoke(name, false, m)
                | ex -> report.Invoke(name, false, ex.ToString())
            }

        let sw = Stopwatch.StartNew()

        let setup (sql: string) =
            match bridge.Execute sql with
            | ""
            | null -> ()
            | error -> failwith error

        setup KoansData.schema
        setup KoansData.data
        let! provider = PGlite.connect bridge

        let options =
            { RepoOptions.Default with
                OnCommand = Some(fun e -> commands.Add e.Sql) }

        let repo = Repo(Db.model, provider, options)
        report.Invoke("Schema and starting data", true, $"created in {sw.ElapsedMilliseconds} ms")

        let customers (r: Repo) =
            task {
                let! n = r.Count(Query.from Db.customers)
                return ok n
            }

        do!
            check "Filtered query" (fun () ->
                task {
                    let! rows = repo.All(plasm { for o in Db.orders do where (o.Status = Published); orderBy o.Id })
                    let rows = ok rows
                    rows |> List.map (fun o -> o.Id) |> expect "ids" [ OrderId 1L; OrderId 3L ]
                    rows |> List.map (fun o -> o.Total) |> expect "totals" [ 30.00m; 99.00m ]
                    let first = List.head rows
                    first.PlacedAt |> expect "placed_at" (DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc))
                    first.PlacedAt.Kind |> expect "DateTime kind" DateTimeKind.Utc
                    first.CustomerId |> expect "customer" (Some(CustomerId 1L))
                    return $"{rows.Length} rows; SQL: {oneLine commands[0]}"
                })

        do!
            check "Values cross the boundary (options, decimals, timestamps)" (fun () ->
                task {
                    let! o = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId 4L) })
                    let o = (ok o).Value
                    o.CustomerId |> expect "null customer" None
                    o.CancellationReason |> expect "reason" (Some "duplicate")
                    o.Status |> expect "status" Cancelled
                    o.Total |> expect "total" 5.00m
                    string o.Total |> expect "decimal scale kept" "5.00"
                    o.Version |> expect "version" 1
                    return $"%A{(o.Id, o.CustomerId, o.Status, o.Total, o.CancellationReason, o.PlacedAt, o.Version)}"
                })

        do!
            check "Preload of Lines and Payments" (fun () ->
                task {
                    let! orders = repo.All(plasm { for o in Db.orders do preload o.Lines; preload o.Payments })
                    let orders = ok orders
                    let first = orders |> List.find (fun o -> o.Id = OrderId 1L)
                    commands.Count |> expect "commands" 3
                    (first.Lines |> Assoc.get |> List.length, first.Payments |> Assoc.get |> List.length) |> expect "loaded" (2, 1)
                    let line = first.Lines |> Assoc.get |> List.head
                    (line.Quantity.Value, line.UnitPrice) |> expect "first line" (2, 10.00m)
                    (orders |> List.find (fun o -> o.Id = OrderId 4L)).Lines |> expect "order 4 lines" (Loaded [])
                    return $"{commands.Count} commands; lines load: {oneLine commands[1]}"
                })

        do!
            check "Join" (fun () ->
                task {
                    let pairs =
                        plasm {
                            for o in Db.orders do
                                join c in Db.customers on (o.CustomerId = Some c.Id)
                                orderBy o.Id
                        }

                    let! rows = repo.All pairs

                    ok rows
                    |> List.map (fun (o, c) -> o.Id, c.Name)
                    |> expect "pairs" [ OrderId 1L, "Ada"; OrderId 2L, "Ada"; OrderId 3L, "Bob" ]

                    return $"SQL: {oneLine commands[0]}"
                })

        do!
            check "Count" (fun () ->
                task {
                    let! n = repo.Count(plasm { for o in Db.orders do where (o.Status = Published) })
                    ok n |> expect "published" 2L
                    let! page = repo.Count(plasm { for o in Db.orders do orderBy o.Id; take 2 })
                    ok page |> expect "page" 2L
                    return $"published = {ok n}"
                })

        do!
            check "Aggregates and groupBy" (fun () ->
                task {
                    let! totals = repo.One(plasm { for o in Db.orders do select (Agg.sum o.Total, Agg.avg o.Total) })
                    (ok totals).Value |> expect "sum, avg" (146.50m, Some 36.625m)

                    let perCustomer =
                        plasm {
                            for o in Db.orders do
                                groupBy o.CustomerId
                                orderBy o.CustomerId
                                select (o.CustomerId, Agg.count ())
                        }

                    let! rows = repo.All perCustomer
                    ok rows |> expect "groups" [ None, 1L; Some(CustomerId 1L), 2L; Some(CustomerId 2L), 1L ]
                    return $"%A{(ok totals).Value}; %A{ok rows}"
                })

        do!
            check "Snapshot read (REPEATABLE READ READ ONLY)" (fun () ->
                task {
                    let! orders = repo.All(plasm { for o in Db.orders do preload o.Lines }, consistency = ReadConsistency.Snapshot)
                    (ok orders).Length |> expect "orders" 4
                    return $"{commands.Count} commands in one snapshot"
                })

        do!
            check "Insert returning its generated key" (fun () ->
                task {
                    let! stored = repo.Insert(Changeset.insert (New.product "SPROCKET" "Sprocket"))
                    let stored = ok stored
                    (stored.Id = ProductId 0L) |> expect "key generated" false
                    let! back = repo.One(plasm { for p in Db.products do where (p.Sku = "SPROCKET") })
                    (ok back).Value.Id |> expect "read back" stored.Id
                    return $"%A{stored.Id}; SQL: {oneLine commands[0]}"
                })

        do!
            check "Update with optimistic concurrency" (fun () ->
                repo.Sandbox(fun repo ->
                    task {
                        let! first = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId 1L) })
                        let first = (ok first).Value
                        let! updated = repo.Update(first |> Changeset.edit (fun o -> { o with Total = 31m }))
                        let updated = ok updated
                        (updated.Total, updated.Version) |> expect "updated" (31m, 2)
                        let update = commands |> Seq.find (fun c -> c.StartsWith "UPDATE")
                        let! again = repo.Update(first |> Changeset.edit (fun o -> { o with Total = 32m }))

                        match again with
                        | Error(WriteError.Stale _) -> ()
                        | other -> raise (Expectation $"expected Stale, got %A{other}")

                        return $"version {first.Version} -> {updated.Version}, stale second update refused; SQL: {oneLine update}"
                    }))

        do!
            check "Unique violation names its constraint" (fun () ->
                task {
                    let! r = repo.Insert(Changeset.insert (New.customer "Ada again" "ada@example.com"))

                    match r with
                    | Error(WriteError.Failed f) ->
                        (f.Kind, f.SqlState, f.Constraint)
                        |> expect "failure" (DbFailureKind.UniqueViolation, Some "23505", Some "customers_email_key")

                        return $"{f.Kind} {f.SqlState.Value} {f.Constraint.Value}: {f.Message}"
                    | other -> return raise (Expectation $"expected a database failure, got %A{other}")
                })

        do!
            check "Unique violation as a field error" (fun () ->
                task {
                    let cs =
                        Changeset.insert (New.customer "Ada again" "ada@example.com")
                        |> Changeset.uniqueConstraint ((fun (c: Customer) -> c.Email), "customers_email_key")

                    let! r = repo.Insert cs

                    match r with
                    | Error(WriteError.Invalid cs) -> return $"%A{cs.Errors}"
                    | other -> return raise (Expectation $"expected an invalid changeset, got %A{other}")
                })

        do!
            check "Transaction whose failing step rolls back" (fun () ->
                task {
                    let signUp =
                        transaction {
                            let! dan = Tx.insert "customer" (Changeset.insert (New.customer "Dan" "dan@example.com"))
                            let! _ = Tx.insert "order" (Changeset.insert (New.order (Some dan.Id) Draft -1m))
                            return ()
                        }

                    let! r = repo.Transact signUp

                    match r with
                    | Ok _ -> return raise (Expectation "expected the transaction to fail")
                    | Error e ->
                        e.Step |> expect "step" "order"
                        e.RolledBack |> expect "rolled back" [ "customer" ]
                        let! n = customers repo
                        n |> expect "customers after" 3L
                        return $"failed at '{e.Step}', rolled back %A{e.RolledBack}, customers = {n}"
                })

        do!
            check "Transaction that commits" (fun () ->
                task {
                    let both =
                        transaction {
                            let! eve = Tx.insert "customer" (Changeset.insert (New.customer "Eve" "eve@example.com"))
                            let! o = Tx.insert "order" (Changeset.insert (New.order (Some eve.Id) Draft 1m))
                            return o.Id
                        }

                    let! r = repo.Transact both
                    let id = ok r
                    let! n = customers repo
                    n |> expect "customers after" 4L
                    return $"committed order %A{id}; customers = {n}"
                })

        do!
            check "Sandbox with a nested sandbox" (fun () ->
                task {
                    let! before = customers repo

                    let! seen =
                        repo.Sandbox(fun outer ->
                            task {
                                let! _ = outer.Insert(Changeset.insert (New.customer "Fay" "fay@example.com"))
                                let! inOuter = customers outer

                                // A database error does not end the sandbox.
                                let! dup = outer.Insert(Changeset.insert (New.customer "Fay" "fay@example.com"))

                                match dup with
                                | Error(WriteError.Failed f) -> f.Kind |> expect "duplicate" DbFailureKind.UniqueViolation
                                | other -> raise (Expectation $"expected a unique violation, got %A{other}")

                                let! inNested =
                                    outer.Sandbox(fun nested ->
                                        task {
                                            let! _ = nested.Insert(Changeset.insert (New.customer "Gus" "gus@example.com"))
                                            return! customers nested
                                        })

                                let! afterNested = customers outer

                                // A transaction inside a sandbox is a savepoint.
                                let failing =
                                    transaction {
                                        let! _ = Tx.insert "customer" (Changeset.insert (New.customer "Hal" "hal@example.com"))
                                        return! Tx.fail "stop" "changed my mind"
                                    }

                                let! _ = outer.Transact failing
                                let! afterTx = customers outer
                                return inOuter, inNested, afterNested, afterTx
                            })

                    let! after = customers repo
                    let b = before

                    (seen, after) |> expect "counts" ((b + 1L, b + 2L, b + 1L, b + 1L), b)
                    return $"before {b}; outer {seen}; after {after}"
                })

        do!
            check "Overlapping reads share the one session" (fun () ->
                task {
                    let reads =
                        [ for i in 1L .. 4L ->
                              task {
                                  let! o = repo.One(plasm { for o in Db.orders do where (o.Id = OrderId i); preload o.Lines })
                                  return (ok o).Value.Id
                              } ]

                    let! ids = Task.WhenAll reads
                    List.ofArray ids |> expect "ids" [ OrderId 1L; OrderId 2L; OrderId 3L; OrderId 4L ]
                    return $"{ids.Length} concurrent reads, {commands.Count} commands"
                })

        do!
            check "Hand-written SQL with an array parameter" (fun () ->
                task {
                    let ids = [| 1L; 3L |]
                    let! n = repo.ExecuteSql $"update customers set name = name where id = any({ids})"
                    ok n |> expect "rows" 2
                    return $"{ok n} rows; SQL: {oneLine commands[0]}"
                })

        report.Invoke("Done", true, $"total {sw.ElapsedMilliseconds} ms")
    }
    :> Task
