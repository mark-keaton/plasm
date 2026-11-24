/// Shared equipment and the rules for booking it. Plain F# values and
/// functions; the attributes tell Plasm how each record is stored.
namespace Starter

open System
open Plasm

type EquipmentId = EquipmentId of Guid
type BookingId = BookingId of Guid

/// What equipment is called: 1 to 60 characters, trimmed. Made only by
/// create, which Plasm also reads every stored name through.
type EquipmentName =
    private
    | EquipmentName of string

    member n.Value =
        let (EquipmentName v) = n
        v

    static member create(text: string | null) : Result<EquipmentName, string> =
        let t = (match text with null -> "" | t -> t).Trim()

        if t.Length = 0 then Error "is required"
        elif t.Length > 60 then Error "must be at most 60 characters"
        else Ok(EquipmentName t)

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
      Name: EquipmentName
      Condition: Condition
      [<OrderedBy(nameof the<Booking>.From)>]
      Bookings: Booking list
      [<Version>]
      Version: int }

/// Why the rules refuse. C# is given each as Refused: the case's name, and
/// its Message.
type Refusal =
    | WhoRequired
    | ReasonRequired
    | EndsBeforeItStarts
    | NotAvailable of reason: string
    | AlreadyBooked of by: string
    | AlreadyOutOfService
    | AlreadyAvailable

    /// Why, in the rules' own words.
    member refusal.Message =
        match refusal with
        | WhoRequired -> "A booking needs the name of who it is for."
        | ReasonRequired -> "Taking equipment out of service needs a reason."
        | EndsBeforeItStarts -> "A booking must end after it starts."
        | NotAvailable reason -> $"The equipment is out of service: {reason}."
        | AlreadyBooked by -> $"{by} has it booked for some of that time."
        | AlreadyOutOfService -> "The equipment is already out of service."
        | AlreadyAvailable -> "The equipment is already available."

module Equipment =

    let create (id: EquipmentId) (name: EquipmentName) =
        { Id = id
          Name = name
          Condition = Available
          Bookings = []
          Version = 1 }

    /// Whether `booking` has any of the time from `from` until `until`.
    let private overlaps (from: DateTimeOffset) (until: DateTimeOffset) (booking: Booking) =
        booking.From < until && from < booking.Until

    /// Books the equipment for `who` from `from` until `until`. Refused when
    /// nobody is named, when it ends before it starts, when the equipment
    /// is out of service, or when someone has some of that time.
    let book (id: BookingId) (who: string) (from: DateTimeOffset) (until: DateTimeOffset) (equipment: Equipment) =
        if String.IsNullOrWhiteSpace who then
            Error WhoRequired
        elif until <= from then
            Error EndsBeforeItStarts
        else
            match equipment.Condition with
            | OutOfService(reason, _) -> Error(NotAvailable reason)
            | Available ->
                match equipment.Bookings |> List.tryFind (overlaps from until) with
                | Some taken -> Error(AlreadyBooked taken.Who)
                | None ->
                    let booking =
                        { Id = id
                          Equipment = equipment.Id
                          Who = who.Trim()
                          From = from
                          Until = until }

                    Ok { equipment with Bookings = equipment.Bookings @ [ booking ] }

    /// Out of service from `now`, saying why. Refused with no reason, or
    /// when it is out of service already.
    let takeOutOfService (reason: string) (now: DateTimeOffset) (equipment: Equipment) =
        if String.IsNullOrWhiteSpace reason then
            Error ReasonRequired
        else
            match equipment.Condition with
            | OutOfService _ -> Error AlreadyOutOfService
            | Available -> Ok { equipment with Condition = OutOfService(reason.Trim(), now) }

    /// Available again. Refused when it is available already.
    let returnToService (equipment: Equipment) =
        match equipment.Condition with
        | Available -> Error AlreadyAvailable
        | OutOfService _ -> Ok { equipment with Condition = Available }
