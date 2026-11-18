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
                        orderBy t.Title
                        take 2
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

