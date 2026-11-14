/// Behavioral compatibility suite: cases adapted from Ecto's tests
/// (https://github.com/elixir-ecto/ecto, Apache-2.0; see NOTICE). Each test
/// cites its source. Outcomes are compared, not SQL text. Deliberate
/// differences are named "ecto (deliberate difference): ..." and assert Plasm's
/// behavior.
module Plasm.Tests.EctoCompat

open System
open System.Collections.Generic
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas
open Plasm.TestDomain.Database
open Plasm.Tests.Helpers

let allOrders (db: TestDb) =
    task {
        let! r = db.Repo.All(plasm { for o in orders do orderBy o.Id })
        return ok r
    }

let byId (os: Order list) id = os |> List.find (fun o -> o.Id = id)
let lineIds (o: Order) = Assoc.get o.Lines |> List.map (fun l -> l.Id) |> List.sort
let withLines = plasm { for o in orders do preload o.Lines }

let input (pairs: (string * string) list) =
    Dictionary<string, string>(dict pairs) :> IReadOnlyDictionary<string, string>

let order0 = New.order None Draft 10m

// ===========================================================================
// Preloading (integration_test/cases/preload.exs)
// ===========================================================================

// preload.exs: test "preload has_many"
[<Fact>]
let ``ecto: preload has_many`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o1 = Seed.order db None "draft" 1m (Seed.at 1)
        let! o2 = Seed.order db None "draft" 2m (Seed.at 2)
        let! o3 = Seed.order db None "draft" 3m (Seed.at 3)
        // Same unit price on lines of different orders exposes assembly bugs.
        let! l1 = Seed.line db o1 p 1 1m
        let! l3 = Seed.line db o2 p 1 2m
        let! l2 = Seed.line db o1 p 1 2m
        let! l4 = Seed.line db o2 p 1 3m
        let! all = allOrders db
        Assert.Equal(NotLoaded, (byId all o1).Lines)
        let given = [ byId all o3; byId all o1; byId all o2 ]
        let! loaded = db.Repo.Preload(withLines, given)
        let loaded = ok loaded
        Assert.Equal<OrderId list>([ o3; o1; o2 ], loaded |> List.map (fun o -> o.Id))
        Assert.Equal<OrderLineId list>([ l1; l2 ], lineIds loaded.[1])
        Assert.Equal<OrderLineId list>([ l3; l4 ], lineIds loaded.[2])
        Assert.Equal<OrderLine list>([], Assoc.get loaded.[0].Lines)
    }

// preload.exs: test "preload has_many multiple times"
[<Fact>]
let ``ecto: preload has_many multiple times`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! l1 = Seed.line db o p 1 1m
        let! l2 = Seed.line db o p 1 2m
        let! all = allOrders db
        let x = all.Head
        let! once = db.Repo.Preload(withLines, [ x; x ])
        let once = ok once
        Assert.True(once |> List.forall (fun o -> lineIds o = [ l1; l2 ]))
        let! twice = db.Repo.Preload(withLines, once)
        Assert.True(ok twice |> List.forall (fun o -> lineIds o = [ l1; l2 ]))
    }

// preload.exs: test "preload belongs_to"
[<Fact>]
let ``ecto: preload belongs_to`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 1 1m
        let! r = db.Repo.All(plasm { for l in orderLines do preload l.Product })
        let line = (ok r).Head
        Assert.Equal(p, (Assoc.get line.Product).Id)
    }

// preload.exs: test "preload belongs_to with shared parent"
[<Fact>]
let ``ecto: preload belongs_to with shared parent`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 1 1m
        let! _ = Seed.line db o p 2 1m
        db.Recorder.Clear()
        let! r = db.Repo.All(plasm { for l in orderLines do preload l.Product })
        let lines = ok r
        Assert.Equal(2, lines.Length)
        Assert.True(lines |> List.forall (fun l -> (Assoc.get l.Product).Id = p))
        Assert.Equal(2, db.Recorder.Count) // lines + one product query for the shared key
    }

