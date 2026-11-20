/// A tour of Plasm's one style against a real PostgreSQL database.
/// Run: dotnet run   (uses Testcontainers, or PLASM_TEST_PG for an existing server)
///      dotnet run -- sql   (regenerates website/sql.js)
module Shop.Program

open System
open Plasm
open Plasm.Postgres
open Shop.Model
open Shop.Queries
open Shop.Writes

let show title value = printfn "\n== %s\n%A" title value

let ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "unexpected error: %A" e

let qty n = Quantity.create n |> ok

/// What a migration adds beyond the tables the model states: a rule only the
/// database holds. Its default name (orders_total_check) makes a violation the
/// Total field's error.
let migration = "alter table orders add constraint orders_total_check check (total >= 0);"

/// A throwaway database with the tables the model expects, created from the
/// DDL Plasm prints for it (`repo.Ddl()`) and the migration above.
let provision () =
    task {
        let! db = Plasm.TestDomain.Database.freshDefault ()
        let! _ = db.Exec "drop schema public cascade; create schema public;"
        // Only for its DDL, before the tables exist: nothing to check yet.
        let! printer =
            Repo.Start(
                Db.model,
                Postgres.connect db.ConnectionString,
                queriesIn = typeof<Order>.Assembly,
                options = { RepoOptions.Default with Queries = StartupCheck.Ignore; Schema = StartupCheck.Ignore }
            )

        let ddl = printer.Ddl()
        let! _ = db.Exec(ddl + "\n" + migration)
        return db.ConnectionString, ddl
    }

