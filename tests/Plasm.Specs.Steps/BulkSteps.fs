namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Bulk writes and upserts
[<Binding>]
type BulkSteps(w: World) =

    let drafts =
        plasm {
            for o in tableOf<Order> do
                where (o.Status = Draft)
        }

    let publishedOrders =
        plasm {
            for o in tableOf<Order> do
                where (o.Status = Published)
        }

    let cancelDrafts = Bulk.update (drafts, fun o -> { o with Status = Cancelled })

    let statusOf (OrderId id) = w.Db.Scalar<string>("select status from orders where id = @id", [ "id", box id ])
    let totalOf (OrderId id) = w.Db.Scalar<decimal>("select total from orders where id = @id", [ "id", box id ])
    let versionOf (OrderId id) = w.Db.Scalar<int>("select version from orders where id = @id", [ "id", box id ])

    let product sku name =
        { Id = ProductId 0L
          Sku = sku
          Name = name }

    let nameOf (sku: string) = w.Db.Scalar<string>("select name from products where sku = @s", [ "s", box sku ])

    [<StepDefinition("3 draft Orders and 2 published Orders")>]
    member _.DraftsAndPublished() =
        task {
            let! d1 = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let! d2 = Seed.order w.Db None "draft" 2m (Seed.at 2)
            let! d3 = Seed.order w.Db None "draft" 3m (Seed.at 3)
            let! p1 = Seed.order w.Db None "published" 10m (Seed.at 4)
            let! p2 = Seed.order w.Db None "published" 20m (Seed.at 5)
            w.Set("drafts", [ d1; d2; d3 ])
            w.Set("published", [ p1; p2 ])
        }
        |> unit

    // Scenario: Updating every row a query matches

    [<StepDefinition("I cancel every draft Order with one bulk update")>]
    member _.CancelDrafts() =
        task {
            w.MarkCommands()
            let! n = w.Write(Tx.execute cancelDrafts)
            w.Set("affected", ok n)
        }
        |> unit

    [<StepDefinition("^(\\d+) rows are reported as (?:updated|deleted)$")>]
    member _.Affected(n: int) = equal n (w.Get<int> "affected") "rows affected"

    [<StepDefinition("the draft Orders are now cancelled and the published Orders are unchanged")>]
    member _.Cancelled() =
        task {
            for o in w.Get<OrderId list> "drafts" do
                let! s = statusOf o
                equal "cancelled" s $"status of {o}"
                let! v = versionOf o
                equal 2 v $"version of {o} is incremented"

            for o in w.Get<OrderId list> "published" do
                let! s = statusOf o
                equal "published" s $"status of {o}"
                let! v = versionOf o
                equal 1 v $"version of {o}"
        }
        |> unit

    [<StepDefinition("the update runs as a single database command filtered by the query")>]
    member _.SingleUpdate() =
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        check (cmd.Sql.StartsWith "UPDATE \"orders\"") cmd.Sql
        check (cmd.Sql.Contains "WHERE \"orders\".\"status\" = @p") cmd.Sql

    // Scenario: Bulk updates can compute from the current row

    [<StepDefinition("I add 10 to the total of every published Order")>]
    member _.AddTen() =
        task {
            w.MarkCommands()
            let! n = w.Repo.Transact(Tx.execute (Bulk.update (publishedOrders, fun o -> { o with Total = o.Total + 10m })): Tx<_, unit>)
            equal 2 (ok n) "rows updated"
        }
        |> unit

    [<StepDefinition("each published total is computed by the database from its old value")>]
    member _.Computed() =
        task {
            let! totals = w.Get<OrderId list> "published" |> List.map totalOf |> System.Threading.Tasks.Task.WhenAll
            equal [| 20m; 30m |] totals "totals"
            check ((w.CommandsSinceMark |> List.exactlyOne).Sql.Contains "\"total\" = \"orders\".\"total\" + @p") "computed in SQL"
        }
        |> unit

    [<StepDefinition("fields not mentioned in the update are not written")>]
    member _.OnlyMentioned() =
        let sql = (w.CommandsSinceMark |> List.exactlyOne).Sql
        let set = sql.Substring(sql.IndexOf "\nSET ", sql.IndexOf "\nWHERE " - sql.IndexOf "\nSET ")
        equal "\nSET \"total\" = \"orders\".\"total\" + @p0, \"version\" = \"orders\".\"version\" + 1" set "SET clause"

    // Scenario: Bulk updates respect optimistic concurrency

    [<StepDefinition("an Order loaded by two independent workflows")>]
    member this.LoadedTwice() =
        task {
            do! this.DraftsAndPublished()
            let id = (w.Get<OrderId list> "drafts").Head

            let byId =
                plasm {
                    for o in tableOf<Order> do
                        where (o.Id = id)
                }

            let! first = w.Repo.One byId
            let! second = w.Repo.One byId
            w.Set("byId", byId)
            w.Set("first", (ok first).Value)
            w.Set("second", (ok second).Value)
        }
        |> unit

    [<StepDefinition("one workflow bulk-cancels it")>]
    member _.BulkCancelsIt() =
        task {
            let! n = w.Repo.Transact(Tx.execute (Bulk.update (w.Get<Query<Order>> "byId", fun o -> { o with Status = Cancelled })): Tx<_, unit>)
            equal 1 (ok n) "rows updated"
        }
        |> unit

    [<StepDefinition("the other workflow saves a changeset based on the old version")>]
    member _.SavesOld() =
        task {
            let order: Order = w.Get "second"
            let! r = w.Repo.Transact(order |> edit (fun o -> { o with Total = 99m }))
            w.Set("save", r)
        }
        |> unit

    [<StepDefinition("the second save is rejected as stale")>]
    member _.Stale() =
        match w.Get<Result<Order, TransactionError<unit>>> "save" with
        | Error { Failure = TxFailure.Conflict _ } -> ()
        | other -> failwithf "expected a conflict, got %A" other

    // Scenario: Deleting every row a query matches

    [<StepDefinition("I delete every draft Order with one bulk delete")>]
    member _.DeleteDrafts() =
        task {
            let! n = w.Repo.Transact(Tx.execute (Bulk.delete drafts): Tx<_, unit>)
            w.Set("affected", ok n)
        }
        |> unit

    // Rule: Updates and deletes can return the rows they wrote

    [<StepDefinition("the first draft Order has a Line")>]
    member _.DraftLine() =
        task {
            let! p = Seed.product w.Db "LINE-1"
            let! _ = Seed.line w.Db (w.Get<OrderId list> "drafts").Head p 1 1m
            ()
        }
        |> unit

    [<StepDefinition("I cancel every draft Order with one bulk update that returns them")>]
    member _.CancelReturning() =
        task {
            w.MarkCommands()
            let! r = w.Write(Tx.execute (Bulk.updateReturning (drafts, fun o -> { o with Status = Cancelled })))
            w.Set("returned", ok r)
        }
        |> unit

    [<StepDefinition("the 3 draft Orders are returned cancelled, the first with its Line")>]
    member _.ReturnedCancelled() =
        let returned = w.Get<Order list> "returned" |> List.sortBy (fun o -> let (OrderId v) = o.Id in v)
        equal (w.Get<OrderId list> "drafts") (returned |> List.map (fun o -> o.Id)) "orders returned"
        check (returned |> List.forall (fun o -> o.Status = Cancelled)) $"statuses returned: %A{returned |> List.map (fun o -> o.Status)}"
        equal [ 1; 0; 0 ] (returned |> List.map (fun o -> o.Lines.Length)) "lines of each order returned"

    [<StepDefinition("the update returns its rows from its own command")>]
    member _.UpdateReturns() =
        let update = w.CommandsSinceMark |> List.find (fun c -> c.Sql.StartsWith "UPDATE")
        check (update.Sql.Contains "RETURNING") update.Sql

    [<StepDefinition("^I delete the Product \"(.*)\" with one bulk delete that returns it$")>]
    member _.DeleteProductReturning(sku: string) =
        task {
            let! r = w.Write(Tx.execute (Bulk.deleteReturning (plasm { for p in tableOf<Product> do where (p.Sku = sku) })))
            w.Set("deletedProducts", ok r)
        }
        |> unit

    [<StepDefinition("^the Product \"(.*)\" named \"(.*)\" is returned$")>]
    member _.ProductReturned(sku: string, name: string) =
        equal [ sku, name ] (w.Get<Product list> "deletedProducts" |> List.map (fun p -> p.Sku, p.Name)) "products returned"

    [<StepDefinition("^only the Product \"(.*)\" remains$")>]
    member _.OnlyProduct(sku: string) =
        task {
            let! rest = w.Repo.All(Query.from (tableOf<Product>))
            equal [ sku ] (ok rest |> List.map (fun p -> p.Sku)) "products remaining"
        }
        |> unit

    [<StepDefinition("I delete every draft Order with one bulk delete that returns them")>]
    member _.DeleteDraftsReturning() =
        task {
            w.MarkCommands()
            let! r = w.Write(Tx.execute (Bulk.deleteReturning drafts))
            w.Set("deleteReturning", r)
        }
        |> unit

    [<StepDefinition("^the request is refused, saying \"(.*)\"$")>]
    member _.RefusedSaying(saying: string) =
        match w.Get<Result<Order list, TransactionError<unit>>> "deleteReturning" with
        | Error { Failure = TxFailure.Failed(DbError.Translation e) } -> check ((string e).Contains saying) (string e)
        | other -> failwithf "expected a refusal, got %A" other

    [<StepDefinition("no draft Order was deleted")>]
    member _.NoDraftDeleted() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from orders where status = 'draft'")
            equal 3L n "draft orders"
        }
        |> unit

    [<StepDefinition("only the published Orders remain")>]
    member _.OnlyPublished() =
        task {
            let! rest = w.Repo.All(plasm { for o in tableOf<Order> do orderBy o.Id })
            equal (w.Get<OrderId list> "published") (ok rest |> List.map (fun o -> o.Id)) "remaining orders"
        }
        |> unit

    // Scenario: Bulk writes are explicit about what they bypass

    [<StepDefinition("a bulk update would violate a database constraint for one row")>]
    member _.Violates() =
        task {
            // Totals must be >= 0; the draft with total 1 would become -1.
            let all = plasm { for o in tableOf<Order> do where (o.Total < 100m) }
            let! r = w.Repo.Transact(Tx.execute (Bulk.update (all, fun o -> { o with Total = o.Total - 2m })): Tx<_, unit>)
            w.Set("bulkResult", r)
        }
        |> unit

    [<StepDefinition("the bulk update fails with a classified constraint violation")>]
    member _.Classified() =
        match w.Get<Result<int, TransactionError<unit>>> "bulkResult" with
        | Error { Failure = TxFailure.Failed(DbError.Database f) } ->
            equal DbFailureKind.CheckViolation f.Kind "failure kind"
            equal (Some "orders_total_check") f.Constraint "constraint"
        | other -> failwithf "expected a database failure, got %A" other

    [<StepDefinition("no row was changed")>]
    member _.NoRowChanged() =
        task {
            let ids = w.Get<OrderId list> "drafts" @ w.Get<OrderId list> "published"
            let! totals = ids |> List.map totalOf |> System.Threading.Tasks.Task.WhenAll
            equal [| 1m; 2m; 3m; 10m; 20m |] totals "totals"
        }
        |> unit

    // Scenario: Unsupported bulk requests are rejected before execution

    [<StepDefinition("I request a bulk update with an ordering or a limit")>]
    member _.OrderedBulk() =
        task {
            w.MarkCommands()

            let firstTwo =
                plasm {
                    for o in tableOf<Order> do
                        orderBy o.Id
                        take 2
                }

            let! r = w.Repo.Transact(Tx.execute (Bulk.update (firstTwo, fun o -> { o with Status = Cancelled })): Tx<_, unit>)
            w.Set("loadFailure", (match r with Error { Failure = TxFailure.Failed e } -> Some e | _ -> None))
            w.Set("expectedExplanation", [ "every row the query matches"; "ordering, take and skip" ])
        }
        |> unit

    [<StepDefinition("I request a bulk update that changes the primary key")>]
    member _.KeyBulk() =
        task {
            let! r = w.Repo.Transact(Tx.execute (Bulk.update (drafts, fun o -> { o with Id = OrderId 1L })): Tx<_, unit>)
            w.Set("loadFailure", (match r with Error { Failure = TxFailure.Failed e } -> Some e | _ -> None))
            w.Set("expectedExplanation", [ "primary key"; "cannot be changed" ])
        }
        |> unit

    // Rule: Inserts and upserts take many records at once

    [<StepDefinition("I insert 3 Products with one bulk insert")>]
    member _.InsertThree() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.Transact(Tx.execute (Bulk.insert [ product "A-1" "Anvil"; product "B-2" "Bolt"; product "C-3" "Clamp" ]): Tx<_, unit>)

            w.Set("inserted", ok r)
        }
        |> unit

    [<StepDefinition("the 3 Products are returned with their generated keys")>]
    member _.Returned() =
        let ps: Product list = w.Get "inserted"
        equal [ "A-1"; "B-2"; "C-3" ] (ps |> List.map (fun p -> p.Sku)) "skus in order"
        equal 3 (ps |> List.map (fun p -> p.Id) |> List.distinct |> List.length) "distinct keys"
        check (ps |> List.forall (fun p -> p.Id <> ProductId 0L)) "generated keys"

    [<StepDefinition("^the (?:insert|upsert) runs as a single database command$")>]
    member _.SingleInsert() =
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        check (cmd.Sql.StartsWith "INSERT INTO \"products\"") cmd.Sql

    [<StepDefinition("^a Product \"(.*)\" named \"(.*)\"$")>]
    member _.ProductNamed(sku: string, name: string) =
        task {
            let! id = w.Db.Scalar<int64>("insert into products(sku, name) values (@s, @n) returning id", [ "s", box sku; "n", box name ])
            w.Set("existing", ProductId id)
        }
        |> unit

    [<StepDefinition("^I upsert Products \"(.*)\" named \"(.*)\" and \"(.*)\" named \"(.*)\" on the SKU$")>]
    member _.Upsert(sku1: string, name1: string, sku2: string, name2: string) =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.Transact(Tx.execute (Bulk.upsert (
                        [ product sku1 name1; product sku2 name2 ],
                        (fun p -> p.Sku),
                        fun current incoming -> { current with Name = incoming.Name }
                    )): Tx<_, unit>)

            w.Set("upserted", ok r)
            check ((w.CommandsSinceMark |> List.exactlyOne).Sql.Contains "ON CONFLICT (\"sku\") DO UPDATE SET \"name\" = \"excluded\".\"name\"") "upsert SQL"
        }
        |> unit

    [<StepDefinition("^\"(.*)\" keeps its key and is now named \"(.*)\"$")>]
    member _.KeepsKey(sku: string, name: string) =
        task {
            let p = w.Get<Product list> "upserted" |> List.find (fun p -> p.Sku = sku)
            equal (w.Get<ProductId> "existing") p.Id "key"
            equal name p.Name "returned name"
            let! stored = nameOf sku
            equal name stored "stored name"
        }
        |> unit

    [<StepDefinition("^\"(.*)\" is inserted$")>]
    member _.Inserted(sku: string) =
        let p = w.Get<Product list> "upserted" |> List.find (fun p -> p.Sku = sku)
        check (p.Id <> w.Get<ProductId> "existing") "new key"

    [<StepDefinition("^I insert Products \"(.*)\" and \"(.*)\" ignoring existing SKUs$")>]
    member _.InsertMissing(sku1: string, sku2: string) =
        task {
            let! r = w.Repo.Transact(Tx.execute (Bulk.insertMissing ([ product sku1 "Ignored"; product sku2 "Second" ], fun p -> p.Sku)): Tx<_, unit>)
            w.Set("insertedMissing", ok r)
        }
        |> unit

    [<StepDefinition("^only \"(.*)\" is inserted and returned$")>]
    member _.OnlyInserted(sku: string) =
        equal [ sku ] (w.Get<Product list> "insertedMissing" |> List.map (fun p -> p.Sku)) "returned"

    [<StepDefinition("^\"(.*)\" is unchanged$")>]
    member _.Unchanged(sku: string) =
        task {
            let! name = nameOf sku
            equal "Old name" name "name"
        }
        |> unit

    // Rule: Bulk writes are values

    [<StepDefinition("I describe a bulk update without running it")>]
    member _.Describe() =
        w.MarkCommands()
        w.Set("bulkPlan", w.Repo.Explain cancelDrafts |> ok)

    [<StepDefinition("its SQL can be inspected")>]
    member _.Inspected() =
        let plan: QueryPlan = w.Get "bulkPlan"
        check (plan.Sql.StartsWith "UPDATE \"orders\"\nSET \"status\" = @p0") plan.Sql

    [<StepDefinition("a transaction bulk-cancels the drafts and then fails")>]
    member _.TxBulk() =
        task {
            let! r =
                w.Repo.Transact(
                    transaction {
                        let! n = Tx.execute cancelDrafts
                        equal 3 n "rows cancelled inside the transaction"
                        return! Tx.fail "notification service unavailable"
                    }
                )

            w.Set("txError", err r)
        }
        |> unit

    [<StepDefinition("the transaction reports the failing step")>]
    member _.TxStep() =
        let e: TransactionError<string> = w.Get "txError"
        equal "fail" e.Step.Action "failed step"
        equal [ "execute" ] (e.RolledBack |> List.map (fun s -> s.Action)) "rolled back"

    [<StepDefinition("the draft Orders are unchanged")>]
    member _.DraftsUnchanged() =
        task {
            for o in w.Get<OrderId list> "drafts" do
                let! s = statusOf o
                equal "draft" s $"status of {o}"
        }
        |> unit
