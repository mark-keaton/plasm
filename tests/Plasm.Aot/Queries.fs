/// The application's queries, as values of a module of their own file: read
/// when the module is first used, every one of them (as Kanban's Workflows.fs).
module Plasm.Aot.Queries

open System
open Plasm
open Plasm.Aot.Shop
open Plasm.Aot.App

let ticketById = Query.prepare (fun (id: Arg<TicketId>) -> plasm { for t in tableOf<Ticket> do where (t.Id = id.Value) })

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
