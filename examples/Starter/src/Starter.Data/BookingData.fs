/// What C# calls: one type, BookingData. Its members take and return the
/// domain's own types; an outcome that can go more than one way is one of
/// Plasm.CSharp's (Found, Added, Changed of the equipment and the rules'
/// Refusal), and a read of many is an array. The seam (Starter.Api's
/// Seam.cs) writes the C# the API and the tests call: a view of each record
/// and union, and a C# BookingData over this one, whose outcomes are of those
/// views, each refusal a Refused. No view, and no mapping to one, is written
/// here. A database that cannot be reached throws Plasm.CSharp's
/// UnavailableException.
namespace Starter.Data

open System
#if STARTER_SQLITE
open System.IO
#endif
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

    /// The equipment as the caller saw it (`seen`), changed as `decide` says:
    /// Stop.Missing when it is not there, Stop.Refused when the rules refuse.
    let change (id: EquipmentId) (seen: int) (decide: Equipment -> Result<Equipment, Refusal>) : Tx<Equipment, Stop<Refusal>> =
        transaction {
            let! equipment = Tx.oneAsSeen (byId id, seen) |> Tx.orReject (Stop.Missing "equipment")
            let! changed = decide equipment |> Tx.ofResult Stop.Refused
            return! Tx.update (equipment, changed)
        }

    /// The tables, as Plasm would create them: the model alone, no database.
    let ddl =
        lazy (Repo.Start(model, Store.printing (), typeof<Equipment>.Assembly, RepoOptions.Unchecked).Result.Ddl())

[<Sealed>]
type BookingData private (repo: Repo, time: TimeProvider) =

    /// The equipment as stored when `tx` commits, or why not.
    let changed (tx: Tx<Equipment, Stop<Refusal>>) (ct: CancellationToken) : Task<Changed<Equipment, Refusal>> =
        task {
            let! result = repo.Transact(tx, cancellationToken = ct)
            return Outcome.changed result
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
    member _.AddEquipmentAsync(name: string, cancellationToken: CancellationToken) : Task<Added<Equipment>> =
        task {
            match EquipmentName.create name with
            | Error message -> return Added(Invalid.field "name" message)
            | Ok name ->
                let equipment = Equipment.create (EquipmentId(Guid.CreateVersion7())) name
                let! result = repo.Transact((Tx.insert equipment: Tx<Equipment, unit>), cancellationToken = cancellationToken)
                return Outcome.added result
        }

    member _.EquipmentAsync(id: EquipmentId, cancellationToken: CancellationToken) : Task<Found<Equipment>> =
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
    member _.BookAsync(id: EquipmentId, version: int, who: string, from: DateTimeOffset, until: DateTimeOffset, cancellationToken: CancellationToken) =
        changed (Db.change id version (Equipment.book (BookingId(Guid.CreateVersion7())) who (from.ToUniversalTime()) (until.ToUniversalTime()))) cancellationToken

    /// Out of service from now, as the clock says it in UTC, whatever offset
    /// the clock answers with.
    member _.TakeOutOfServiceAsync(id: EquipmentId, version: int, reason: string, cancellationToken: CancellationToken) =
        changed (Db.change id version (Equipment.takeOutOfService reason (time.GetUtcNow().ToUniversalTime()))) cancellationToken

    member _.ReturnToServiceAsync(id: EquipmentId, version: int, cancellationToken: CancellationToken) =
        changed (Db.change id version Equipment.returnToService) cancellationToken
