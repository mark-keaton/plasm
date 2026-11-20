/// The application's queries, as values of a module of their own file: read
/// when the module is first used, every one of them (as Kanban's Workflows.fs).
module Plasm.Aot.Queries

open System
open Plasm
open Plasm.Aot.Shop
open Plasm.Aot.App

let ticketById = Query.prepare (fun (id: Arg<TicketId>) -> plasm { for t in tableOf<Ticket> do where (t.Id = id.Value) })

/// The first ticket, locked for update and skipped when another transaction
/// holds it: a job queue's claim, read only in a transaction (row-locks.feature).
let claimTicket =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Id
            take 1
            locking RowLock.ForUpdate.SkipLocked
    }

/// Filtered and boolean aggregates (aggregates.feature), read natively.
let sizeFacts =
    plasm {
        for t in tableOf<Ticket> do
            select (Agg.countWhere (t.Size > 3), Agg.sumWhere (t.Size > 3) t.Price, Agg.any (t.Size > 3), Agg.all (t.Size > 0))
    }

/// List and text aggregates in a stated order (aggregates.feature), read natively.
let sizesInOrder =
    plasm {
        for t in tableOf<Ticket> do
            select (
                Agg.listBy t.Size t.Created,
                Agg.listByDescending t.Size t.Created,
                Agg.stringConcatBy ", " t.Title t.Created,
                Agg.stringConcatByDescending ", " t.Title t.Created
            )
    }

/// Window functions over an order (window-functions.feature), read natively.
let sizeRanks =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Created
            select (
                Win.rowNumber (Win.whole |> Win.orderBy t.Created),
                Win.rank (Win.whole |> Win.orderByDescending t.Size),
                Win.denseRank (Win.partition t.Title |> Win.orderBy t.Size |> Win.thenByDescending t.Created),
                Win.lag t.Size (Win.whole |> Win.orderBy t.Created |> Win.thenBy t.Title),
                Win.lead t.Size (Win.whole |> Win.orderBy t.Created),
                Win.runningSum t.Price (Win.whole |> Win.orderBy t.Created)
            )
    }

/// Window functions over every row of a window (window-functions.feature).
let sizeWindows =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Created
            select (Win.sum t.Size Win.whole, Win.avg t.Price (Win.partition t.Title), Win.count Win.whole)
    }

let withOpenNotes =
    plasm {
        for t in tableOf<Ticket> do
            where (t.Notes |> List.exists (fun n -> not n.Done))
    }

let withOpenItems =
    plasm {
        for t in tableOf<Ticket> do
            where (t.Checklist |> List.exists (fun i -> not i.Done))
    }

let customersWithTickets =
    plasm {
        for c in tableOf<Customer> do
            orderBy c.Name

            let! titles =
                plasm {
                    for t in referring<Ticket> c do
                        orderByDescending t.Created
                        take 3
                        select t.Title
                }

            and! started =
                plasm {
                    for t in referring<Ticket> c do
                        where t.Stage.IsDoing
                        select t.Title
                }

            return { Customer = c; Titles = titles; Started = started }
    }

let page =
    Query.prepare2 (fun (from: Arg<int>) (size: Arg<int>) ->
        plasm {
            for t in tableOf<Ticket> do
                orderBy t.Title
                skip from.Value
                take size.Value
        })

let ticketsOf =
    Query.prepare (fun (owner: Arg<CustomerId>) -> plasm { for t in tableOf<Ticket> do where (t.Owner = Some owner.Value) })


/// Each a value type only the quotation names.
/// The tickets a customer watches: an argument in an array column.
let watchedBy =
    Query.prepare (fun (who: Arg<CustomerId>) -> plasm { for t in tableOf<Ticket> do where (List.contains who.Value t.Watchers) })

/// A shipment by its whole key, a record of two parts, as one argument.
let shipmentByKey =
    Query.prepare (fun (key: Arg<ShipmentKey>) -> plasm { for x in tableOf<Shipment> do where (x.Id = key.Value) })

let bySize =
    plasm {
        for t in tableOf<Ticket> do
            groupBy t.Size
            select (t.Size, Agg.count ())
    }

let titlesAndSizes =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Title
            select (t.Title, t.Size)
    }

