/// Shared equipment and the rules for booking it. Plain F# values and
/// functions; the attributes tell Plasm how each record is stored.
namespace Starter

open System
open Plasm

type EquipmentId = EquipmentId of Guid
type BookingId = BookingId of Guid

/// Whether the equipment can be booked. Out of service says why, and since when.
type Condition =
    | Available
    | OutOfService of reason: string * since: DateTimeOffset

/// A booking holds its equipment's id, so it belongs to it: stored, changed
/// and deleted with the equipment.
[<Table "bookings">]
type Booking =
    { Id: BookingId
      Equipment: EquipmentId
      Who: string
      From: DateTimeOffset
      Until: DateTimeOffset }

[<Table "equipment">]
type Equipment =
    { Id: EquipmentId
      [<Unique>]
      Name: string
      Condition: Condition
      [<OrderedBy(nameof the<Booking>.From)>]
      Bookings: Booking list
      [<Version>]
      Version: int }

/// Why the rules refuse.
type Refusal =
    | NameRequired
    | WhoRequired
    | ReasonRequired
    | EndsBeforeItStarts
    | NotAvailable of reason: string
    | AlreadyBooked of by: string
    | AlreadyOutOfService
    | AlreadyAvailable

module Refusal =
    let describe refusal =
        match refusal with
        | NameRequired -> "Equipment needs a name."
        | WhoRequired -> "A booking needs the name of who it is for."
        | ReasonRequired -> "Taking equipment out of service needs a reason."
        | EndsBeforeItStarts -> "A booking must end after it starts."
        | NotAvailable reason -> $"The equipment is out of service: {reason}."
        | AlreadyBooked by -> $"{by} has it booked for some of that time."
        | AlreadyOutOfService -> "The equipment is already out of service."
        | AlreadyAvailable -> "The equipment is already available."

module Equipment =

    let create (id: EquipmentId) (name: string) =
        if String.IsNullOrWhiteSpace name then
            Error NameRequired
        else
            Ok
                { Id = id
                  Name = name.Trim()
                  Condition = Available
                  Bookings = []
                  Version = 1 }

    /// Books the equipment for `who` from `from` until `until`, if it is
    /// available and nobody has any of that time.
    let book (id: BookingId) (who: string) (from: DateTimeOffset) (until: DateTimeOffset) (equipment: Equipment) =
        let clash = equipment.Bookings |> List.tryFind (fun b -> b.From < until && from < b.Until)

        match equipment.Condition, clash with
        | _ when String.IsNullOrWhiteSpace who -> Error WhoRequired
        | _ when until <= from -> Error EndsBeforeItStarts
        | OutOfService(reason, _), _ -> Error(NotAvailable reason)
        | Available, Some other -> Error(AlreadyBooked other.Who)
        | Available, None ->
            let booking =
                { Id = id
                  Equipment = equipment.Id
                  Who = who.Trim()
                  From = from
                  Until = until }

            Ok { equipment with Bookings = equipment.Bookings @ [ booking ] }

    let takeOutOfService (reason: string) (now: DateTimeOffset) (equipment: Equipment) =
        match equipment.Condition with
        | _ when String.IsNullOrWhiteSpace reason -> Error ReasonRequired
        | OutOfService _ -> Error AlreadyOutOfService
        | Available -> Ok { equipment with Condition = OutOfService(reason.Trim(), now) }

    let returnToService (equipment: Equipment) =
        match equipment.Condition with
        | Available -> Error AlreadyAvailable
        | OutOfService _ -> Ok { equipment with Condition = Available }
