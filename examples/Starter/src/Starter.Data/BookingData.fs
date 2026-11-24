/// What C# calls: one type, BookingData. Its members take and return the
/// domain's own types; an outcome that can go more than one way is one of
/// Plasm.CSharp's (FindResult, AddResult, ChangeResult of the equipment and the rules'
/// Refusal), and a read of many is an array. The seam (Starter.Api's
/// Seam.cs) writes the C# the API and the tests call: a view of each record
/// and union, and a C# BookingData over this one, whose outcomes are of those
/// views, each refusal a Refused. No view, and no mapping to one, is written
/// here, and no plumbing: BookingStore, at the end, says where the data is.
/// A database that cannot be reached throws Plasm.CSharp's
/// UnavailableException.
namespace Starter.Data

open System
open System.Threading
open System.Threading.Tasks
open Plasm
open Plasm.CSharp
open Starter

module private Db =
    let model = Model.buildOrFail [] [ typeof<Equipment> ]

    let byId =
        Query.prepare (fun (id: Arg<EquipmentId>) -> plasm { for e in tableOf<Equipment> do where (e.Id = id.Value) })

    /// Every piece of equipment, ordered by name.
    let byName = plasm { for e in tableOf<Equipment> do orderBy e.Name }

/// What the API is asked, as its requests' bodies say it: the seam gives C#
/// a record of the same fields for each, reads its JSON, and makes this one
/// from it, so an endpoint passes its body whole.
type NewEquipment = { Name: string }

type NewBooking =
    { Version: int
      Who: string
      From: DateTimeOffset
      Until: DateTimeOffset }

type OutOfService = { Version: int; Reason: string }

type InService = { Version: int }

[<Sealed>]
/// What C# calls: the operations, each over the repository and the clock
/// BookingStore makes it with.
type BookingData(repo: Repo, time: TimeProvider) =

    /// Adds equipment called `name`: Invalid, with the field, when the name
    /// is not one (EquipmentName.create) or is taken.
    member _.AddEquipmentAsync(request: NewEquipment, cancellationToken: CancellationToken) : Task<AddResult<Equipment>> =
        task {
            match EquipmentName.create request.Name with
            | Error message -> return AddResult(Invalid.field "name" message)
            | Ok name ->
                let equipment = Equipment.create (EquipmentId(Guid.CreateVersion7())) name
                let! result = repo.Transact((Tx.insert equipment: Tx<Equipment, unit>), cancellationToken = cancellationToken)
                return Outcome.added result
        }

    member _.EquipmentAsync(id: EquipmentId, cancellationToken: CancellationToken) : Task<FindResult<Equipment>> =
        task {
            let! result = repo.One(Db.byId id, cancellationToken)
            return Outcome.found "equipment" result
        }

    /// Every piece of equipment, ordered by name: none is an empty array.
    member _.AllEquipmentAsync(cancellationToken: CancellationToken) : Task<Equipment[]> =
        task {
            let! result = repo.All(Db.byName, cancellationToken = cancellationToken)
            return Outcome.all "equipment" result
        }

    /// Books the equipment, if `version` is the one stored. `from` and
    /// `until` may come with any offset (a request's, a user's): they are the
    /// instants they say, kept in UTC, as Plasm stores times.
    member _.BookAsync(id: EquipmentId, request: NewBooking, cancellationToken: CancellationToken) =
        let booking = BookingId(Guid.CreateVersion7())
        let from = request.From.ToUniversalTime()
        let until = request.Until.ToUniversalTime()
        let rule = Equipment.book booking request.Who from until
        repo.Change("equipment", Db.byId id, request.Version, rule, cancellationToken)

    /// Out of service from now, as the clock says it in UTC, whatever offset
    /// the clock answers with.
    member _.TakeOutOfServiceAsync(id: EquipmentId, request: OutOfService, cancellationToken: CancellationToken) =
        let now = time.GetUtcNow().ToUniversalTime()
        let rule = Equipment.takeOutOfService request.Reason now
        repo.Change("equipment", Db.byId id, request.Version, rule, cancellationToken)

    member _.ReturnToServiceAsync(id: EquipmentId, request: InService, cancellationToken: CancellationToken) =
        repo.Change("equipment", Db.byId id, request.Version, Equipment.returnToService, cancellationToken)

/// Where BookingData's data is, from Store.fs. The seam gives C# all the
/// rest from it: BookingData opened (Plasm checking every query and the
/// tables first), its DDL, a test's sandbox, the database made where it is a
/// file, and AddBookingData for ASP.NET.
module BookingStore =
    let private reached: Store<BookingData> =
        { Model = Db.model
          Provider = Store.provider
          Printing = Store.printing
          Queries = typeof<Equipment>.Assembly
          Make = fun repo time -> BookingData(repo, time) }

#if STARTER_SQLITE
    let store: FileStore<BookingData> = { Store = reached; File = Store.file }
#else
    let store: Store<BookingData> = reached
#endif
