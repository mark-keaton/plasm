/// What C# is given, from Plasm.CSharp's package: notes in a SQLite file,
/// read and changed so that each outcome's cases come up, a stale copy among
/// them (its key read through Plasm's shapes, natively too).
namespace Seam.Data

open System
open System.Runtime.CompilerServices
open System.Text.Json.Serialization
open System.Threading
open Plasm
open Plasm.Sqlite
open Plasm.CSharp

type NoteId = NoteId of Guid

[<Table "notes">]
type Note =
    { Id: NoteId
      [<Unique>]
      Title: string
      [<Version>]
      Version: int }

/// A key of two parts, the first of which may hold a "/".
type FiledKey = { Shelf: string; Number: int }

[<Table "filed">]
type Filed =
    { Id: FiledKey
      Title: string
      [<Version>]
      Version: int }

[<Sealed>]
type Bookable() =
    [<JsonPropertyOrder(-1)>]
    member _.Kind = "bookable"

type Withdrawn =
    { Reason: string }

    [<JsonPropertyOrder(-1)>]
    member _.Kind = "withdrawn"

type ConditionByKind() =
    inherit ByKind(typeof<ConditionView>, [ "bookable", typeof<Bookable>; "withdrawn", typeof<Withdrawn> ])

and [<Struct; Union; JsonUnion(TypeClassifier = typeof<ConditionByKind>)>] ConditionView =
    val private value: obj
    new(bookable: Bookable) = { value = bookable :> obj }
    new(withdrawn: Withdrawn) = { value = withdrawn :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

type NoteView =
    { Id: Guid
      Title: string
      Condition: ConditionView }

module private Db =
    let model = Model.buildOrFail [] [ typeof<Note>; typeof<Filed> ]

    let byId =
        Query.prepare (fun (id: Arg<NoteId>) -> plasm { for n in tableOf<Note> do where (n.Id = id.Value) })

    let view (n: Note) : NoteView =
        let (NoteId id) = n.Id
        { Id = id; Title = n.Title; Condition = ConditionView { Reason = "kept" } }

    let first = Guid.Parse "01900000-0000-7000-8000-000000000001"

/// What the rules refuse of a note: nothing, here; a change still names it.
type NoteRefusal =
    | NoRule

    member r.Message = "no rule refuses a note"

[<Sealed>]
type Notes private (repo: Repo) =
    let run (tx: Tx<'T, Stop<NoteRefusal>>) = repo.Transact(tx).Result

    let noRefusal (_: NoteRefusal) : Refused = invalidOp "nothing here is refused"

    /// The notes in the SQLite file `path`, made from the model's DDL.
    static member Open(path: string) =
        let provider = Sqlite.connect ("Data Source=" + path)
        let repo = Repo.Start(Db.model, provider, typeof<Notes>.Assembly, RepoOptions.Unchecked).Result
        use conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
        use cmd = conn.CreateCommand()
        cmd.CommandText <- repo.Ddl()
        cmd.ExecuteNonQueryAsync().Result |> ignore
        Notes(repo)

    member _.Add(title: string) : AddResult<NoteView> =
        let id = if title = "Groceries" then Db.first else Guid.Parse "01900000-0000-7000-8000-000000000002"
        Outcome.added (run (Tx.insert ({ Id = NoteId id; Title = title; Version = 1 }: Note))) |> AddResult.map Db.view

    member _.Read(id: Guid) : FindResult<NoteView> =
        Outcome.found (repo.One(Db.byId (NoteId id)).Result) |> FindResult.map Db.view

    /// A note renamed from a copy older than the stored one: Stale.
    member _.RenameStale() : ChangeResult<NoteView> =
        let note = (repo.One(Db.byId (NoteId Db.first)).Result |> Result.toOption |> Option.flatten).Value
        run (Tx.update (note, { note with Title = "Shopping" })) |> ignore
        Outcome.changed (run (Tx.update (note, { note with Title = "Errands" }))) |> ChangeResult.map Db.view |> ChangeResult.refused noRefusal

    /// A filed note, keyed by a shelf holding "/", retitled from an old copy: Stale, by its parts.
    member _.RetitleFiledStale() : ChangeResult<NoteView> =
        let filed: Filed = { Id = { Shelf = "a/b"; Number = 7 }; Title = "Crash"; Version = 1 }
        let stored = match run (Tx.insert filed) with Ok f -> f | Error e -> failwith e.Display
        run (Tx.update (stored, { stored with Title = "Crash (again)" })) |> ignore
        let view (f: Filed) : NoteView = { Id = Guid.Empty; Title = f.Title; Condition = ConditionView(Bookable()) }
        Outcome.changed (run (Tx.update (stored, { stored with Title = "Retitled" }))) |> ChangeResult.map view |> ChangeResult.refused noRefusal
