/// Queries that read a union case's field (Kanban's Card.Assignee), in a file
/// of their own: a native binary cannot read them yet (docs/aot.md), and a
/// file's module values are read together, so these would take the others
/// with them.
module Plasm.Aot.CaseFieldQueries

open System
open Plasm
open Plasm.Aot.Shop
open Plasm.Aot.App

let assignedTo =
    Query.prepare (fun (who: Arg<string>) -> plasm { for t in tableOf<Ticket> do where (t.Assignee = Some who.Value) })

let work =
    plasm {
        for t in tableOf<Ticket> do
            join c in tableOf<Customer> on (t.Owner = Some c.Id)
            orderBy t.Title
            select { Customer = c.Name; Ticket = t.Title; Who = t.Assignee }
    }

