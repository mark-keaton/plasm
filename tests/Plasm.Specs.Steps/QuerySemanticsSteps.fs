namespace Plasm.Specs

open System.Collections.Generic
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Module-level values captured by queries (static properties, not closures).
module ModuleLevel =
    let statuses = [| Published |]
    let statusList = ResizeArray [ Published ]

    let reset () =
        statuses.[0] <- Published
        statusList.Clear()
        statusList.Add Published

/// Feature: Queries keep their F# meaning
/// (Data from "Orders with and without a Customer": Ada has orders 1 and 2,
/// Bob order 3, order 4 is a draft without a Customer.)
[<Binding>]
type QuerySemanticsSteps(w: World) =

    let ids (os: Order list) = os |> List.map (fun o -> o.Id) |> List.sort

    let everyOrder () =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id })
            return ok r
        }

    /// Runs the query and evaluates the same predicate over every Order in F#.
    let compare (key: string) (query: Query<Order>) (predicate: Order -> bool) =
        task {
            let! rows = w.Repo.All query
            let! all = everyOrder ()
            w.Set(key, (ok rows |> ids, all |> List.filter predicate |> ids))
        }

    let result key = w.Get<OrderId list * OrderId list> key

    [<StepDefinition("Orders with and without a Customer and with and without a cancellation reason")>]
    member _.WithReasons() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! o1 = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db (Some ada) "published" 20m (Seed.at 2)
            let! o3 = Seed.order w.Db (Some bob) "published" 30m (Seed.at 3)
            let! o4 = Seed.order w.Db None "draft" 40m (Seed.at 4)
            w.Set("ada", ada)
            w.Set("orders", [ o1; o2; o3; o4 ])
            w.Set("withoutCustomer", o4)
            let (OrderId third) = o3
            let! _ = w.Db.Exec("update orders set cancellation_reason = 'late' where id = @id", [ "id", box third ])
            return ()
        }
        |> unit

    [<StepDefinition("I keep Orders that are not for a given Customer, written with not")>]
    member _.NotForCustomer() =
        let ada: CustomerId = w.Get "ada"

        compare "single" (plasm { for o in tableOf<Order> do where (not (o.CustomerId = Some ada)) }) (fun o ->
            not (o.CustomerId = Some ada))
        |> unit

    [<StepDefinition("the Orders without a Customer are included")>]
    member _.WithoutIncluded() =
        let rows, _ = result "single"
        check (List.contains (w.Get<OrderId> "withoutCustomer") rows) $"the Order without a Customer is missing: %A{rows}"

    [<StepDefinition("the result equals evaluating the predicate over the same Orders in F#")>]
    member _.EqualsFSharp() =
        let rows, expected = result "single"
        equal expected rows "rows compared with F#"

    [<StepDefinition("I filter Orders on (.*) with not and with <>")>]
    member _.NotAndDifferent(field: string) =
        task {
            let ada: CustomerId = w.Get "ada"

            let negated, different, predicate =
                match field with
                | "CustomerId" ->
                    plasm { for o in tableOf<Order> do where (not (o.CustomerId = Some ada)) },
                    plasm { for o in tableOf<Order> do where (o.CustomerId <> Some ada) },
                    (fun (o: Order) -> o.CustomerId <> Some ada)
                | "CancellationReason" ->
                    plasm { for o in tableOf<Order> do where (not (o.CancellationReason = Some "late")) },
                    plasm { for o in tableOf<Order> do where (o.CancellationReason <> Some "late") },
                    (fun (o: Order) -> o.CancellationReason <> Some "late")
                | "Status" ->
                    plasm { for o in tableOf<Order> do where (not (o.Status = Published)) },
                    plasm { for o in tableOf<Order> do where (o.Status <> Published) },
                    (fun (o: Order) -> o.Status <> Published)
                | f -> failwithf "unknown field %s" f

            do! compare "negated" negated predicate
            do! compare "different" different predicate
        }
        |> unit

    [<StepDefinition("both filters return the same Orders")>]
    member _.SameOrders() =
        equal (fst (result "negated")) (fst (result "different")) "not (a = b) compared with a <> b"

    [<StepDefinition("both equal evaluating the predicate over the same Orders in F#")>]
    member _.BothEqualFSharp() =
        for key in [ "negated"; "different" ] do
            let rows, expected = result key
            equal expected rows $"{key} compared with F#"

    [<StepDefinition("I keep Orders for which \"for a given Customer and published\" is false")>]
    member _.Compound() =
        let ada: CustomerId = w.Get "ada"

        compare
            "single"
            (plasm { for o in tableOf<Order> do where (not (o.CustomerId = Some ada && o.Status = Published)) })
            (fun o -> not (o.CustomerId = Some ada && o.Status = Published))
        |> unit

    // Rule: Captured values are read each time the query runs

    [<StepDefinition("I build a query that filters on the statuses in a captured (.*)")>]
    member _.Captured(container: string) =
        // Each container starts with Published and is then changed to Draft.
        let query, change =
            match container with
            | "array" ->
                let statuses = [| Published |]
                plasm { for o in tableOf<Order> do where (Array.contains o.Status statuses) }, (fun () -> statuses.[0] <- Draft)
            | "reference" ->
                let status = ref Published
                plasm { for o in tableOf<Order> do where (o.Status = status.Value) }, (fun () -> status.Value <- Draft)
            | "ResizeArray" ->
                let statuses = ResizeArray [ Published ]

                plasm { for o in tableOf<Order> do where (statuses.Contains o.Status) },
                (fun () ->
                    statuses.Clear()
                    statuses.Add Draft)
            | c -> failwithf "unknown container %s" c

        w.Set("captured", query)
        w.Set("change", change)

    [<StepDefinition("I run it, change the (.*) and run the same query again")>]
    member _.RunChangeRun(_container: string) =
        task {
            let q: Query<Order> = w.Get "captured"
            let! first = w.Repo.All q
            w.Get<unit -> unit> "change" ()
            let! second = w.Repo.All q
            w.Set("runs", (ok first |> ids, ok second |> ids))
        }
        |> unit

    [<StepDefinition("the second run reflects the changed (.*)")>]
    member _.Reflects(_container: string) =
        let first, second = w.Get<OrderId list * OrderId list> "runs"
        let orders: OrderId list = w.Get "orders"
        equal (orders |> List.take 3) first "first run (published)"
        equal [ w.Get<OrderId> "withoutCustomer" ] second "second run (draft)"

    [<StepDefinition("I run a query that captures only immutable values twice")>]
    member _.Immutable() =
        task {
            let statuses = [ Published; Cancelled ]
            let ada: CustomerId = w.Get "ada"
            let q = plasm { for o in tableOf<Order> do where (List.contains o.Status statuses && o.CustomerId = Some ada) }
            let repo = w.Db.RepoWith()
            let! _ = repo.All q
            let! _ = repo.All q
            w.Set("plans", repo.PlansMade)
        }
        |> unit

    [<StepDefinition("it is translated once")>]
    member _.TranslatedOnce() = equal 1 (w.Get<int> "plans") "plans made"

    [<StepDefinition("I explain a query that negates a comparison of a required field")>]
    member _.ExplainRequiredNot() =
        w.Set("sql", (w.Repo.Explain(plasm { for o in tableOf<Order> do where (not (o.Total > 15m)) }) |> ok).Sql)

    [<StepDefinition("the SQL writes the opposite comparison, so an index on the field can still be used")>]
    member _.OppositeComparison() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "\"o\".\"total\" <= @p0" && not (sql.Contains "NOT")) $"expected total <= 15: {sql}"

    // SQLite compares decimals stored as text by plasm_decimal_compare, which
    // no index on the column serves.
    [<StepDefinition("the SQL writes the opposite comparison of the decimals by value")>]
    member _.OppositeByValue() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "plasm_decimal_compare(\"o\".\"total\", @p0) <= 0" && not (sql.Contains "NOT")) $"expected total <= 15: {sql}"

    [<StepDefinition("I run a query comparing with DateTime.UtcNow twice, a moment apart")>]
    member _.Clock() =
        task {
            let repo = w.Db.RepoWith(includeParameterValues = true)
            let q = plasm { for o in tableOf<Order> do where (o.PlacedAt < System.DateTime.UtcNow) }
            w.MarkCommands()
            let! _ = repo.All q
            // Diagnostics record times to the second.
            do! System.Threading.Tasks.Task.Delay 1100
            let! _ = repo.All q
            w.Set("times", w.QueryCommands |> List.map (fun c -> c.Parameters |> List.map snd |> List.head))
            w.Set("timesKind", "recorded")
        }
        |> unit

    [<StepDefinition("each run sends the time at which it ran")>]
    member _.TimesDiffer() =
        match w.Get<string option list> "times" with
        | [ Some a; Some b ] -> check (a <> b) $"both runs sent the same time: {a}"
        | other -> failwithf "expected two commands with a recorded time, got %A" other

    [<StepDefinition("^I build a query that filters on the statuses in a module-level (array|ResizeArray)$")>]
    member _.ModuleCaptured(container: string) =
        ModuleLevel.reset ()

        let query, change =
            match container with
            | "array" ->
                plasm { for o in tableOf<Order> do where (Array.contains o.Status ModuleLevel.statuses) },
                (fun () -> ModuleLevel.statuses.[0] <- Draft)
            | _ ->
                plasm { for o in tableOf<Order> do where (ModuleLevel.statusList.Contains o.Status) },
                (fun () ->
                    ModuleLevel.statusList.Clear()
                    ModuleLevel.statusList.Add Draft)

        w.Set("captured", query)
        w.Set("change", change)

    // Rule: Union cases stored as text compare in the order F# declares them

    [<StepDefinition("Orders in every status")>]
    member _.EveryStatus() =
        task {
            // Seeded so that neither insertion order nor the stored text agrees
            // with the declaration (Draft, Published, Cancelled).
            let! a = Seed.order w.Db None "cancelled" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! c = Seed.order w.Db None "draft" 30m (Seed.at 3)
            let! d = Seed.order w.Db None "cancelled" 40m (Seed.at 4)
            let! e = Seed.order w.Db None "draft" 50m (Seed.at 5)
            w.Set("orders", [ a; b; c; d; e ])
            return ()
        }
        |> unit

    [<StepDefinition("^I order Orders by Status (ascending|descending)$")>]
    member _.OrderByStatus(direction: string) =
        task {
            let query =
                if direction = "ascending" then
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.Status
                            thenBy o.Id
                    }
                else
                    plasm {
                        for o in tableOf<Order> do
                            orderByDescending o.Status
                            thenBy o.Id
                    }

            let! rows = w.Repo.All query
            let! all = everyOrder ()

            let expected =
                if direction = "ascending" then
                    all |> List.sortBy (fun o -> o.Status, o.Id)
                else
                    all |> List.sortBy (fun o -> o.Id) |> List.sortByDescending (fun o -> o.Status)

            w.Set("sorted", (ok rows |> List.map (fun o -> o.Id), expected |> List.map (fun o -> o.Id)))
        }
        |> unit

    [<StepDefinition("the Orders come in the order sorting them by Status in F# gives")>]
    member _.SortedAsFSharp() =
        let rows, expected = w.Get<OrderId list * OrderId list> "sorted"
        equal expected rows "Orders sorted by Status"

    [<StepDefinition("^I keep Orders whose Status (<|<=|>|>=) Published$")>]
    member _.CompareStatus(comparison: string) =
        let query, predicate =
            match comparison with
            | "<" -> plasm { for o in tableOf<Order> do where (o.Status < Published) }, (fun (o: Order) -> o.Status < Published)
            | "<=" -> plasm { for o in tableOf<Order> do where (o.Status <= Published) }, (fun o -> o.Status <= Published)
            | ">" -> plasm { for o in tableOf<Order> do where (o.Status > Published) }, (fun o -> o.Status > Published)
            | _ -> plasm { for o in tableOf<Order> do where (o.Status >= Published) }, (fun o -> o.Status >= Published)

        compare "single" query predicate |> unit

    [<StepDefinition("I run a prepared query for Orders whose Status is before a given status, with Cancelled")>]
    member _.PreparedBefore() =
        task {
            let before =
                Query.prepare (fun (status: Arg<OrderStatus>) ->
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Status < status.Value)
                            orderBy o.Id
                    })

            let! rows = w.Repo.All(before Cancelled)
            w.Set("before", ok rows |> List.map (fun o -> o.Status) |> List.distinct |> List.sort)
        }
        |> unit

    [<StepDefinition("the Draft and Published Orders are returned")>]
    member _.DraftAndPublished() =
        equal [ Draft; Published ] (w.Get<OrderStatus list> "before") "statuses returned"

    [<StepDefinition("I select the minimum and maximum Status of the Orders")>]
    member _.MinMaxStatus() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do select (Agg.min o.Status, Agg.max o.Status) })
            w.Set("minMax", ok r)
        }
        |> unit

    [<StepDefinition("they are Draft and Cancelled")>]
    member _.DraftAndCancelled() =
        equal [ Some Draft, Some Cancelled ] (w.Get<(OrderStatus option * OrderStatus option) list> "minMax") "minimum and maximum Status"

    [<StepDefinition("I explain a query that orders Orders by Status")>]
    member _.ExplainOrderByStatus() =
        w.Set("sql", (w.Repo.Explain(plasm { for o in tableOf<Order> do orderBy o.Status }) |> ok).Sql)

    [<StepDefinition("the SQL orders by the position of each stored name in the declaration")>]
    member _.OrdersByPosition() =
        let sql = w.Get<string> "sql"
        check
            (sql.Contains "ORDER BY (CASE \"o\".\"status\" WHEN 'draft' THEN 0 WHEN 'published' THEN 1 WHEN 'cancelled' THEN 2 END)")
            $"the ordering is not by declaration position: {sql}"

    // -- String functions ----------------------------------------------------

    /// Names padded on both sides with each kind of whitespace .NET trims.
    static member PaddedNames =
        [ " Ada "
          "\tBob\n"
          "\u00A0Cy\u00A0"
          "\u2003\u3000Dee\u2028\u0085"
          "\r\v\fEve\u202F\u205F"
          "Fay" ]

    [<StepDefinition("Customers whose names are padded with spaces, tabs, line breaks and Unicode spaces")>]
    member _.Padded() =
        task {
            for i, name in List.indexed QuerySemanticsSteps.PaddedNames do
                let! _ = Seed.customer w.Db name $"padded{i}@example.com"
                ()
        }
        |> unit

    [<StepDefinition("^I read the Customers' names through (Trim|TrimStart|TrimEnd)$")>]
    member _.Trimmed(f: string) =
        task {
            let! r =
                match f with
                | "Trim" -> w.Repo.All(plasm { for c in tableOf<Customer> do orderBy c.Email; select (c.Name, c.Name.Trim()) })
                | "TrimStart" -> w.Repo.All(plasm { for c in tableOf<Customer> do orderBy c.Email; select (c.Name, c.Name.TrimStart()) })
                | _ -> w.Repo.All(plasm { for c in tableOf<Customer> do orderBy c.Email; select (c.Name, c.Name.TrimEnd()) })

            match r with
            | Ok rows -> w.Set("trimmed", rows)
            | Error e -> failwithf "the read failed: %A" e
        }
        |> unit

    [<StepDefinition("^each name read equals (Trim|TrimStart|TrimEnd) of the stored name in F#$")>]
    member _.TrimmedAre(f: string) =
        let rows = w.Get<(string * string) list> "trimmed"
        equal QuerySemanticsSteps.PaddedNames.Length rows.Length "names read"

        for stored, read in rows do
            let expected =
                match f with
                | "Trim" -> stored.Trim()
                | "TrimStart" -> stored.TrimStart()
                | _ -> stored.TrimEnd()

            equal expected read $"{f} of %A{stored}"

    [<StepDefinition("I read the Orders over twice a captured total less one, computed in the query")>]
    member _.ComputedTotal() =
        task {
            let total = 30m
            w.MarkCommands()
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do where (o.Total > total * 2m - 1m) })
            let! all = w.Repo.All(plasm { for o in tableOf<Order> do select o })
            w.Set("computed", (ok r, ok all, w.QueryCommands |> List.head))
        }
        |> unit

    [<StepDefinition("the Orders are those over that total, and the SQL has one parameter for it")>]
    member _.ComputedTotalRead() =
        let (read: Order list), (all: Order list), (command: CommandEvent) = w.Get<Order list * Order list * CommandEvent> "computed"
        let ids (os: Order list) = os |> List.map (fun o -> o.Id) |> List.sort
        equal (all |> List.filter (fun o -> o.Total > 59m) |> ids) (ids read) "the Orders over 59"
        equal 1 command.Parameters.Length $"parameters of {command.Sql}"

    [<StepDefinition("I read the Orders whose total is the sum of a captured list of decimals, computed in the query")>]
    member _.GenericOverValueType() =
        task {
            let totals = [ 10m; 20m ]
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do where (o.Total = List.sum totals) })
            w.Set("genericRead", Result.map ignore r)
        }
        |> unit

    [<StepDefinition("the read is refused, naming List.sum and saying to bind its value outside the query")>]
    member _.GenericRefused() =
        match w.Get<Result<unit, DbError>> "genericRead" with
        | Error e ->
            let text = string e
            check (text.Contains "List.sum" && text.Contains "bind" && text.Contains "outside the query") text
        | Ok() -> failwith "expected a refusal, the query ran"

    [<StepDefinition("^I read the Customers whose name (=|<>) a captured null string$")>]
    member _.CapturedNull(op: string) =
        task {
            let name: string = null

            let! r =
                if op = "=" then
                    w.Repo.All(plasm { for c in tableOf<Customer> do where (c.Name = name) })
                else
                    w.Repo.All(plasm { for c in tableOf<Customer> do where (c.Name <> name) })

            w.Set("nullRead", r)
        }
        |> unit

    [<StepDefinition("I run a prepared query for the Customers named an argument, with null")>]
    member _.PreparedNull() =
        task {
            let named = Query.prepare (fun (name: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name = name.Value) })
            let! r = w.Repo.All(named null)
            w.Set("nullRead", r)
        }
        |> unit

    [<StepDefinition("^the read is refused, saying the (value|argument) is null and an option says a value may be absent$")>]
    member _.NullRefused(what: string) =
        match w.Get<Result<Customer list, DbError>> "nullRead" with
        | Error e ->
            let text = string e
            check (text.Contains "null" && text.Contains "option" && text.Contains what) text
        | Ok rows -> failwithf "expected a refusal, got %d Customers" rows.Length

    [<StepDefinition("^I read the Orders, written with (.+)$")>]
    member _.Clauses(clauses: string) =
        let unitOf (t: System.Threading.Tasks.Task<Result<'a, DbError>>) =
            task {
                let! r = t
                return Result.map ignore r
            }

        task {
            let! r =
                match clauses with
                | "take 2, then where" -> w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; take 2; where (o.Total > 0m) }) |> unitOf
                | "skip 1, then where" -> w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; skip 1; where (o.Total > 0m) }) |> unitOf
                | "take 2, then orderBy" -> w.Repo.All(plasm { for o in tableOf<Order> do take 2; orderBy o.Id }) |> unitOf
                | "take 2, then take 3" -> w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; take 2; take 3 }) |> unitOf
                | "skip 1, then skip 2" -> w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; skip 1; skip 2 }) |> unitOf
                | "take 2, then skip 1" -> w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; take 2; skip 1 }) |> unitOf
                | "groupBy, then where" ->
                    w.Repo.All(plasm { for o in tableOf<Order> do groupBy o.CustomerId; where (o.Total > 0m); select (o.CustomerId, Agg.count ()) }) |> unitOf
                | "take 2, then groupBy" ->
                    w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.CustomerId; take 2; groupBy o.CustomerId; select (o.CustomerId, Agg.count ()) }) |> unitOf
                | other -> failwithf "no query for %s" other

            w.Set("clausesRead", r)
        }
        |> unit

    [<StepDefinition("^the read is refused, saying (.+ is written .+)$")>]
    member _.ClausesRefused(reason: string) =
        match w.Get<Result<unit, DbError>> "clausesRead" with
        | Error(DbError.Translation e) ->
            let text = string e
            check (text.Contains reason) text
        | other -> failwithf "expected a refusal saying %s, got %A" reason other

    [<StepDefinition("I read the Orders by id with skip 1, then take 2")>]
    member _.SkipTake() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; skip 1; take 2 })
            let! all = everyOrder ()
            w.Set("skipTake", (ids (ok r), all |> ids |> List.skip 1 |> List.take 2))
        }
        |> unit

    [<StepDefinition("the second and third Orders by id are read, as F# pages them")>]
    member _.SecondAndThird() =
        let got, expected = w.Get<OrderId list * OrderId list> "skipTake"
        equal expected got "the second and third Orders"
