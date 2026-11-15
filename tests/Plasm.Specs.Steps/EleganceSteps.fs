namespace Plasm.Specs

open System
open System.Reflection
open System.Threading.Tasks
open Npgsql
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rules from the elegance specification.
[<Binding>]
type EleganceSteps(w: World) =

    let compiles (code: string) =
        task {
            let! r = CompileCheck.check code
            check r.Compiles ("expected to compile:\n" + code + "\n" + String.concat "\n" r.Errors)
        }

    let rejectedWith (errorCode: string) (code: string) =
        task {
            let! r = CompileCheck.check code
            check (r.Errors |> List.exists (fun e -> e.StartsWith errorCode)) $"expected {errorCode} for:\n{code}\ngot: %A{r.Errors}"
        }

    let rejected = rejectedWith "FS0001"

    let all (q: Query<'T>) =
        task {
            let! r = w.Repo.All q
            return ok r
        }

    // == Rule: Ordinary F# domain types remain usable ==========================

    [<StepDefinition("an immutable F# record with supported mapped fields")>]
    member _.ImmutableRecord() = w.Set("type", typeof<Order>)

    [<StepDefinition("I configure its persistence mapping")>]
    member _.Configure() =
        check (List.contains typeof<Order> w.Db.Model.Types) "Order is mapped by Schemas.orders"

    [<StepDefinition("I do not need to add public setters")>]
    member _.NoSetters() =
        for p in w.Get<Type>("type").GetProperties() do
            check (isNull (p.GetSetMethod())) $"{p.Name} has no public setter"

    [<StepDefinition("I do not need to add a parameterless constructor")>]
    member _.NoParameterlessCtor() =
        let ctors = w.Get<Type>("type").GetConstructors()
        check (ctors |> Array.forall (fun c -> c.GetParameters().Length > 0)) "no parameterless constructor"

    [<StepDefinition("I do not need to inherit from a persistence base class")>]
    member _.NoBaseClass() = equal typeof<obj> (w.Get<Type>("type").BaseType) "base type"

    // Scenario: Domain scalar wrappers preserve their meaning

    [<StepDefinition("distinct CustomerId and OrderId types")>]
    member _.DistinctIds() = check (typeof<CustomerId> <> typeof<OrderId>) "distinct types"

    [<StepDefinition("each has a configured scalar mapping")>]
    member _.Mapped() =
        for t in [ typeof<CustomerId>; typeof<OrderId> ] do
            check (Schemas.scalars |> List.exists (fun s -> s.Domain = t)) $"{t.Name} mapping"

    [<StepDefinition("I construct queries and changes")>]
    member _.ConstructQueriesAndChanges() =
        task {
            let! id = Seed.order w.Db None "draft" 1m (Seed.at 1)
            w.Set("id", id)
        }
        |> unit

    [<StepDefinition("those identifiers remain distinct at compile time")>]
    member _.DistinctAtCompileTime() =
        task {
            do! compiles "let q (id: OrderId) = plasm { for o in orders do where (o.Id = id) }"
            do! rejected "let q (id: CustomerId) = plasm { for o in orders do where (o.Id = id) }"
            do! compiles "let c (o: Order) (c: CustomerId) : Tx<Order, unit> = Tx.update (o, { o with CustomerId = Some c })"
            do! rejected "let c (o: Order) (i: OrderId) : Tx<Order, unit> = Tx.update (o, { o with CustomerId = Some i })"
        }
        |> unit

    [<StepDefinition("their database parameters use the configured scalar representation")>]
    member _.ScalarParameters() =
        task {
            let id: OrderId = w.Get "id"
            let repo = w.Db.RepoWith(includeParameterValues = true)
            w.MarkCommands()
            let! r = repo.All(plasm { for o in Schemas.orders do where (o.Id = id) })
            equal [ id ] (ok r |> List.map (fun o -> o.Id)) "found by wrapped id"
            let (OrderId raw) = id
            equal [ Some(string raw) ] (w.CommandsSinceMark.Head.Parameters |> List.map snd) "parameter is the int64 value"
        }
        |> unit

    // Scenario: Domain reconstruction can fail explicitly

    [<StepDefinition("a persisted value violates a domain constructor's requirements")>]
    member _.InvalidStoredValue() =
        task {
            let! p = Seed.product w.Db "SKU"
            let! o = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let! _ = Seed.line w.Db o p 3 1m
            let! _ = Seed.line w.Db o p 0 1m
            ()
        }
        |> unit

    [<StepDefinition("the library materializes the record")>]
    member _.Materialize() =
        task {
            let! r = w.Repo.All(plasm { for l in Schemas.orderLines do orderBy l.Id })
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("it reports a structured materialization error")>]
    member _.MaterializationError() =
        match w.Get<Result<OrderLine list, DbError>> "result" with
        | Error(DbError.Materialization m) ->
            equal ("OrderLine", "Quantity") (m.Model, m.Property) "location"
            check (m.Message.Contains "quantity must be positive") m.Message
            check (m.Value.StartsWith "0") m.Value
        | other -> failwithf "expected a materialization error, got %A" other

    [<StepDefinition("it does not bypass the constructor or invent a valid value")>]
    member _.NoBypass() =
        // The whole read fails: no partially valid list, no default Quantity.
        check (Result.isError (w.Get<Result<OrderLine list, DbError>> "result")) "no values returned"

    // == Rule: Query composition preserves relational meaning ==================

    [<StepDefinition("an Order has several Payments matching a relationship predicate")>]
    member _.SeveralMatching() =
        task {
            let! a = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! b = Seed.order w.Db None "published" 20m (Seed.at 2)
            let! c = Seed.order w.Db None "published" 30m (Seed.at 3)

            for i in 1..3 do
                let! _ = Seed.payment w.Db a 1m "succeeded" (Seed.at i)
                ()

            let! _ = Seed.payment w.Db b 1m "succeeded" (Seed.at 9)
            let! _ = Seed.payment w.Db c 1m "succeeded" (Seed.at 9)
            w.Set("ids", [ a; b; c ])
        }
        |> unit

    [<StepDefinition("I query Orders satisfying that predicate")>]
    member _.QuerySatisfying() =
        task {
            let q =
                plasm {
                    for o in Schemas.orders do
                        where (o.Payments |> Rel.any (fun p -> p.Status = Succeeded))
                        orderBy o.Id
                }

            let! everything = all q
            let! page = all (plasm { for o in q do take 2 })
            w.Set("everything", everything)
            w.Set("page", page)
        }
        |> unit

    [<StepDefinition("the Order appears once")>]
    member _.AppearsOnce() =
        equal (w.Get<OrderId list> "ids") (w.Get<Order list> "everything" |> List.map (fun o -> o.Id)) "orders"

    [<StepDefinition("parent pagination is not affected by the number of matching Payments")>]
    member _.PaginationUnaffected() =
        equal (w.Get<OrderId list> "ids" |> List.take 2) (w.Get<Order list> "page" |> List.map (fun o -> o.Id)) "page"

    // Scenario: Optional relationships preserve parent rows

    [<StepDefinition("an Order has no optional Customer relationship")>]
    member _.NoCustomer() =
        task {
            let! c = Seed.customer w.Db "Ada" "ada@example.com"
            let! withCustomer = Seed.order w.Db (Some c) "published" 1m (Seed.at 1)
            let! without = Seed.order w.Db None "published" 2m (Seed.at 2)
            w.Set("ids", [ withCustomer; without ])
        }
        |> unit

    [<StepDefinition("I load that relationship")>]
    member _.LoadCustomer() =
        task {
            let! os = all (plasm { for o in Schemas.orders do orderBy o.Id; preload o.Customer })
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("the Order remains in the result")>]
    member _.Remains() =
        equal (w.Get<OrderId list> "ids") (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "orders"

    [<StepDefinition("the relationship is represented as loaded and absent")>]
    member _.LoadedAbsent() =
        let orders: Order list = w.Get "orders"
        equal (Loaded None) orders.[1].Customer "missing customer"
        check (match orders.[0].Customer with Loaded(Some c) -> c.Name = "Ada" | _ -> false) "present customer"

    // Scenario: Null comparisons have explicit semantics

    [<StepDefinition("a mapped optional field")>]
    member _.OptionalField() =
        task {
            let! a = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let! b = Seed.order w.Db None "cancelled" 2m (Seed.at 2)
            let! _ = w.Db.Exec("update orders set cancellation_reason = 'x' where id = @id", [ "id", box (let (OrderId v) = b in v) ])
            w.Set("withoutReason", a)
        }
        |> unit

    [<StepDefinition("I query for values equal to None")>]
    member _.EqualNone() =
        task {
            w.MarkCommands()
            let! os = all (plasm { for o in Schemas.orders do where (o.CancellationReason = None) })
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("the database predicate tests for SQL NULL")>]
    member _.TestsNull() =
        check (w.CommandsSinceMark.Head.Sql.Contains "\"cancellation_reason\" IS NULL") w.CommandsSinceMark.Head.Sql
        equal [ w.Get<OrderId> "withoutReason" ] (w.Get<Order list> "orders" |> List.map (fun o -> o.Id)) "rows"

    [<StepDefinition("the comparison does not silently become SQL equality with NULL")>]
    member _.NotEqualsNull() =
        let sql = w.CommandsSinceMark.Head.Sql
        check (not (sql.Contains "= NULL")) sql
        equal [] w.CommandsSinceMark.Head.Parameters "no NULL parameter"

    // Scenario: Empty membership predicates remain valid

    [<StepDefinition("I filter Orders by membership in an empty identifier collection")>]
    member _.EmptyMembership() =
        task {
            let! _ = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let ids: OrderId list = []
            w.MarkCommands()
            let! r = w.Repo.All(plasm { for o in Schemas.orders do where (List.contains o.Id ids) })
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the result is empty")>]
    member _.ResultEmpty() = equal [] (ok (w.Get<Result<Order list, DbError>> "result")) "result"

    [<StepDefinition("no invalid SQL is generated")>]
    member _.ValidSql() =
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        check cmd.Succeeded "command succeeded"
        check (cmd.Sql.Contains "WHERE FALSE") cmd.Sql

    // Scenario: Pagination requires stable ordering

    [<StepDefinition("I paginate a query without a deterministic ordering")>]
    member _.PaginateUnordered() =
        task {
            for i in 1..7 do
                let! _ = Seed.order w.Db None (if i % 2 = 0 then "draft" else "published") 1m (Seed.at 1)
                ()

            w.MarkCommands()
            let! r = w.Repo.All(plasm { for o in Schemas.orders do take 3 })
            w.Set("unordered", r)
        }
        |> unit

    [<StepDefinition("the library requires an explicit ordering")>]
    member _.RequiresOrdering() =
        match w.Get<Result<Order list, DbError>> "unordered" with
        | Error(DbError.Translation(TranslationError.InvalidQuery msg)) -> check (msg.Contains "requires an explicit ordering") msg
        | other -> failwithf "expected InvalidQuery, got %A" other

    [<StepDefinition("Or applies a documented unique ordering")>]
    member _.UniqueOrdering() =
        task {
            // Ordering by a non-unique column gets the primary key as a tiebreaker.
            let page n = plasm { for o in Schemas.orders do orderBy o.Status; skip (n * 3); take 3 }
            let plan = w.Repo.Explain(page 0) |> ok
            check (plan.Sql.Contains "ORDER BY (CASE \"t0\".\"status\" WHEN 'draft' THEN 0 WHEN 'published' THEN 1 WHEN 'cancelled' THEN 2 END) ASC, \"t0\".\"id\" ASC") plan.Sql
            let! p0 = all (page 0)
            let! p1 = all (page 1)
            let! p2 = all (page 2)
            let ids = p0 @ p1 @ p2 |> List.map (fun o -> o.Id)
            equal 7 (List.distinct ids).Length "pages are disjoint and complete"
        }
        |> unit

    [<StepDefinition("it does not silently promise stable pages")>]
    member _.NoSilentPromise() =
        // The unordered page was rejected before any command ran.
        check (w.CommandsSinceMark |> List.forall (fun e -> e.Sql.Contains "ORDER BY")) "every executed page is ordered"
        equal 3 (w.CommandsSinceMark.Length) "only the ordered pages executed"

    // == Rule: Composition has consistent algebraic behavior ==================

    [<StepDefinition("two pure predicates combined by logical AND")>]
    member _.TwoPredicates() =
        task {
            for i in 1..12 do
                let! _ = Seed.order w.Db None (if i % 3 = 0 then "draft" else "published") (decimal i) (Seed.at i)
                ()
        }
        |> unit

    [<StepDefinition("I compose them in either order")>]
    member _.EitherOrder() =
        task {
            let isPublished (q: Query<Order>) = plasm { for o in q do where (o.Status = Published) }
            let isLarge (q: Query<Order>) = plasm { for o in q do where (o.Total > 5m) }
            let baseQ = plasm { for o in Schemas.orders do orderBy o.Id }
            let! a = all (baseQ |> isPublished |> isLarge)
            let! b = all (baseQ |> isLarge |> isPublished)
            w.Set("a", a)
            w.Set("b", b)
        }
        |> unit

    [<StepDefinition("both queries return equivalent results")>]
    member _.Equivalent() =
        let a: Order list = w.Get "a"
        equal a (w.Get<Order list> "b") "results"
        equal 4 a.Length "matching rows"

    // Scenario: Repeating an identical load does not duplicate work

    [<StepDefinition("two query-building functions request the same relationship load")>]
    member _.SameLoad() =
        task {
            let! id = Seed.order w.Db None "published" 1m (Seed.at 1)
            let! _ = Seed.payment w.Db id 1m "succeeded" (Seed.at 2)
            let! _ = Seed.payment w.Db id 2m "failed" (Seed.at 3)
            ()
        }
        |> unit

    [<StepDefinition("I compose them")>]
    member _.ComposeThem() =
        task {
            let a (q: Query<Order>) = plasm { for o in q do preload o.Payments successfulPayments }
            let b (q: Query<Order>) = plasm { for o in q do preload o.Payments successfulPayments }
            let q = plasm { for o in Schemas.orders do orderBy o.Id } |> a |> b
            w.Set("plan", w.Repo.Explain q |> ok)
            w.MarkCommands()
            let! os = all q
            w.Set("orders", os)
        }
        |> unit

    [<StepDefinition("the relationship is loaded once")>]
    member _.LoadedOnce() =
        equal [ "Payments" ] (w.Get<QueryPlan>("plan").LoadPaths) "planned loads"
        equal 1 (commandsOn "payments" w.CommandsSinceMark).Length "payment commands"

    [<StepDefinition("its records are not duplicated")>]
    member _.NotDuplicated() =
        let payments = Assoc.get (List.exactlyOne (w.Get<Order list> "orders")).Payments
        equal 1 payments.Length "payments"

    // Scenario: Empty transaction descriptions are valid

    [<StepDefinition("I execute a transaction description containing no operations")>]
    member _.EmptyTransaction() =
        task {
            w.MarkCommands()
            let! r = w.Repo.Transact(transaction { return () }: Tx<unit, string>)
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("it succeeds with an empty result")>]
    member _.EmptySucceeds() = equal (Ok()) (w.Get<Result<unit, TransactionError<string>>> "result") "result"

    [<StepDefinition("it performs no writes")>]
    member _.NoWrites() = equal 0 w.CommandsSinceMark.Length "commands"

    // == Rule: Partial data cannot accidentally become destructive writes ======

    [<StepDefinition("I query only an Order identifier and status")>]
    member _.ProjectIdStatus() =
        task {
            let! id = Seed.order w.Db None "draft" 42m (Seed.at 1)
            let! _ = w.Db.Exec("update orders set cancellation_reason = 'keep me'")

            let! r =
                w.Repo.All(
                    plasm {
                        for o in Schemas.orders do
                            where (o.Id = id)
                            select {| Id = o.Id; Status = o.Status; Version = o.Version |}
                    }
                )

            w.Set("summary", List.exactlyOne (ok r))
        }
        |> unit

    [<StepDefinition("I construct an update from that projection")>]
    member _.UpdateFromProjection() =
        task {
            let s: {| Id: OrderId; Status: OrderStatus; Version: int |} = w.Get "summary"

            // A projection is not a record: an update from it says which rows
            // and which fields, and nothing else.
            let byId = plasm { for o in Schemas.orders do where (o.Id = s.Id) }
            w.MarkCommands()
            let! r = w.Repo.Transact(Tx.execute (Bulk.update (byId, fun o -> { o with Status = Published })): Tx<int, unit>)
            equal 1 (ok r) "rows updated"
            let! read = w.Repo.One byId
            w.Set("updated", (ok read).Value)
        }
        |> unit

    [<StepDefinition("unselected fields cannot implicitly become defaults or NULL")>]
    member _.UnselectedKept() =
        task {
            let! total = w.Db.Scalar<decimal>("select total from orders")
            let! reason = w.Db.Scalar<string>("select cancellation_reason from orders")
            equal (42m, "keep me") (total, reason) "stored fields"
            let updated: Order = w.Get "updated"
            equal (42m, Some "keep me", Published) (updated.Total, updated.CancellationReason, updated.Status) "returned record"
        }
        |> unit

    [<StepDefinition("only explicitly described fields are eligible for update")>]
    member _.OnlyExplicit() =
        let update = w.CommandsSinceMark |> List.find (fun c -> c.Sql.StartsWith "UPDATE")
        let setClause = update.Sql.Substring(update.Sql.IndexOf " SET ", update.Sql.IndexOf " WHERE " - update.Sql.IndexOf " SET ")
        equal " SET \"status\" = @p0, \"version\" = (\"orders\".\"version\" + @p1)" setClause "SET clause"

    // Scenario: Filtered collections are not authoritative replacements

    [<StepDefinition("I load only successful Payments for an Order")>]
    member _.LoadSuccessfulOnly() =
        task {
            let! id = Seed.order w.Db None "published" 10m (Seed.at 1)
            let! _ = Seed.payment w.Db id 5m "succeeded" (Seed.at 2)
            let! _ = Seed.payment w.Db id 5m "succeeded" (Seed.at 3)
            let! _ = Seed.payment w.Db id 5m "failed" (Seed.at 4)
            let! os = all (plasm { for o in Schemas.orders do preload o.Payments successfulPayments })
            w.Set("order", List.exactlyOne os)
        }
        |> unit

    [<StepDefinition("I submit an unrelated Order change")>]
    member _.UnrelatedChange() =
        task {
            let! r = w.Repo.Update(w.Get<Order> "order" |> Changeset.edit (fun o -> { o with Status = Cancelled }))
            w.Set("order", ok r)
        }
        |> unit

    [<StepDefinition("unsuccessful Payments remain unchanged")>]
    member _.UnsuccessfulRemain() =
        task {
            let! failed = w.Db.Scalar<int64>("select count(*) from payments where status = 'failed'")
            let! total = w.Db.Scalar<int64>("select count(*) from payments")
            equal (1L, 3L) (failed, total) "stored payments"
        }
        |> unit

    [<StepDefinition("I attempt to replace Payments using the filtered collection")>]
    member _.ReplaceFiltered() =
        task {
            // Without a scope the call does not compile.
            do!
                rejectedWith
                    "FS0501"
                    "let r (o: Order) (ps: Changeset<Payment> list) = Changeset.update o |> Changeset.replaceChildren((fun o -> o.Payments), ps)"

            let order: Order = w.Get "order"
            let loaded = Assoc.get order.Payments |> List.map Changeset.update

            let cs =
                Changeset.update order
                |> Changeset.replaceChildren((fun o -> o.Payments), ReplaceScope.matching successfulPayments, loaded)

            let! r = w.Repo.Update cs
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the API requires explicit acknowledgement of replacement scope")>]
    member _.ScopeRequired() =
        task {
            // Scoped to successful payments, the replacement leaves the failed one alone.
            let! failed = w.Db.Scalar<int64>("select count(*) from payments where status = 'failed'")
            let! total = w.Db.Scalar<int64>("select count(*) from payments")
            equal (1L, 3L) (failed, total) "stored payments"
        }
        |> unit

    // Scenario: Duplicate child identities are rejected

    [<StepDefinition("an association changeset contains two entries for the same child identity")>]
    member _.DuplicateEntries() =
        task {
            let! p = Seed.product w.Db "SKU"
            let! id = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let! _ = Seed.line w.Db id p 1 1m
            let! r = w.Repo.One(plasm { for o in Schemas.orders do preload o.Lines })
            let order = (ok r).Value
            let line = Assoc.get order.Lines |> List.exactlyOne

            w.Set(
                "changeset",
                Changeset.update order
                |> Changeset.putChildren(
                    (fun o -> o.Lines),
                    [ line |> Changeset.edit (fun l -> { l with Quantity = qty 2 })
                      line |> Changeset.edit (fun l -> { l with Quantity = qty 3 }) ]
                )
            )
        }
        |> unit

    [<StepDefinition("I validate the changeset")>]
    member _.ValidateChangeset() =
        w.MarkCommands()
        w.Set("checked", Changeset.check w.Db.Model (w.Get<Changeset<Order>> "changeset"))

    [<StepDefinition("it reports the duplicate identity")>]
    member _.ReportsDuplicate() =
        let cs: Changeset<Order> = w.Get "checked"
        let e = cs.Errors |> List.exactlyOne
        equal (FieldErrorKind.DuplicateIdentity, "Lines") (e.Kind, e.Field) "error"
        equal 0 w.CommandsSinceMark.Length "validation needs no database"

    [<StepDefinition("it does not choose an entry based on incidental ordering")>]
    member _.NoChoice() =
        task {
            let! r = w.Repo.Update(w.Get<Changeset<Order>> "changeset")
            check (match r with Error(WriteError.Invalid _) -> true | _ -> false) "the write is rejected"
            let! q = w.Db.Scalar<int>("select quantity from order_lines")
            equal 1 q "stored quantity untouched"
        }
        |> unit

    // == Rule: Efficiency guarantees are testable ==============================

    [<StepDefinition("a query with nested separate-query loads")>]
    member _.NestedQuery() =
        w.Set("query", published |> Loads.withLinesAndProducts |> Loads.withSuccessfulPayments)

    [<StepDefinition("I inspect its execution plan")>]
    member _.Inspect() =
        w.MarkCommands()

        let plan =
            if w.Has "explain" then
                (w.Get<unit -> Result<QueryPlan, TranslationError>> "explain") ()
            else
                w.Repo.Explain(w.Get<Query<Order>> "query")

        w.Set("plan", ok plan)

    [<StepDefinition("I can identify the requested load paths")>]
    member _.Paths() =
        equal [ "Lines"; "Lines.Product"; "Payments" ] (w.Get<QueryPlan>("plan").LoadPaths) "paths"

    [<StepDefinition("the planned batching strategy")>]
    member _.Strategy() =
        let plan: QueryPlan = w.Get "plan"
        check (plan.Loads |> List.forall (fun l -> l.Strategy = LoadStrategy.BatchedByKeys 5000)) "batched by keys"
        check (plan.Loads |> List.forall (fun l -> l.Sql.Contains "= ANY(@p0)")) "one key-list parameter per batch"

    [<StepDefinition("inspection performs no database reads or writes")>]
    member _.InspectNoDb() = equal 0 w.CommandsSinceMark.Length "commands"

    // Scenario: Larger parent sets do not cause N+1 behavior

    [<StepDefinition("the same relationship load executed for different parent counts")>]
    member _.DifferentCounts() =
        task {
            let! _ =
                w.Db.Exec("insert into orders(status, total, placed_at) select 'published', g, now() from generate_series(1, 150) g")

            let! _ =
                w.Db.Exec(
                    "insert into payments(order_id, amount, status, created_at) select o.id, 1, 'succeeded', now() from orders o, generate_series(1, 3)"
                )

            ()
        }
        |> unit

    [<StepDefinition("both counts fit within provider batching limits")>]
    member _.FitLimits() =
        task {
            let run n =
                task {
                    w.MarkCommands()
                    let! os = all (plasm { for o in Schemas.orders do orderBy o.Id; take n; preload o.Payments })
                    equal n os.Length "parents"
                    return (commandsOn "payments" w.CommandsSinceMark).Length
                }

            let! small = run 10
            let! large = run 150
            w.Set("counts", (small, large))
        }
        |> unit

    [<StepDefinition("the number of relationship commands does not grow per parent")>]
    member _.DoesNotGrow() = equal (1, 1) (w.Get<int * int> "counts") "relationship commands for 10 and 150 parents"

    // Scenario: Database work remains bounded by the requested page

    [<StepDefinition("a large Orders table")>]
    member _.LargeTable() =
        task {
            let! p = Seed.product w.Db "SKU"

            let! _ =
                w.Db.Exec("insert into orders(status, total, placed_at) select 'published', g, now() - g * interval '1 minute' from generate_series(1, 5000) g")

            let! _ =
                w.Db.Exec(
                    "insert into order_lines(order_id, product_id, quantity, unit_price) select o.id, @p, 1, 1 from orders o, generate_series(1, 2)",
                    [ "p", box (let (ProductId v) = p in v) ]
                )

            ()
        }
        |> unit

    [<StepDefinition("I request one page with related collections")>]
    member _.RequestOnePage() =
        task {
            let repo = w.Db.RepoWith(includeParameterValues = true)
            w.MarkCommands()

            let! r =
                repo.All(
                    plasm {
                        for o in Schemas.orders do
                            orderByDescending o.PlacedAt
                            take 20
                            preload o.Lines
                    }
                )

            w.Set("orders", ok r)
        }
        |> unit

    [<StepDefinition("the library does not materialize all matching Orders in application memory")>]
    member _.NotAll() =
        let root = w.CommandsSinceMark.Head
        equal 20 root.Rows "parent rows transferred"
        equal 20 (w.Get<Order list> "orders").Length "parents materialized"

    [<StepDefinition("relationship queries are restricted to the selected parents")>]
    member _.Restricted() =
        let lines = commandsOn "order_lines" w.CommandsSinceMark |> List.exactlyOne
        equal 40 lines.Rows "line rows transferred"
        let keys = (snd lines.Parameters.Head).Value
        let selected = w.Get<Order list> "orders" |> List.map (fun o -> let (OrderId v) = o.Id in string v)
        equal ("[" + String.concat ", " selected + "]") keys "keys sent are exactly the selected parents"

    // == Rule: Transaction retries do not conceal repeated execution ===========

    [<StepDefinition("an operation encounters a retryable database failure")>]
    member _.RetryableFailure() =
        task {
            let! id = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let reads = ref 0
            let callbacks = ref 0
            let interfered = ref false

            let tx =
                transaction {
                    let! os = Tx.all (plasm { for o in Schemas.orders do where (o.Id = id) })
                    reads.Value <- reads.Value + 1
                    let order = List.exactlyOne os

                    // A concurrent writer commits a change to the row we read (first attempt only).
                    do!
                        Tx.run (fun _ ->
                            task {
                                callbacks.Value <- callbacks.Value + 1

                                if not interfered.Value then
                                    interfered.Value <- true
                                    use c = new NpgsqlConnection(w.Db.ConnectionString)
                                    do! c.OpenAsync()
                                    use cmd = c.CreateCommand()
                                    cmd.CommandText <- "update orders set total = 100"
                                    let! _ = cmd.ExecuteNonQueryAsync()
                                    ()

                                return Ok()
                            })

                    return! Tx.update (order, { order with Total = order.Total + 1m })
                }

            w.Set("tx", (tx: Tx<Order, string>))
            w.Set("reads", reads)
            w.Set("callbacks", callbacks)
        }
        |> unit

    [<StepDefinition("no retry policy is configured")>]
    member _.NoRetryPolicy() =
        task {
            let! r = w.Repo.Transact(w.Get<Tx<Order, string>> "tx", Data.IsolationLevel.RepeatableRead)
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("the library does not silently rerun application callbacks")>]
    member _.NoRerun() =
        let e = err (w.Get<Result<Order, TransactionError<string>>> "result")
        equal 1 e.Attempts "attempts"
        check (e.Step.Line > 0 && e.Step.File.EndsWith "EleganceSteps.fs") $"failed step {e.Step}"
        check (match e.Failure with TxFailure.Failed(DbError.Database f) -> f.Kind = DbFailureKind.SerializationFailure | _ -> false) $"%A{e.Failure}"
        equal 1 (w.Get<int ref> "callbacks").Value "callback invocations"

    [<StepDefinition("a configured transaction retry policy")>]
    member _.RetryPolicyStep() = w.Set("retry", RetryPolicy.OnSerializationFailure 3)

    [<StepDefinition("a transaction is retried")>]
    member _.Retried() =
        task {
            do! (EleganceSteps(w)).RetryableFailure()
            let! r = w.Repo.Transact(w.Get<Tx<Order, string>> "tx", Data.IsolationLevel.RepeatableRead, w.Get<RetryPolicy> "retry")
            w.Set("result", r)
        }
        |> unit

    [<StepDefinition("database-dependent reads are repeated within the new attempt")>]
    member _.ReadsRepeated() =
        equal 2 (w.Get<int ref> "reads").Value "reads (one per attempt)"
        equal 2 (w.Get<int ref> "callbacks").Value "callbacks (one per attempt, explicitly opted in)"
        // The successful attempt worked from the fresh read: 100 + 1, not 10 + 1.
        equal 101m (ok (w.Get<Result<Order, TransactionError<string>>> "result")).Total "result of the committed attempt"

    [<StepDefinition("results from the failed attempt are not presented as committed")>]
    member _.FailedAttemptNotCommitted() =
        task {
            let! total = w.Db.Scalar<decimal>("select total from orders")
            equal 101m total "stored total"
        }
        |> unit