// preload.exs: test "preload has_many through" is not applicable (no :through yet).

// preload.exs: test "preload empty"
[<Fact>]
let ``ecto: preload empty`` () =
    task {
        let! db = freshDefault ()
        let! r = db.Repo.Preload(withLines, [])
        Assert.Empty(ok r)
        Assert.Equal(0, db.Recorder.Count)
    }

// preload.exs: test "preload has_many with no associated entries"
[<Fact>]
let ``ecto: preload has_many with no associated entries`` () =
    task {
        let! db = freshDefault ()
        let! _ = Seed.order db None "draft" 1m (Seed.at 1)
        let! r = db.Repo.All withLines
        let o = (ok r).Head
        Assert.Equal(1m, o.Total)
        Assert.Equal(Loaded [], o.Lines)
    }

// preload.exs: test "preload belongs_to with no associated entry"
// (and "preload has_one with no associated entries": optional one → nil)
[<Fact>]
let ``ecto: preload belongs_to with no associated entry`` () =
    task {
        let! db = freshDefault ()
        let! _ = Seed.order db None "draft" 1m (Seed.at 1)
        db.Recorder.Clear()
        let! r = db.Repo.All(plasm { for o in orders do preload o.Customer })
        Assert.Equal(Loaded None, (ok r).Head.Customer)
        // No keys to look up, so no relationship command is issued.
        Assert.Equal(1, db.Recorder.Count)
    }

// preload.exs: test "preload has_many" over an optional foreign key (Customer.Orders)
[<Fact>]
let ``ecto: preload has_many through an optional foreign key`` () =
    task {
        let! db = freshDefault ()
        let! c = Seed.customer db "Ada" "a@x"
        let! o1 = Seed.order db (Some c) "draft" 1m (Seed.at 1)
        let! _ = Seed.order db None "draft" 2m (Seed.at 2)
        let! r = db.Repo.All(plasm { for c in customers do preload c.Orders })
        let cust = (ok r).Head
        Assert.Equal<OrderId list>([ o1 ], Assoc.get cust.Orders |> List.map (fun o -> o.Id))
    }

// preload.exs: test "preload with query"
[<Fact>]
let ``ecto: preload with query`` () =
    task {
        let! db = freshDefault ()
        let! o1 = Seed.order db None "draft" 1m (Seed.at 1)
        let! o2 = Seed.order db None "draft" 2m (Seed.at 2)
        let! p1 = Seed.payment db o1 5m "succeeded" (Seed.at 1)
        let! p2 = Seed.payment db o1 9m "succeeded" (Seed.at 2)
        let! _ = Seed.payment db o2 1m "failed" (Seed.at 3)

        let q =
            plasm {
                for p in payments do
                    where (p.Status = Succeeded)
                    orderByDescending p.Amount
            }

        let! r = db.Repo.All(plasm { for o in orders do orderBy o.Id; preload o.Payments q })
        let os = ok r
        Assert.Equal<PaymentId list>([ p2; p1 ], Assoc.get os.[0].Payments |> List.map (fun p -> p.Id))
        Assert.Equal(Loaded [], os.[1].Payments)
    }

// preload.exs: test "preload nested"
[<Fact>]
let ``ecto: preload nested`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "SKU"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 1 1m
        let! all = allOrders db
        let spec = plasm { for o in orders do preload o.Lines (plasm { for l in orderLines do preload l.Product }) }
        let! r = db.Repo.Preload(spec, all)
        let line = Assoc.get (ok r).Head.Lines |> List.exactlyOne
        Assert.Equal("SKU", (Assoc.get line.Product).Sku)
    }

// preload.exs: test "preload nested via custom query"
[<Fact>]
let ``ecto: preload nested via custom query`` () =
    task {
        let! db = freshDefault ()
        let! a = Seed.product db "A"
        let! b = Seed.product db "B"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o a 1 1m
        let! _ = Seed.line db o b 5 1m

        let big =
            plasm {
                for l in orderLines do
                    where (l.UnitPrice >= 1m)
                    orderByDescending l.Quantity
                    preload l.Product
            }

        let! r = db.Repo.All(plasm { for o in orders do preload o.Lines big })
        let skus = Assoc.get (ok r).Head.Lines |> List.map (fun l -> (Assoc.get l.Product).Sku)
        Assert.Equal<string list>([ "B"; "A" ], skus)
    }

// preload.exs: test "preload many assocs"
[<Fact>]
let ``ecto: preload many assocs`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.line db o p 1 1m
        let! _ = Seed.payment db o 1m "pending" (Seed.at 1)
        let! r = db.Repo.All(plasm { for o in orders do preload o.Lines; preload o.Payments })
        let o = (ok r).Head
        Assert.Equal(1, (Assoc.get o.Lines).Length)
        Assert.Equal(1, (Assoc.get o.Payments).Length)
    }

