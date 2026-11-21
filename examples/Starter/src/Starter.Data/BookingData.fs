/// What C# calls: one type, BookingData. Its methods take plain arguments and
/// return a Task of plain types; an outcome that can go more than one way is a
/// union C# switches over, and the compiler names a case it does not handle.
/// Nothing returned is null, an F# option or an F# Result; a database that
/// cannot be reached throws BookingUnavailableException.
namespace Starter.Data

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Npgsql
open Plasm
open Plasm.Postgres
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
    inherit JsonTypeClassifierFactory()

    override _.CanClassify(context) = context.DeclaringType = typeof<ConditionView>

    override _.CreateJsonClassifier(_, _) =
        JsonTypeClassifier(fun reader ->
            let mutable r = reader
            let mutable found: Type | null = null

            if r.TokenType = JsonTokenType.StartObject then
                let depth = r.CurrentDepth

                while isNull found && r.Read() && r.CurrentDepth > depth do
                    if r.TokenType = JsonTokenType.PropertyName && r.ValueTextEquals "kind" then
                        r.Read() |> ignore

                        let kind: Type | null =
                            match r.GetString() with
                            | "bookable" -> typeof<Bookable>
                            | "withdrawn" -> typeof<Withdrawn>
                            | _ -> null

                        found <- kind
                    elif r.TokenType = JsonTokenType.StartObject || r.TokenType = JsonTokenType.StartArray then
                        r.Skip()

            found)

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

/// What is missing.
type Missing = { What: string }

/// The rules refused: the Refusal case's name, and why in words.
type Refused = { Code: string; Message: string }

/// The caller's copy is older than the stored one.
type Stale = { Equipment: Guid }

type FieldProblem = { Field: string; Message: string }

/// Input the database refused, each field with why.
type Invalid = { Problems: FieldProblem[] }

[<Struct; Union>]
type Found<'T when 'T: not null> =
    val private value: obj
    new(found: 'T) = { value = found :> obj }
    new(missing: Missing) = { value = missing :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// Adding equipment: the equipment, or the input that was not valid.
[<Struct; Union>]
type AddOutcome =
    val private value: obj
    new(equipment: EquipmentView) = { value = equipment :> obj }
    new(invalid: Invalid) = { value = invalid :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// Changing equipment: the equipment as stored, or why not.
[<Struct; Union>]
type ChangeOutcome =
    val private value: obj
    new(equipment: EquipmentView) = { value = equipment :> obj }
    new(missing: Missing) = { value = missing :> obj }
    new(refused: Refused) = { value = refused :> obj }
    new(stale: Stale) = { value = stale :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

type BookingUnavailableException(message: string, inner: exn | null) =
    inherit Exception(message, inner)

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
        lazy (Repo.Start(model, Postgres.connect "Host=localhost", typeof<Stop>.Assembly, RepoOptions.Unchecked).Result.Ddl())

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

    let problem (e: FieldError) =
        let field = if e.Field = "" then "" else string (Char.ToLowerInvariant e.Field.[0]) + e.Field.Substring 1

        match e.Kind with
        | FieldErrorKind.UniqueConstraint _ -> { Field = field; Message = "is already taken" }
        | _ -> { Field = field; Message = e.Message }

[<Sealed>]
type BookingData private (repo: Repo, time: TimeProvider) =

    let unavailable (what: string) (error: DbError) =
        match error with
        | DbError.Database failure -> raise (BookingUnavailableException($"{what}: {failure.Message}", failure.Exception))
        | other -> raise (BookingUnavailableException($"{what}: {other}", null))

    let changed (tx: Tx<Equipment, Db.Stop>) (ct: CancellationToken) : Task<ChangeOutcome> =
        task {
            match! repo.Transact(tx, cancellationToken = ct) with
            | Ok stored -> return ChangeOutcome(View.equipment stored)
            | Error { Failure = TxFailure.Rejected Db.Gone } -> return ChangeOutcome { What = "equipment" }
            | Error { Failure = TxFailure.Rejected(Db.Ruled refusal) } -> return ChangeOutcome(View.refused refusal)
            // The conflict names the stale record by its key.
            | Error { Failure = TxFailure.Conflict(_, (:? EquipmentId as EquipmentId stale)) } -> return ChangeOutcome { Equipment = stale }
            | Error { Failure = TxFailure.Failed error } -> return unavailable "a change" error
            | Error other -> return raise (BookingUnavailableException(string other, null))
        }

    static member private Open(dataSource: NpgsqlDataSource, onSql: Action<string> option, time: TimeProvider, cancellationToken: CancellationToken) =
        task {
            let options =
                match onSql with
                | None -> RepoOptions.Default
                | Some onSql -> { RepoOptions.Default with OnCommand = Some(fun e -> onSql.Invoke e.Sql) }

            let! repo = Repo.Start(Db.model, PostgresProvider dataSource, typeof<BookingData>.Assembly, options, cancellationToken)
            return BookingData(repo, time)
        }

    /// Starts Plasm's repository over `dataSource`. It checks every query and
    /// the tables first, so one that would fail throws here, before anything
    /// is served. Every change takes its time from `time`, the application's
    /// one clock: TimeProvider.System, or a test's own.
    static member OpenAsync(dataSource: NpgsqlDataSource, time: TimeProvider, cancellationToken: CancellationToken) : Task<BookingData> =
        BookingData.Open(dataSource, None, time, cancellationToken)

    /// As OpenAsync, `onSql` given the SQL of each command sent, BEGIN and
    /// COMMIT included (a log).
    static member OpenAsync(dataSource: NpgsqlDataSource, onSql: Action<string>, time: TimeProvider, cancellationToken: CancellationToken) : Task<BookingData> =
        BookingData.Open(dataSource, Some onSql, time, cancellationToken)

    /// The SQL that creates the tables.
    static member Ddl: string = Db.ddl.Value

    /// `body` over a BookingData whose every change is rolled back when it
    /// ends: one connection, one transaction, never committed. For tests.
    member _.SandboxAsync(body: Func<BookingData, Task<'T>>, cancellationToken: CancellationToken) : Task<'T> =
        repo.Sandbox((fun sandboxed -> body.Invoke(BookingData(sandboxed, time))), cancellationToken)

    /// Adds equipment called `name`: Invalid, with the field, when the name
    /// is not one (EquipmentName.create) or is taken.
    member _.AddEquipmentAsync(name: string, cancellationToken: CancellationToken) : Task<AddOutcome> =
        task {
            match EquipmentName.create name with
            | Error message -> return AddOutcome { Problems = [| { Field = "name"; Message = message } |] }
            | Ok name ->
                let equipment = Equipment.create (EquipmentId(Guid.CreateVersion7())) name

                match! repo.Transact((Tx.insert equipment: Tx<Equipment, unit>), cancellationToken = cancellationToken) with
                | Ok stored -> return AddOutcome(View.equipment stored)
                | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> return AddOutcome { Problems = [| for e in errors -> View.problem e |] }
                | Error { Failure = TxFailure.Failed error } -> return unavailable "adding equipment" error
                | Error other -> return raise (BookingUnavailableException(string other, null))
        }

    member _.EquipmentAsync(id: Guid, cancellationToken: CancellationToken) : Task<Found<EquipmentView>> =
        task {
            match! repo.One(Db.byId (EquipmentId id), cancellationToken) with
            | Ok(Some equipment) -> return Found(View.equipment equipment)
            | Ok None -> return Found { What = "equipment" }
            | Error error -> return unavailable "reading equipment" error
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
