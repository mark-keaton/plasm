namespace Plasm.Specs

open System
open System.Collections.Generic
open System.Threading.Tasks
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Per-scenario state, injected into every binding class by Reqnroll.
type World() =
    let bag = Dictionary<string, obj>()

    member val Db: TestDb = Unchecked.defaultof<TestDb> with get, set
    member val Mark = 0 with get, set

    /// Action run by shared steps such as "When I execute the query".
    member val Pending: unit -> Task = (fun () -> failwith "no pending action for this scenario") with get, set

    member _.Set(key: string, value: 'T) = bag.[key] <- box value
    member _.Get<'T>(key: string) : 'T = unbox<'T> bag.[key]
    member _.Has(key: string) = bag.ContainsKey key

    member w.Repo = w.Db.Repo

    /// A single write: one transaction of one step, refusing nothing of its own.
    member w.Write(tx: Tx<'T, unit>) = w.Db.Repo.Transact tx

    /// Remember how many commands have run so far.
    member w.MarkCommands() = w.Mark <- w.Db.Recorder.Count

    member w.CommandsSinceMark = w.Db.Recorder.Events |> List.skip w.Mark

    /// The commands since the mark, without the loads of the OrderLines an
    /// Order owns (one more command whenever Orders are read).
    member w.QueryCommands =
        w.CommandsSinceMark
        |> List.filter (fun c -> not (c.Sql.StartsWith "SELECT" && c.Sql.Contains "FROM \"order_lines\" AS \"t0\" WHERE (\"t0\".\"order_id\""))

[<AutoOpen>]
module Support =

    /// The change `f` makes to `original`, as one write step.
    let edit (f: 'T -> 'T) (original: 'T) : Tx<'T, unit> = Tx.update (original, f original)

    let ok (r: Result<'a, 'e>) =
        match r with
        | Ok v -> v
        | Error e -> failwithf "unexpected error: %A" e

    let err (r: Result<'a, 'e>) =
        match r with
        | Ok v -> failwithf "expected an error, got %A" v
        | Error e -> e

    let check (condition: bool) (message: string) = if not condition then failwith message

    let equal (expected: 'a) (actual: 'a) (what: string) =
        if expected <> actual then
            failwithf "%s: expected %A but was %A" what expected actual

    let unit (t: Task<'a>) : Task = t :> Task

    let published =
        plasm {
            for o in Schemas.orders do
                where (o.Status = Published)
        }

    let successfulPayments =
        plasm {
            for p in Schemas.payments do
                where (p.Status = Succeeded)
                orderBy p.CreatedAt
        }

    let forCustomer (customer: CustomerId) (q: Query<Order>) =
        plasm {
            for o in q do
                where (o.CustomerId = Some customer)
        }

    let rowsOf (events: CommandEvent list) = events |> List.sumBy (fun e -> e.Rows)

    let commandsOn (table: string) (events: CommandEvent list) =
        events |> List.filter (fun e -> e.Sql.Contains($"FROM \"{table}\""))