let tour (connectionString: string) (ddl: string) =
    task {
        // -- Start-up checks ----------------------------------------------------
        // Repo.Start translates every query value in the assembly and compares
        // the model with the database; by default either problem fails start-up.
        let! repo =
            Repo.Start(
                Db.model,
                Postgres.connect connectionString,
                queriesIn = typeof<Order>.Assembly,
                options =
                    { RepoOptions.Default with
                        OnCommand = Some(fun e -> printfn "   [sql %4.1fms, %d rows] %s" e.Duration.TotalMilliseconds e.Rows (e.Sql.Substring(0, min 100 e.Sql.Length))) }
            )

        show "the DDL the model expects (printed, never run by Plasm)" ddl
        show "start-up" "every query translates and the database fits the model"

        // -- Writes are transactions -------------------------------------------
        let! products =
            repo.Transact(
                Tx.execute (Bulk.insert [ { Id = ProductId 0L; Sku = "W-1"; Name = "Widget" }; { Id = ProductId 0L; Sku = "G-1"; Name = "Gadget" } ])
                : Tx<Product list, Refusal>
            )

        let widget, gadget =
            match ok products with
            | [ w; g ] -> w, g
            | other -> failwithf "expected two products, got %A" other

        let ada = { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }
        let lines = [ Order.line widget.Id (qty 2) 9.99m; Order.line gadget.Id (qty 1) 3m ]
        let! checkedOut = repo.Transact(checkout ada lines DateTime.UtcNow)
        let ada, order = ok checkedOut
        show "checkout: generated keys, the lines written with their order" (order.Id, order.Total, order.Lines)

        // The domain refuses; nothing is written.
        let! empty = repo.Transact(checkout { ada with Email = "empty@example.com" } [] DateTime.UtcNow)

        match empty with
        | Error { Failure = TxFailure.Rejected r; RolledBack = steps } -> show "domain rejection" (r, steps |> List.map string)
        | other -> show "unexpected" other

        // A constraint with PostgreSQL's default name is that field's error.
        let! dup = repo.Transact(Tx.insert { ada with Id = CustomerId 0L; Name = "Eve" }: Tx<Customer, Refusal>)

        match dup with
        | Error { Failure = TxFailure.InvalidChanges(entity, errors) } -> show "unique email" (entity, errors)
        | other -> show "unexpected" other

        let! missing = repo.Transact(checkout { ada with Email = "bob@example.com"; Name = "Bob" } [ Order.line (ProductId 999L) (qty 1) 1m ] DateTime.UtcNow)

        match missing with
        | Error { Failure = TxFailure.InvalidChanges(entity, errors) } -> show "a line referring to no product" (entity, errors)
        | other -> show "unexpected" other

        // Changing the lines of an order read writes the difference.
        let! trimmed = repo.Transact(changeOrder order.Id order.Version (Order.removeLines (fun l -> l.ProductId = gadget.Id)))
        let trimmed = ok trimmed
        show "one line removed: the order and its lines, as stored" (trimmed.Total, trimmed.Lines, trimmed.Version)

        // Optimistic concurrency: the caller saw version 1, the order is at 2.
        let! stale = repo.Transact(cancel order.Id order.Version "changed my mind")

        match stale with
        | Error { Failure = TxFailure.Conflict entity } -> show "stale copy" entity
        | other -> show "unexpected" other

        let! cancelled = repo.Transact(cancel trimmed.Id trimmed.Version "changed my mind")
        show "cancelled, with its reason" (ok cancelled).Status

        let! again = repo.Transact(cancel trimmed.Id (ok cancelled).Version "twice")

        match again with
        | Error { Failure = TxFailure.Rejected r } -> show "cancelled twice" r
        | other -> show "unexpected" other

        // -- Reads -------------------------------------------------------------
        let! second = repo.Transact(Tx.insert (Order.create ada.Id [ Order.line gadget.Id (qty 3) 3m ] DateTime.UtcNow |> ok): Tx<Order, Refusal>)
        let second = ok second

        let! _ =
            repo.Transact(
                Tx.insert
                    { Id = PaymentId 0L
                      OrderId = second.Id
                      Amount = 9m
                      Status = Succeeded
                      CreatedAt = DateTime.UtcNow }
                : Tx<Payment, Refusal>
            )

        let page = Query.from (tableOf<Order>) |> forCustomer ada.Id |> orderPage 0 20
        let! orders = repo.All(withSucceededPayments page)

        for row in ok orders do
            show $"order {row.Order.Id}" (row.Order.Status, row.Order.Lines |> List.map (fun l -> l.ProductId, l.Quantity.Value))
            show "  its succeeded payments" (row.Payments |> List.map (fun p -> p.Amount))

        let! late = repo.All(overdue ada.Id)
        show "unpaid orders (NOT EXISTS)" (ok late |> List.map (fun o -> o.Id))

        let! withGadget = repo.All(openWithProduct gadget.Id)
        show "open orders with a gadget line" (ok withGadget |> List.map (fun o -> o.Id))

        let! rows = repo.All totals
        show "projection" (ok rows)

        let! sold = repo.All soldProducts
        show "join: sold products" (ok sold)

        let! stats = repo.One orderStats
        show "aggregates: count, sum, avg, max" (ok stats)

        let! latest = repo.All(latestOrdersOf ada.Id 5)
        show "prepared" (ok latest |> List.map (fun o -> o.Id))

        let! attention = repo.All(needsAttention ada.Id)
        show "union: drafts and unpaid orders, each once" (ok attention |> List.map (fun o -> o.Id))

        let! history = repo.All orderHistory
        show "window: number and total so far" (ok history)

        let! summary = repo.All customerSummary
        show "filtered aggregates" (ok summary)

        let! registered = repo.Transact(register { ada with Id = CustomerId 0L })
        show "register: the email is taken, so the customer who has it" (ok registered).Name

        let! claimed = repo.Transact(claimDraft, retry = RetryPolicy.OnSerializationFailure 3)
        show "claimed draft (FOR UPDATE SKIP LOCKED)" (ok claimed |> Option.map (fun o -> o.Id))

        let! expired = repo.Transact(Tx.execute (expireDraftsReturning (DateTime.UtcNow.AddDays 1.0)): Tx<Order list, Refusal>)
        show "expired, as they are now" (ok expired |> List.map (fun o -> o.Id, o.Status))

        let batch = [ for n in 1..3 -> Order.create ada.Id [ Order.line gadget.Id (qty n) 3m ] DateTime.UtcNow |> ok ]
        let! placed = repo.Transact(placeAll batch)
        show "placed together (Tx.traverse)" (ok placed |> List.map (fun o -> o.Id, o.Lines.Length))

        match repo.Explain page with
        | Ok plan -> show "plan (no database access)" (plan.LoadPaths, plan.Sql)
        | Error e -> show "plan error" e

        // -- Linked records ------------------------------------------------------
        let! article = repo.Transact(Tx.insert { Id = ArticleId 0L; Title = "Plasm in ten minutes"; Tags = [] }: Tx<Article, Refusal>)
        let! tagged = repo.Transact(retag (ok article) [ "fsharp"; "orm" ])
        show "retagged" (ok tagged).Tags
        let! found = repo.All(taggedWith "orm")
        show "articles tagged orm" (ok found |> List.map (fun a -> a.Title))

        // Untranslatable expressions are refused, never run client-side.
        let isLucky (d: decimal) = d = 7m
        let! rejected = repo.All(plasm { for o in tableOf<Order> do where (isLucky o.Total) })
        show "untranslatable" rejected

        return 0
    }

[<EntryPoint>]
let main argv =
    task {
        let! connectionString, ddl = provision ()

        if argv = [| "sql" |] then
            let output = IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "website", "sql.js")
            do! Shop.SqlCapture.run connectionString (IO.Path.GetFullPath output)
            return 0
        else
            return! tour connectionString ddl
    }
    |> fun t -> t.Result
