/// What C# calls: one type, BookingData. Its methods take plain arguments and
/// return a Task of plain types; an outcome that can go more than one way is
/// one of Plasm.CSharp's unions (Found, Added, Changed), which C# switches
/// over, and the compiler names a case it does not handle. Nothing returned is
/// null, an F# option or an F# Result; a database that cannot be reached
/// throws Plasm.CSharp's UnavailableException.
namespace Starter.Data

open System
#if STARTER_SQLITE
open System.IO
#endif
open System.Runtime.CompilerServices
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Plasm
open Plasm.CSharp
open Starter

/// Available equipment: a class, since a record needs a field.
[<Sealed>]
type Bookable() =
    [<JsonPropertyOrder(-1)>]
    member _.Kind = "bookable"

type Withdrawn =
    { Reason: string
      Since: DateTimeOffset }

    [<JsonPropertyOrder(-1)>]
    member _.Kind = "withdrawn"

/// Which case of ConditionView a JSON object is, by its "kind". The JSON
/// source generator takes a union-shaped type only with a classifier.
type ConditionByKind() =
    inherit ByKind(typeof<ConditionView>, [ "bookable", typeof<Bookable>; "withdrawn", typeof<Withdrawn> ])

and [<Struct; Union; JsonUnion(TypeClassifier = typeof<ConditionByKind>)>] ConditionView =
    val private value: obj
    new(bookable: Bookable) = { value = bookable :> obj }
    new(withdrawn: Withdrawn) = { value = withdrawn :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

type BookingView =
    { Who: string
      From: DateTimeOffset
      Until: DateTimeOffset }

type EquipmentView =
    { Id: Guid
      Name: string
      Condition: ConditionView
      Bookings: BookingView[]
      Version: int }

module private Db =
    let model = Model.buildOrFail [] [ typeof<Equipment> ]

    let byId =
        Query.prepare (fun (id: Arg<EquipmentId>) -> plasm { for e in tableOf<Equipment> do where (e.Id = id.Value) })

    /// Why a transaction stopped without a database failure.
    type Stop =
        | Ruled of Refusal
        | Gone

    /// The equipment as the caller saw it (`seen`), changed as `decide` says.
    let change (id: EquipmentId) (seen: int) (decide: Equipment -> Result<Equipment, Refusal>) : Tx<Equipment, Stop> =
        transaction {
            let! equipment = Tx.oneAsSeen (byId id, seen) |> Tx.orReject Gone
            let! changed = decide equipment |> Tx.ofResult Ruled
            return! Tx.update (equipment, changed)
        }

    /// The tables, as Plasm would create them: the model alone, no database.
    let ddl =
        lazy (Repo.Start(model, Store.printing (), typeof<Stop>.Assembly, RepoOptions.Unchecked).Result.Ddl())

module private View =
    let condition (condition: Condition) =
        match condition with
        | Available -> ConditionView(Bookable())
        | OutOfService(reason, since) -> ConditionView { Reason = reason; Since = since }

    let equipment (e: Equipment) : EquipmentView =
        let (EquipmentId id) = e.Id

        { Id = id
          Name = e.Name.Value
          Condition = condition e.Condition
          Bookings = [| for b in e.Bookings -> { Who = b.Who; From = b.From; Until = b.Until } |]
          Version = e.Version }

    let refused (refusal: Refusal) =
        let code =
            match refusal with
            | WhoRequired -> "WhoRequired"
            | ReasonRequired -> "ReasonRequired"
            | EndsBeforeItStarts -> "EndsBeforeItStarts"
            | NotAvailable _ -> "NotAvailable"
            | AlreadyBooked _ -> "AlreadyBooked"
            | AlreadyOutOfService -> "AlreadyOutOfService"
            | AlreadyAvailable -> "AlreadyAvailable"

        { Code = code; Message = Refusal.describe refusal }

[<Sealed>]
type BookingData private (repo: Repo, time: TimeProvider) =

    /// Why a change stopped, as the case of Changed each means.
    let rejected (stop: Db.Stop) : Changed<EquipmentView> =
        match stop with
        | Db.Gone -> Changed { What = "equipment" }
        | Db.Ruled refusal -> Changed(View.refused refusal)

    let changed (tx: Tx<Equipment, Db.Stop>) (ct: CancellationToken) : Task<Changed<EquipmentView>> =
        task {
            let! result = repo.Transact(tx, cancellationToken = ct)
            return Outcome.changed View.equipment rejected result
        }

    static member private Open(connectionString: string, onSql: Action<string> option, time: TimeProvider, cancellationToken: CancellationToken) =
        task {
            let options =
                match onSql with
                | None -> RepoOptions.Default
                | Some onSql -> { RepoOptions.Default with OnCommand = Some(fun e -> onSql.Invoke e.Sql) }

            let! repo = Repo.Start(Db.model, Store.provider connectionString, typeof<BookingData>.Assembly, options, cancellationToken)
            return BookingData(repo, time)
        }

    /// Starts Plasm's repository over the database `connectionString` names,
    /// opened as Store.fs says (where its connections are configured). It
    /// checks every query and the tables first, so one that would fail throws
    /// here, before anything is served. Every change takes its time from
    /// `time`, the application's one clock: TimeProvider.System, or a test's own.
    static member OpenAsync(connectionString: string, time: TimeProvider, cancellationToken: CancellationToken) : Task<BookingData> =
        BookingData.Open(connectionString, None, time, cancellationToken)

    /// As OpenAsync, `onSql` given the SQL of each command sent, BEGIN and
    /// COMMIT included (a log).
    static member OpenAsync(connectionString: string, onSql: Action<string>, time: TimeProvider, cancellationToken: CancellationToken) : Task<BookingData> =
        BookingData.Open(connectionString, Some onSql, time, cancellationToken)

    /// The SQL that creates the tables.
    static member Ddl: string = Db.ddl.Value
#if STARTER_SQLITE

    /// The file `connectionString` names, its full path.
    static member DatabaseFile(connectionString: string) : string = Store.file connectionString

    /// Makes the database: the file `connectionString` names, with the tables
    /// Ddl creates. A file already there is never changed: IOException.
    static member CreateDatabaseAsync(connectionString: string, cancellationToken: CancellationToken) : Task =
        task {
            let file = Store.file connectionString

            if File.Exists file then
                raise (IOException $"{file} exists: a database is made only where there is none")

            use! connection = (Store.provider connectionString).OpenConnection cancellationToken
            use command = connection.CreateCommand()
            command.CommandText <- BookingData.Ddl
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            ()
        }
        :> Task
#endif

    /// `body` over a BookingData whose every change is rolled back when it
    /// ends: one connection, one transaction, never committed. For tests.
    member _.SandboxAsync(body: Func<BookingData, Task<'T>>, cancellationToken: CancellationToken) : Task<'T> =
        repo.Sandbox((fun sandboxed -> body.Invoke(BookingData(sandboxed, time))), cancellationToken)

    /// Adds equipment called `name`: Invalid, with the field, when the name
    /// is not one (EquipmentName.create) or is taken.
    member _.AddEquipmentAsync(name: string, cancellationToken: CancellationToken) : Task<Added<EquipmentView>> =
        task {
            match EquipmentName.create name with
            | Error message -> return Added(Invalid.field "name" message)
            | Ok name ->
                let equipment = Equipment.create (EquipmentId(Guid.CreateVersion7())) name
                let! result = repo.Transact((Tx.insert equipment: Tx<Equipment, unit>), cancellationToken = cancellationToken)
                return Outcome.added View.equipment result
        }

    member _.EquipmentAsync(id: Guid, cancellationToken: CancellationToken) : Task<Found<EquipmentView>> =
        task {
            let! result = repo.One(Db.byId (EquipmentId id), cancellationToken)
            return Outcome.found "equipment" View.equipment result
        }

    /// Books the equipment, if `version` is the one stored. `from` and
    /// `until` may come with any offset (a request's, a user's): they are the
    /// instants they say, kept in UTC, as Plasm stores times.
    member _.BookAsync(id: Guid, version: int, who: string, from: DateTimeOffset, until: DateTimeOffset, cancellationToken: CancellationToken) =
        changed (Db.change (EquipmentId id) version (Equipment.book (BookingId(Guid.CreateVersion7())) who (from.ToUniversalTime()) (until.ToUniversalTime()))) cancellationToken

    /// Out of service from now, as the clock says it in UTC, whatever offset
    /// the clock answers with.
    member _.TakeOutOfServiceAsync(id: Guid, version: int, reason: string, cancellationToken: CancellationToken) =
        changed (Db.change (EquipmentId id) version (Equipment.takeOutOfService reason (time.GetUtcNow().ToUniversalTime()))) cancellationToken

    member _.ReturnToServiceAsync(id: Guid, version: int, cancellationToken: CancellationToken) =
        changed (Db.change (EquipmentId id) version Equipment.returnToService) cancellationToken
