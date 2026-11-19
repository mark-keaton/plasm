/// The application's own records and unions, beside its queries, as an
/// application declares them (Kanban's shapes, examples/Kanban).
module Plasm.Aot.App

open System
open Plasm
open Plasm.Aot.Shop

type TicketId = TicketId of int64
type NoteId = NoteId of int64

type Priority =
    | Low
    | High

type Stage =
    | Todo
    | Doing of who: string * since: DateTimeOffset
    | Done of at: DateTimeOffset

/// An item of a ticket's checklist, held in one JSON document.
type Item = { Text: string; Done: bool }

[<Table "tickets">]
type Ticket =
    { Id: TicketId
      Title: string
      Owner: CustomerId option
      Stage: Stage
      Due: DateOnly option
      Points: int64 option
      Priority: Priority option
      Created: DateTimeOffset
      Size: int
      Price: decimal
      Ref: Guid
      /// Lists of scalars, each one array column (array-columns.feature).
      Labels: string list
      Watchers: CustomerId list
      [<Json>]
      Checklist: Item list
      Notes: Note list }

    /// Who works on the ticket: a fact of one stage, readable in a query.
    [<ReflectedDefinition>]
    member t.Assignee =
        match t.Stage with
        | Doing(who, _) -> Some who
        | _ -> None

/// A note a ticket owns.
and [<Table "notes">] Note = { Id: NoteId; TicketId: TicketId; Text: string; Done: bool }

/// What a ticket's stage is, without its facts.
[<RequireQualifiedAccess>]
type Phase =
    | Waiting
    | Working
    | Finished

/// The application's own function over a stage, readable in a query (as
/// Kanban's `kind`).
[<ReflectedDefinition>]
let phase (t: Ticket) =
    match t.Stage with
    | Todo -> Phase.Waiting
    | Doing _ -> Phase.Working
    | Done _ -> Phase.Finished

/// Rows queries return.
type TicketLine = { Title: string; Points: int64 option }
type WorkLine = { Customer: string; Ticket: string; Who: string option }
type CustomerLine = { Customer: Customer; Titles: string list; Started: string list }
type SizeLine = { Title: string; Size: int }

/// An API contract, as a web application declares one beside its records:
/// functions, which nothing stores.
type TicketApi = { getTicket: int64 -> Async<Ticket option> }

/// The application's own word, readable in a query.
[<ReflectedDefinition>]
let named (name: string) (c: Customer) = c.Name = name