// preload.exs: test "preload skips already loaded for cardinality many"
[<Fact>]
let ``ecto (deliberate difference): Repo.Preload reloads relationships that are already loaded`` () =
    // Ecto keeps already-loaded associations unless force: true. Plasm treats an
    // explicit load request as authoritative and always reloads, so results never
    // silently mix stale and fresh data.
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let! _ = Seed.payment db o 1m "pending" (Seed.at 1)
        let spec = plasm { for o in orders do preload o.Payments }
        let! all = allOrders db
        let! first = db.Repo.Preload(spec, all)
        let! _ = db.Exec("update payments set status = 'failed'")
        let! second = db.Repo.Preload(spec, ok first)
        Assert.Equal(Pending, (Assoc.get (ok first).Head.Payments).Head.Status)
        Assert.Equal(Failed, (Assoc.get (ok second).Head.Payments).Head.Status)
    }

// lib/ecto/query.ex docs, "Preload queries": limit in a preload query applies globally.
[<Fact>]
let ``ecto (deliberate difference): limit in a preload query is rejected; limitPerParent is per parent`` () =
    task {
        let! db = freshDefault ()
        let! o1 = Seed.order db None "draft" 1m (Seed.at 1)
        let! o2 = Seed.order db None "draft" 2m (Seed.at 2)

        for i in 1..3 do
            let! _ = Seed.payment db o1 (decimal i) "succeeded" (Seed.at i)
            let! _ = Seed.payment db o2 (decimal i) "succeeded" (Seed.at i)
            ()

        let globalLimit = plasm { for p in payments do orderBy p.Amount; take 2 }
        let! rejected = db.Repo.All(plasm { for o in orders do preload o.Payments globalLimit })

        match rejected with
        | Error(DbError.Translation(TranslationError.InvalidQuery _)) -> ()
        | other -> failwithf "expected InvalidQuery, got %A" other

        Assert.Equal(0, db.Recorder.Count)
        let perParent = plasm { for p in payments do orderBy p.Amount; limitPerParent 2 }
        let! r = db.Repo.All(plasm { for o in orders do preload o.Payments perParent })
        Assert.True(ok r |> List.forall (fun o -> (Assoc.get o.Payments |> List.map (fun p -> p.Amount)) = [ 1m; 2m ]))
    }

// ===========================================================================
// Repo (integration_test/cases/repo.exs)
// ===========================================================================

