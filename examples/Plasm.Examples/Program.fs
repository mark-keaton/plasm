/// A tour of the public API against a real PostgreSQL database.
/// Run: dotnet run   (uses Testcontainers, or PLASM_TEST_PG for an existing server)
module Shop.Program

open System.Collections.Generic
open Plasm
open Plasm.Postgres
open Shop.Model
open Shop.Queries
open Shop.Writes

let show title value = printfn "\n== %s\n%A" title value

[<EntryPoint>]
let main argv =
    task {
        // Throwaway database with the shop tables.
        let! testDb = Plasm.TestDomain.Database.freshDefault ()

        if argv = [| "sql" |] then
            let output = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "website", "sql.js")
            do! Shop.SqlCapture.run testDb.ConnectionString (System.IO.Path.GetFullPath output)
            return 0
        else

            let repo =
                Repo(
                    Db.model,
                    Postgres.connect testDb.ConnectionString,
                    { RepoOptions.Default with OnCommand = Some(fun e -> printfn "   [sql %4.1fms, %d rows] %s" e.Duration.TotalMilliseconds e.Rows (e.Sql.Substring(0, min 90 e.Sql.Length))) }
                )

            // -- Writes ------------------------------------------------------------
            let! widget = repo.Insert(Changeset.insert { Id = ProductId 0L; Sku = "W-1"; Name = "Widget" })
            let widget = Result.toOption widget |> Option.get

            let! checkedOut = repo.Transact(checkout "Ada" "ada@example.com" widget.Id)

            let ada, order =
                match checkedOut with
                | Ok(c, o) -> c, o
                | Error e -> failwithf "checkout failed at step %s: %A" e.Step e.Failure

            show "inserted order (generated ids, loaded lines)" (order.Id, order.Total, order.Lines)

            // Invalid input is inspectable before anything runs.
            let bad = registerCustomer "Bob" "not-an-email"
            show "invalid changeset (no database access)" bad.Errors

            // Constraint violations come back as field errors.
            let! dup = repo.Insert(registerCustomer "Eve" "ada@example.com")

            match dup with
            | Error(WriteError.Invalid cs) -> show "unique constraint" cs.Errors
            | other -> show "unexpected" other

            // Optimistic concurrency.
            let form = Dictionary(dict [ "cancellationReason", "changed my mind"; "id", "999"; "version", "7" ])
            let! cancelled = repo.Update(order |> cancelFromForm form)
            show "cancelled via form (id/version ignored)" (cancelled |> Result.map (fun o -> o.Status, o.CancellationReason, o.Version))
            let! stale = repo.Update(order |> Changeset.edit (fun o -> { o with Total = 1m }))

            match stale with
            | Error(WriteError.Stale _) -> show "stale update rejected" "the stored row has a newer version"
            | other -> show "unexpected" other

            // -- Reads -------------------------------------------------------------
            let page = Db.orders |> Query.from |> forCustomer ada.Id |> orderPage 0 20

            match repo.Explain page with
            | Ok plan -> show "plan (no database access)" (plan.LoadPaths, plan.Loads |> List.map (fun l -> l.Path, l.Strategy))
            | Error e -> show "plan error" e

            let! orders = repo.All page

            match orders with
            | Ok os ->
                for o in os do
                    let lines =
                        o.Lines |> Assoc.defaultValue [] |> List.map (fun l -> (Assoc.get l.Product).Sku, l.Quantity.Value)

                    show $"order {o.Id}" (o.Status, lines, o.Payments, o.Customer)
            | Error e -> show "query failed" e

            let! rows = repo.All totals
            show "projection" rows

            // Untranslatable expressions are rejected, never run client-side.
            let isLucky (d: decimal) = d = 7m
            let! rejected = repo.All(plasm { for o in Db.orders do where (isLucky o.Total) })
            show "untranslatable" rejected

            return 0
    }
    |> fun t -> t.Result