let sizes =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Title
            select { Title = t.Title; Size = t.Size }
    }

let totalPrice =
    plasm {
        for t in tableOf<Ticket> do
            groupBy t.Owner
            select (t.Owner, Agg.sum t.Price)
    }

let withRefs =
    Query.prepare (fun (refs: Arg<Guid list>) -> plasm { for t in tableOf<Ticket> do where (List.contains t.Ref refs.Value) })

let withPoints = plasm { for t in tableOf<Ticket> do where (t.Points = Some 3L) }

let withPointsOf =
    Query.prepare (fun (points: Arg<int64>) -> plasm { for t in tableOf<Ticket> do where (t.Points = Some points.Value) })

/// As Kanban's cardCounts: grouped by a field and a reflected function's
/// result, selecting both and a count.
let phaseCounts =
    plasm {
        for t in tableOf<Ticket> do
            groupBy (t.Owner, phase t)
            select (t.Owner, phase t, Agg.count ())
    }

/// The rest of what a query names inside its quotations (QuerySurface.fs),
/// so the binary shows each works natively: nested queries with each builder
/// operation, every aggregate, a raw fragment, and the tie the other way.
let customersWithTickets2 =
    plasm {
        for c in tableOf<Customer> do
            where (Raw.fragment<bool> "{0} ILIKE 'a%%'" c.Name)
            where (exists (plasm { for t in tableOf<Ticket> do where (t.Owner = Some c.Id) }))
            orderBy c.Name

            let! ordered =
                plasm {
                    for t in referring<Ticket> c do
                        orderBy t.Size
                        thenBy t.Title
                        thenByDescending t.Created
                        skip 0
                        take 5
                        select t.Title
                }

            and! sizes =
                plasm {
                    for t in referring<Ticket> c do
                        selectDistinct t.Size
                }

            return {| Name = c.Name; Ordered = ordered; Sizes = sizes |}
    }

/// A load that joins and groups, which Plasm refuses: inside a quotation
/// join, leftJoin, groupBy and having are only read to say so. A function, so
/// the start-up check does not refuse the application for it; the step shows
/// the refusal reads the same natively.
let refusedLoad () =
    plasm {
        for c in tableOf<Customer> do
            let! openNotes =
                plasm {
                    for t in referring<Ticket> c do
                        join n in tableOf<Note> on (t.Id = n.TicketId)
                        leftJoin o in tableOf<Customer> on (t.Owner = Some o.Id)
                        where (not n.Done)
                        groupBy t.Size
                        having (Agg.count () > 0L)
                        select t.Size
                }

            return {| Name = c.Name; OpenNotes = openNotes |}
    }

let ticketsWithOwners =
    plasm {
        for t in tableOf<Ticket> do
            orderBy t.Title

            let! owner = plasm { for c in referredBy<Customer> t do select c.Name }

            return {| Title = t.Title; Owner = owner |}
    }

let priceRanges =
    plasm {
        for t in tableOf<Ticket> do
            groupBy t.Owner
            select (t.Owner, Agg.min t.Price, Agg.max t.Price, Agg.avg t.Price, Agg.countDistinct t.Size)
    }

/// A function of the application a query cannot translate (not a
/// [<ReflectedDefinition>]), so the refusal names it with its arguments.
let labelled (title: string) (price: decimal) (tag: string) (n: int64) (weight: float) (mark: char) (flag: bool) =
    title + string price + tag + string n + string weight + string mark + string flag

/// A prepared query whose condition computes from its argument.
let withPointsTimesThree =
    Query.prepare (fun (n: Arg<int>) -> plasm { for t in tableOf<Ticket> do where (t.Points = Some(int64 (n.Value * 3))) })

/// A query Plasm refuses, its message naming literal values of each kind:
/// the message reads the same natively (docs/aot.md).
let refusedWithValues () =
    plasm {
        for t in tableOf<Ticket> do
            where (labelled t.Title 1.5m "s" 2L 1.0 'x' true = "")
    }

/// Why a change was refused, carrying a value of each structured kind, so the
/// rejection's message shows them the same natively (docs/aot.md).
type Refusal =
    { Reason: string
      Stage: Stage
      Points: int64 option
      Sizes: int list }