// repo.exs: test "insert, update and delete"
[<Fact>]
let ``ecto: insert, update and delete`` () =
    task {
        let! db = freshDefault ()
        let! inserted = db.Repo.Insert(Changeset.insert order0)
        let inserted = ok (inserted |> Result.mapError string)
        let! updated = db.Repo.Update(inserted |> Changeset.edit (fun o -> { o with Total = 2m }))
        let updated = ok (updated |> Result.mapError string)
        Assert.Equal(2m, updated.Total)
        let! deleted = db.Repo.Delete(Changeset.delete updated)
        Assert.Equal(updated, ok (deleted |> Result.mapError string))
        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// repo.exs: test "insert and update with empty changeset"
[<Fact>]
let ``ecto: insert and update with empty changeset`` () =
    task {
        let! db = freshDefault ()
        let! inserted = db.Repo.Insert(Changeset.insert order0)
        let inserted = ok (inserted |> Result.mapError string)
        db.Recorder.Clear()
        let cs = Changeset.update inserted
        let! a = db.Repo.Update cs
        let! b = db.Repo.Update cs
        Assert.Equal(inserted, ok (a |> Result.mapError string))
        Assert.Equal(inserted, ok (b |> Result.mapError string))
        Assert.Equal(0, db.Recorder.Count)
    }

// repo.exs: test "insert and update with changeset read after writes"
[<Fact>]
let ``ecto: insert and update with changeset read after writes`` () =
    task {
        let! db = freshDefault ()
        let! inserted = db.Repo.Insert(Changeset.insert order0)
        let inserted = ok (inserted |> Result.mapError string)
        Assert.NotEqual(order0.Id, inserted.Id)
        Assert.Equal(OrderId 0L, order0.Id)
        let! stored = db.Scalar<decimal>("select total from orders where id = @i", [ "i", box (let (OrderId v) = inserted.Id in v) ])
        Assert.Equal(10m, stored)
    }

// repo.exs: test "optimistic locking in update/delete operations"
[<Fact>]
let ``ecto: optimistic locking in update/delete operations`` () =
    task {
        let! db = freshDefault ()
        let! base' = db.Repo.Insert(Changeset.insert order0)
        let base' = ok (base' |> Result.mapError string)
        let! okUpdate = db.Repo.Update(base' |> Changeset.edit (fun o -> { o with Total = 3m }))
        Assert.Equal(2, (ok (okUpdate |> Result.mapError string)).Version)
        let! staleUpdate = db.Repo.Update(base' |> Changeset.edit (fun o -> { o with Total = 4m }))
        let! staleDelete = db.Repo.Delete(Changeset.delete base')

        match staleUpdate, staleDelete with
        | Error(WriteError.Stale _), Error(WriteError.Stale _) -> ()
        | a, b -> failwithf "expected stale errors, got %A / %A" a b

        let! total = db.Scalar<decimal>("select total from orders")
        Assert.Equal(3m, total)
    }

// repo.exs: test "unique constraint"
[<Fact>]
let ``ecto: unique constraint`` () =
    task {
        let! db = freshDefault ()
        let cs = Changeset.insert (New.customer "A" "dup@example.com")
        let! first = db.Repo.Insert cs
        ok (first |> Result.mapError string) |> ignore

        // A constraint with PostgreSQL's default name maps to its field, with
        // nothing declared on the changeset (constraints.feature).
        let! duplicate = db.Repo.Insert cs

        match duplicate with
        | Error(WriteError.Invalid c) ->
            Assert.Equal<FieldError list>(
                [ { Field = "Email"
                    Message = "has already been taken"
                    Kind = FieldErrorKind.UniqueConstraint "customers_email_key" } ],
                c.Errors
            )

            Assert.Equal(Some(New.customer "A" "dup@example.com"), c.Data)
        | other -> failwithf "expected Invalid, got %A" other
    }

// repo.exs: test "foreign key constraint"
[<Fact>]
let ``ecto: foreign key constraint`` () =
    task {
        let! db = freshDefault ()
        let! o = Seed.order db None "draft" 1m (Seed.at 1)
        let line = { New.line (ProductId 999L) 1 1m with OrderId = o }
        let! declared = db.Repo.Insert(Changeset.insert line)

        match declared with
        | Error(WriteError.Invalid c) ->
            Assert.Equal(FieldErrorKind.ForeignKeyConstraint "order_lines_product_id_fkey", c.Errors.Head.Kind)
            Assert.Equal("ProductId", c.Errors.Head.Field)
        | other -> failwithf "expected Invalid, got %A" other
    }

// repo.exs: test "insert and update with changeset" — invalid changesets are not written
[<Fact>]
let ``ecto: invalid changeset is not written`` () =
    task {
        let! db = freshDefault ()
        let cs = Changeset.insert order0 |> Changeset.validateRange((fun o -> o.Total), max = 5m)
        let! r = db.Repo.Insert cs

        match r with
        | Error(WriteError.Invalid c) -> Assert.Equal("Total", c.Errors.Head.Field)
        | other -> failwithf "expected Invalid, got %A" other

        Assert.Equal(0, db.Recorder.Count)
    }

// ===========================================================================
// Changesets (test/ecto/changeset_test.exs)
// ===========================================================================

// changeset_test.exs: test "cast/4: with valid string keys"
[<Fact>]
let ``ecto: cast/4: with valid string keys`` () =
    let cs =
        Changeset.update order0
        |> Changeset.cast(input [ "total", "12.50"; "cancellation_reason", "late" ], fun o -> o.Total)
        |> Changeset.cast(input [ "total", "12.50"; "cancellation_reason", "late" ], fun o -> o.CancellationReason)

    Assert.Equal<(string * FieldChange) list>(
        [ "Total", FieldChange.Assigned(box 12.50m); "CancellationReason", FieldChange.Assigned(box (Some "late")) ],
        cs.Changes
    )

    Assert.True cs.IsValid

// changeset_test.exs: cast/4 only casts permitted fields
[<Fact>]
let ``ecto: cast/4: ignores fields that are not permitted`` () =
    let raw = input [ "id", "999"; "version", "7"; "total", "3" ]
    let cs = Changeset.update order0 |> Changeset.cast(raw, fun o -> o.Total)
    Assert.Equal<string list>([ "Total" ], cs.Changes |> List.map fst)

// changeset_test.exs: test "cast/4: with empty values"
[<Fact>]
let ``ecto: cast/4: with empty values`` () =
    let original = { order0 with CancellationReason = Some "late" }
    let cs = Changeset.update original |> Changeset.cast(input [ "cancellation_reason", "" ], fun o -> o.CancellationReason)
    Assert.Equal<(string * FieldChange) list>([ "CancellationReason", FieldChange.Cleared ], cs.Changes)

// changeset_test.exs: cast errors mark the field invalid ("is invalid")
[<Fact>]
let ``ecto: cast/4: type errors mark the field as invalid`` () =
    let cs = Changeset.update order0 |> Changeset.cast(input [ "total", "ten" ], fun o -> o.Total)
    Assert.False cs.IsValid
    Assert.Equal<FieldError list>([ { Field = "Total"; Message = "is invalid"; Kind = FieldErrorKind.CastFailed } ], cs.Errors)
    Assert.Empty cs.Changes

// changeset_test.exs: test "change/2 with a struct"
[<Fact>]
let ``ecto: change/2 with a struct`` () =
    let cs = order0 |> Changeset.edit (fun o -> { o with Total = 11m; Status = order0.Status })
    Assert.Equal<string list>([ "Total" ], cs.Changes |> List.map fst)
    Assert.Equal(Some order0, cs.Data)

// changeset_test.exs: test "put_change/3 and delete_change/2"
[<Fact>]
let ``ecto: put_change/3 drops a change equal to the data`` () =
    let cs =
        Changeset.update order0
        |> Changeset.set((fun o -> o.Total), 20m)
        |> Changeset.set((fun o -> o.Total), order0.Total)

    Assert.Empty cs.Changes

// changeset_test.exs: test "fetch_change/2"
[<Fact>]
let ``ecto: fetch_change/2`` () =
    let cs = Changeset.update order0 |> Changeset.set((fun o -> o.Total), 20m)
    Assert.Equal(Some 20m, cs |> Changeset.tryGetChange(fun o -> o.Total))
    Assert.Equal(None, cs |> Changeset.tryGetChange(fun o -> o.Status))
    Assert.Equal(Some Draft, cs |> Changeset.getField(fun o -> o.Status))

// changeset_test.exs: test "validate_required/3"
[<Fact>]
let ``ecto: validate_required/3`` () =
    let blank = Changeset.insert (New.customer "  " "a@x") |> Changeset.validateRequired(fun (c: Customer) -> c.Name)
    Assert.Equal<FieldError list>([ { Field = "Name"; Message = "can't be blank"; Kind = FieldErrorKind.Required } ], blank.Errors)
    let missing = Changeset.insert order0 |> Changeset.validateRequired(fun o -> o.CancellationReason)
    Assert.Equal("CancellationReason", missing.Errors.Head.Field)
    let present = Changeset.insert (New.customer "Ada" "a@x") |> Changeset.validateRequired(fun (c: Customer) -> c.Name)
    Assert.True present.IsValid

// changeset_test.exs: test "validate_length/3 with string"
[<Fact>]
let ``ecto: validate_length/3 with string`` () =
    let v name = Changeset.insert (New.customer name "a@x") |> Changeset.validateLength((fun (c: Customer) -> c.Name), min = 3, max = 5)
    Assert.True((v "abcd").IsValid)
    Assert.Equal("should be at least 3 character(s)", (v "ab").Errors.Head.Message)
    Assert.Equal("should be at most 5 character(s)", (v "abcdef").Errors.Head.Message)

// changeset_test.exs: test "validate_number/3"
[<Fact>]
let ``ecto: validate_number/3`` () =
    let v t = Changeset.insert { order0 with Total = t } |> Changeset.validateRange((fun o -> o.Total), min = 0m, max = 100m)
    Assert.True((v 0m).IsValid)
    Assert.True((v 100m).IsValid)
    Assert.Equal(FieldErrorKind.Range, (v -1m).Errors.Head.Kind)
    Assert.Equal(FieldErrorKind.Range, (v 101m).Errors.Head.Kind)

// changeset_test.exs: test "validate_inclusion/3"
[<Fact>]
let ``ecto: validate_inclusion/3`` () =
    let v s = Changeset.insert { order0 with Status = s } |> Changeset.validateInclusion((fun o -> o.Status), [ Draft; Published ])
    Assert.True((v Published).IsValid)
    Assert.Equal<FieldError list>([ { Field = "Status"; Message = "is invalid"; Kind = FieldErrorKind.Inclusion } ], (v Cancelled).Errors)

// changeset_test.exs: test "validate_format/4"
[<Fact>]
let ``ecto: validate_format/4`` () =
    let v e = Changeset.insert (New.customer "A" e) |> Changeset.validateFormat((fun c -> c.Email), "@")
    Assert.True((v "a@x").IsValid)
    Assert.Equal<FieldError list>([ { Field = "Email"; Message = "has invalid format"; Kind = FieldErrorKind.Format } ], (v "ax").Errors)

// changeset_test.exs: validations other than validate_required only run on changes
[<Fact>]
let ``ecto: validations on updates only check changed fields`` () =
    let invalidData = { order0 with Total = -5m }
    let unchanged = Changeset.update invalidData |> Changeset.validateRange((fun o -> o.Total), min = 0m)
    Assert.True unchanged.IsValid
    let changed = Changeset.update invalidData |> Changeset.set((fun o -> o.Total), -1m) |> Changeset.validateRange((fun o -> o.Total), min = 0m)
    Assert.False changed.IsValid

// ===========================================================================
// Multi (test/ecto/multi_test.exs) — Plasm's Tx
// ===========================================================================

// multi_test.exs: test "new" (empty multi) / repo transaction of an empty multi
[<Fact>]
let ``ecto: empty multi`` () =
    task {
        let! db = freshDefault ()
        let! r = db.Repo.Transact(transaction { return () })
        Assert.Equal(Ok(), r)
        Assert.Equal(0, db.Recorder.Count)
    }

// multi_test.exs: tests "insert changeset", "update changeset" and "insert fun"
[<Fact>]
let ``ecto: insert and update changesets with dependent steps`` () =
    task {
        let! db = freshDefault ()
        let! p = Seed.product db "A"

        let tx =
            transaction {
                let! o = Tx.insert (Changeset.insert order0)
                let! l = Tx.insert (Changeset.insert { New.line p 1 1m with OrderId = o.Id })
                let! o2 = Tx.update (o |> Changeset.edit (fun o -> { o with Status = Published }))
                return o2, l
            }

        let! r = db.Repo.Transact tx
        let o, l = ok (r |> Result.mapError string)
        Assert.Equal(o.Id, l.OrderId)
        Assert.Equal(Published, o.Status)
    }

// multi_test.exs: test "error" — failure identifies the operation; earlier work rolls back
[<Fact>]
let ``ecto: error`` () =
    task {
        let! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert (Changeset.insert order0)
                let! (_: unit) = Tx.fail "nope"
                return ()
            }

        let! r = db.Repo.Transact tx

        match r with
        | Error e ->
            Assert.Equal("fail", e.Step.Action)
            match e.Failure with
            | TxFailure.Rejected r -> Assert.Equal("nope", r)
            | other -> failwithf "expected a rejection, got %A" other

            Assert.Equal<string list>([ "insert Order" ], e.RolledBack |> List.map (fun s -> s.Action))
        | Ok _ -> failwith "expected failure"

        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// multi_test.exs / repo transaction semantics: an invalid changeset fails its named operation
[<Fact>]
let ``ecto: multi with invalid changeset returns the failed operation and its errors`` () =
    task {
        let! db = freshDefault ()
        let bad = Changeset.insert order0 |> Changeset.validateRange((fun o -> o.Total), max = 1m)

        let tx =
            transaction {
                let! _ = Tx.insert (Changeset.insert order0)
                let! _ = Tx.insert bad
                return ()
            }

        let! r = db.Repo.Transact tx

        match r with
        | Error { Step = step; Failure = TxFailure.InvalidChanges("Order", errors) } when step.Action = "insert Order" && step.Line > 0 ->
            Assert.Equal("Total", errors.Head.Field)
        | other -> failwithf "unexpected %A" other

        let! n = db.Scalar<int64>("select count(*) from orders")
        Assert.Equal(0L, n)
    }

// multi_test.exs: test "run with fun"
[<Fact>]
let ``ecto: run with fun`` () =
    task {
        let! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert (Changeset.insert order0)

                let! count =
                    Tx.run (fun ctx ->
                        task {
                            use cmd = ctx.CreateCommand()
                            cmd.CommandText <- "select count(*) from orders"
                            let! v = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                            return Ok(unbox<int64> v)
                        })

                return count
            }

        let! r = db.Repo.Transact tx
        Assert.Equal(Ok 1L, r |> Result.mapError string)
    }

// multi_test.exs: test "all queryable"
[<Fact>]
let ``ecto: all queryable`` () =
    task {
        let! db = freshDefault ()

        let tx =
            transaction {
                let! _ = Tx.insert (Changeset.insert order0)
                let! seen = Tx.all (Query.from orders)
                return seen.Length
            }

        let! r = db.Repo.Transact tx
        Assert.Equal(Ok 1, r |> Result.mapError string)
    }

// ===========================================================================
// Query semantics
// ===========================================================================

// Ecto raises on `== nil` comparisons and requires is_nil/1.
[<Fact>]
let ``ecto (deliberate difference): comparing with None means IS NULL`` () =
    task {
        let! db = freshDefault ()
        let! a = Seed.order db None "draft" 1m (Seed.at 1)
        let! b = Seed.order db None "draft" 2m (Seed.at 2)
        let! _ = db.Exec("update orders set cancellation_reason = 'late' where id = @i", [ "i", box (let (OrderId v) = b in v) ])
        let! r = db.Repo.All(plasm { for o in orders do where (o.CancellationReason = None) })
        Assert.Equal<OrderId list>([ a ], ok r |> List.map (fun o -> o.Id))
        let! r2 = db.Repo.All(plasm { for o in orders do where (o.CancellationReason <> Some "late") })
        Assert.Equal<OrderId list>([ a ], ok r2 |> List.map (fun o -> o.Id))
    }
