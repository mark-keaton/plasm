/// Property-based tests for composition and preservation guarantees.
module Plasm.Tests.Properties

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

let run (t: Threading.Tasks.Task<'a>) = t.GetAwaiter().GetResult()

// -- (a) predicate composition commutes and matches F# semantics ---------------

type Pred =
    | TotalAbove of int
    | TotalBelow of int
    | StatusIs of OrderStatus
    | HasReason
    | ReasonIs of int
    | ForCustomer of int
    | NoCustomer
    | HasSucceededPayment
    | HasLineAbove of int
    | AllLinesAbove of int

let reasons = [| "late"; "duplicate"; "fraud" |]

let applyPred (p: Pred) (q: Query<Order>) : Query<Order> =
    match p with
    | TotalAbove t ->
        let t = decimal (abs t % 100)
        plasm { for o in q do where (o.Total > t) }
    | TotalBelow t ->
        let t = decimal (abs t % 100)
        plasm { for o in q do where (o.Total < t) }
    | StatusIs s -> plasm { for o in q do where (o.Status = s) }
    | HasReason -> plasm { for o in q do where (o.CancellationReason <> None) }
    | ReasonIs i ->
        let r = Some reasons.[abs i % reasons.Length]
        plasm { for o in q do where (o.CancellationReason = r) }
    | ForCustomer c ->
        let c = Some(CustomerId(int64 (abs c % 3 + 1)))
        plasm { for o in q do where (o.CustomerId = c) }
    | NoCustomer -> plasm { for o in q do where (o.CustomerId = None) }
    | HasSucceededPayment ->
        plasm { for o in q do where (exists (plasm { for p in payments do where (p.OrderId = o.Id && p.Status = Succeeded) })) }
    | HasLineAbove a ->
        let a = decimal (abs a % 30)
        plasm { for o in q do where (o.Lines |> List.exists (fun l -> l.UnitPrice > a)) }
    | AllLinesAbove a ->
        let a = decimal (abs a % 30)
        plasm { for o in q do where (o.Lines |> List.forall (fun l -> l.UnitPrice > a)) }

/// The same predicate evaluated in memory over the orders read, with their
/// lines, and every payment: the oracle.
let evalPred (paid: Set<OrderId>) (p: Pred) (o: Order) =
    match p with
    | TotalAbove t -> o.Total > decimal (abs t % 100)
    | TotalBelow t -> o.Total < decimal (abs t % 100)
    | StatusIs s -> o.Status = s
    | HasReason -> o.CancellationReason <> None
    | ReasonIs i -> o.CancellationReason = Some reasons.[abs i % reasons.Length]
    | ForCustomer c -> o.CustomerId = Some(CustomerId(int64 (abs c % 3 + 1)))
    | NoCustomer -> o.CustomerId = None
    | HasSucceededPayment -> paid.Contains o.Id
    | HasLineAbove a -> o.Lines |> List.exists (fun l -> l.UnitPrice > decimal (abs a % 30))
    | AllLinesAbove a -> o.Lines |> List.forall (fun l -> l.UnitPrice > decimal (abs a % 30))

let private seeded =
    lazy
        (run (
            task {
                let! db = freshDefault ()

                for i in 1..3 do
                    let! _ = Seed.customer db $"c{i}" $"c{i}@example.com"
                    ()

                let! product = Seed.product db "P"
                let statuses = [| "draft"; "published"; "cancelled" |]
                let pstat = [| "pending"; "succeeded"; "failed" |]

                for i in 0..39 do
                    let customer = if i % 5 = 0 then None else Some(CustomerId(int64 (i % 3 + 1)))
                    let! o = Seed.order db customer statuses.[i % 3] (decimal ((i * 37) % 100)) (Seed.at i)

                    if i % 4 = 1 then
                        let! _ =
                            db.Exec(
                                "update orders set cancellation_reason = @r where id = @id",
                                [ "r", box reasons.[i % 3]; "id", box (let (OrderId v) = o in v) ]
                            )

                        ()

                    for j in 0 .. (i % 4) - 1 do
                        let! _ = Seed.payment db o (decimal ((i * 13 + j * 7) % 50)) pstat.[(i + j) % 3] (Seed.at (i + j))
                        ()

                    for j in 0 .. (i % 3) - 1 do
                        let! _ = Seed.line db o product (j + 1) (decimal ((i * 11 + j * 5) % 30))
                        ()

                let! all = db.Repo.All(Query.from orders)
                let! ps = db.Repo.All(Query.from payments)
                let paid = ok ps |> List.filter (fun p -> p.Status = Succeeded) |> List.map (fun p -> p.OrderId) |> Set.ofList
                return db, ok all, paid
            }
        ))

let ids (os: Order list) = os |> List.map (fun o -> o.Id) |> List.sort

[<Property(MaxTest = 30)>]
let ``composing two predicates in either order returns the same rows, matching F# semantics`` (a: Pred) (b: Pred) =
    let db, all, paid = seeded.Force()
    let ab = orders |> Query.from |> applyPred a |> applyPred b
    let ba = orders |> Query.from |> applyPred b |> applyPred a
    let r1 = ids (ok (run (db.Repo.All ab)))
    let r2 = ids (ok (run (db.Repo.All ba)))
    let expected = all |> List.filter (fun o -> evalPred paid a o && evalPred paid b o) |> ids
    r1 = r2 && r1 = expected

// -- (b) extending a query never changes the original -------------------------

type Extension =
    | Filter of Pred
    | Order
    | Page of int

let extend (q: Query<Order>) (e: Extension) =
    match e with
    | Filter p -> applyPred p q
    | Order -> plasm { for o in q do orderByDescending o.PlacedAt }
    | Page n ->
        plasm {
            for o in q do
                orderBy o.Id
                take (abs n % 10 + 1)
        }

[<Property(MaxTest = 100)>]
let ``extending a query never changes the original`` (baseOps: Extension list) (more: Extension list) =
    let baseQ = baseOps |> List.truncate 4 |> List.fold extend (Query.from orders)
    let before = baseQ.FilterCount, explain baseQ |> Result.map (fun p -> p.Sql, p.LoadPaths)
    let derived = more |> List.truncate 4 |> List.fold extend baseQ
    ignore derived
    let after = baseQ.FilterCount, explain baseQ |> Result.map (fun p -> p.Sql, p.LoadPaths)
    before = after

// -- (c) Changeset.edit records exactly the fields that differ -----------------

[<Property(MaxTest = 200)>]
let ``Changeset.edit records exactly the differing fields and proposes the edited value``
    (total: int)
    (status: OrderStatus)
    (reason: bool option)
    (newTotal: int)
    (newStatus: OrderStatus)
    (newReason: bool option)
    =
    let r (x: bool option) = x |> Option.map (fun b -> if b then "late" else "fraud")

    let original =
        { New.order None status (decimal (abs total % 50)) with
            CancellationReason = r reason }

    let edited =
        { original with
            Total = decimal (abs newTotal % 50)
            Status = newStatus
            CancellationReason = r newReason }

    let cs = original |> Changeset.edit (fun _ -> edited)

    let expected =
        [ if original.Status <> edited.Status then "Status"
          if original.Total <> edited.Total then "Total"
          if original.CancellationReason <> edited.CancellationReason then "CancellationReason" ]
        |> List.sort

    let cleared =
        cs.Changes |> List.exists (fun (n, c) -> n = "CancellationReason" && c = FieldChange.Cleared)

    (cs.Changes |> List.map fst |> List.sort) = expected
    && cs.Proposed = Some edited
    && cs.Data = Some original
    && cleared = (original.CancellationReason.IsSome && edited.CancellationReason.IsNone)

// -- (d) batched loading is equivalent across batch sizes ---------------------

let batchSizes = [ 1; 2; 3; 7; 5000 ]
let ceilDiv a b = if a = 0 then 0 else (a + b - 1) / b

[<Property(MaxTest = 15)>]
let ``batched loading assembles the same aggregates for every batch size with key-proportional command counts`` (lineCounts: int list) =
    let parents = max 1 (min 9 lineCounts.Length)
    let lineCounts = (lineCounts @ List.replicate parents 0) |> List.take parents |> List.map (fun c -> abs c % 5)

    run (
        task {
            let! db = freshDefault ()
            let! products = [ "a"; "b"; "c"; "d" ] |> List.map (Seed.product db) |> Threading.Tasks.Task.WhenAll

            for i in 0 .. parents - 1 do
                let! o = Seed.order db None "published" (decimal i) (Seed.at i)

                for j in 0 .. lineCounts.[i] - 1 do
                    let! _ = Seed.line db o products.[(i + j) % products.Length] (j + 1) 1m
                    ()

            let q = plasm { for o in orders do orderBy o.Id }

            let results =
                [ for b in batchSizes ->
                      db.Recorder.Clear()
                      let repo = db.RepoWith(options = { MaxKeysPerCommand = b })
                      let aggregates = ok (run (repo.All q))
                      // The Orders, then their lines in batches of b keys (keys are
                      // parent ids, whether or not lines exist).
                      aggregates, db.Recorder.Count = 1 + ceilDiv parents b ]

            let all = results |> List.map fst
            let countsOk = results |> List.forall snd
            let same = all |> List.forall (fun g -> g = all.Head)
            let shapeOk = all.Head |> List.map (fun o -> o.Lines.Length) = lineCounts
            return same && countsOk && shapeOk
        }
    )

// -- (e) owned children keep their declared order within each parent --------

[<Property(MaxTest = 10)>]
let ``owned lines keep their declared order within each parent`` (prices: int list) (batch: PositiveInt) =
    let prices = prices |> List.truncate 30 |> List.map (fun a -> decimal (abs a % 20))

    run (
        task {
            let! db = freshDefault ()
            let! p = Seed.product db "P"
            let! o1 = Seed.order db None "published" 1m (Seed.at 1)
            let! o2 = Seed.order db None "published" 2m (Seed.at 2)
            let! o3 = Seed.order db None "published" 3m (Seed.at 3)
            let parents = [| o1; o2; o3 |]

            for i, price in List.indexed prices do
                let! _ = Seed.line db parents.[i % 3] p (i + 1) price
                ()

            let repo = db.RepoWith(options = { MaxKeysPerCommand = batch.Get % 4 + 1 })
            let! r = repo.All(Query.from orders)

            return
                ok r
                |> List.forall (fun o ->
                    // Schemas.orders declares ownsOrderedBy ... (fun l -> l.Id).
                    o.Lines = List.sortBy (fun l -> l.Id) o.Lines && o.Lines |> List.forall (fun l -> l.OrderId = o.Id))
        }
    )
