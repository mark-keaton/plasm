namespace Plasm.Specs.CSharpOutcomes

open System
open Plasm
open Plasm.CSharp

type NoteId = NoteId of Guid

/// What C# is given of a note. (Declared before Note, so that a record
/// expression with these fields is a Note.)
type NoteView = { Id: Guid; Title: string; Version: int }

[<Table "seam_notes">]
type Note =
    { Id: NoteId
      [<Unique>]
      Title: string
      [<Version>]
      Version: int }

/// Why the rules refuse a note's change, in the domain's own words.
type NoteRefusal =
    | TitleTaken of string

    member r.Message =
        match r with
        | TitleTaken title -> $"a note is already called {title}"

module Notes =
    let model = Model.buildOrFail [] [ typeof<Note> ]

    let byId =
        Query.prepare (fun (id: Arg<NoteId>) -> plasm { for n in tableOf<Note> do where (n.Id = id.Value) })

    /// Every note, ordered by title.
    let byTitle = plasm { for n in tableOf<Note> do orderBy n.Title }

    let view (n: Note) : NoteView =
        let (NoteId id) = n.Id
        { Id = id; Title = n.Title; Version = n.Version }

    /// The note as the caller saw it (`seen`), renamed, unless it has the title already.
    let rename (id: NoteId) (seen: int) (title: string) : Tx<Note, Stop<NoteRefusal>> =
        transaction {
            let! note = Tx.oneAsSeen (byId id, seen) |> Tx.orReject (Stop.Missing "note")

            if note.Title = title then
                return! Tx.fail (Stop.Refused(TitleTaken title))
            else
                return! Tx.update (note, { note with Title = title })
        }

    /// The rule a rename keeps: the note with its new title, unless it has it already.
    let retitle (title: string) (note: Note) : Result<Note, NoteRefusal> =
        if note.Title = title then Error(TitleTaken title) else Ok { note with Title = title }

    /// As rename, a request whose input is refused as Invalid.
    let renameAsked (id: NoteId) (seen: int) (title: string) : Tx<Note, Stop<Invalid>> =
        transaction {
            let! note = Tx.oneAsSeen (byId id, seen) |> Tx.orReject (Stop.Missing "note")

            if note.Title = title then
                return! Tx.fail (Stop.Refused(Invalid.field "title" $"is already {title}"))
            else
                return! Tx.update (note, { note with Title = title })
        }

namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.CSharp
open Plasm.Specs.CSharpOutcomes

/// Feature: A C# host is given one of the cases an outcome can have
/// (csharp-outcomes.feature). C# is played by the F# that reads what a data
/// layer would give it: the case the outcome holds (its Value), or the
/// UnavailableException it throws.
[<Binding>]
type CSharpOutcomeSteps(w: World) =

    let repo () = w.Get<Repo> "notesRepo"

    let notes () =
        if w.Has "notes" then w.Get<Map<string, Note>> "notes" else Map.empty

    let remember (n: Note) = w.Set("notes", (notes ()).Add(n.Title, n))

    let insert (title: string) =
        task {
            let! r = repo().Transact(Tx.insert { Id = NoteId(Guid.CreateVersion7()); Title = title; Version = 1 }: Tx<_, unit>)
            remember (ok r)
        }

    /// The note C# names by its title: the one stored, or one never stored.
    let note (title: string) =
        match (notes ()).TryFind title with
        | Some n -> n
        | None -> { Id = NoteId(Guid.CreateVersion7()); Title = title; Version = 1 }

    /// What C# is given: the case, or the exception thrown.
    let given (outcome: unit -> Threading.Tasks.Task<obj>) =
        task {
            try
                let! value = outcome ()
                w.Set("given", value)
            with :? UnavailableException as e ->
                w.Set("given", box e)
        }
        |> unit

    let given' () = w.Get<obj> "given"

    /// The note renamed, and what C# is given of it, as `given` makes it.
    let renamedAs (given: ChangeResult<Note, NoteRefusal> -> obj) (n: Note) (title: string) =
        task {
            let! r = repo().Transact(Notes.rename n.Id n.Version title)

            match r with
            | Ok stored -> remember stored
            | Error _ -> ()

            return given (Outcome.changed r)
        }

    let renamed = renamedAs (fun c -> c.Value)

    /// The note renamed with repo.Change: one call, the query, the version seen, the rule.
    let changedWith (n: Note) (title: string) =
        task {
            let! changed = repo().Change("note", Notes.byId n.Id, n.Version, Notes.retitle title)

            match changed.Value with
            | :? Note as stored -> remember stored
            | _ -> ()

            return changed.Value
        }

    member private _.Case<'T>() : 'T =
        match given' () with
        | :? 'T as t -> t
        | other -> failwith $"C# is given {other.GetType().Name}: %A{other}"

    [<StepDefinition("notes, each with a unique title and a version")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec("drop table if exists seam_notes; " + Repo(Notes.model, w.Db.Provider).Ddl())
            w.Set("notesRepo", w.Db.RepoWith(model = Notes.model))
        }
        |> unit

    [<StepDefinition("^the note \"(.*)\"$")>]
    member _.Note(title: string) = insert title |> unit

    [<StepDefinition("^the notes \"(.*)\" and \"(.*)\"$")>]
    member _.TwoNotes(a: string, b: string) =
        task {
            do! insert a
            do! insert b
        }
        |> unit

    [<StepDefinition("^the notes table is gone$")>]
    member _.Gone() =
        task {
            let! _ = w.Db.Exec "drop table seam_notes"
            ()
        }
        |> unit

    [<StepDefinition("^someone else renamed \"(.*)\" to \"(.*)\"$")>]
    member _.SomeoneElse(from: string, title: string) =
        task {
            let first = note from
            w.Set("firstCopy", first)
            let! r = repo().Transact(Notes.rename first.Id first.Version title)
            remember (ok r)
        }
        |> unit

    [<StepDefinition("^C# reads the note \"(.*)\"$")>]
    member _.Read(title: string) =
        given (fun () ->
            task {
                let! r = repo().One(Notes.byId (note title).Id)
                return (Outcome.found "note" r).Value
            })

    [<StepDefinition("^C# reads the note \"(.*)\", made a view with FindResult\\.map$")>]
    member _.ReadView(title: string) =
        given (fun () ->
            task {
                let! r = repo().One(Notes.byId (note title).Id)
                return (Outcome.found "note" r |> FindResult.map Notes.view).Value
            })

    [<StepDefinition("^C# reads a note that is not there, made a view with FindResult\\.map$")>]
    member _.ReadNoneView() =
        given (fun () ->
            task {
                let! r = repo().One(Notes.byId (NoteId(Guid.CreateVersion7())))
                return (Outcome.found "note" r |> FindResult.map Notes.view).Value
            })

    [<StepDefinition("^C# reads a note that is not there$")>]
    member _.ReadNone() =
        given (fun () ->
            task {
                let! r = repo().One(Notes.byId (NoteId(Guid.CreateVersion7())))
                return (Outcome.found "note" r).Value
            })

    [<StepDefinition("^C# reads every note, ordered by title$")>]
    member _.ReadAll() =
        given (fun () ->
            task {
                let! r = repo().All Notes.byTitle
                return box (Outcome.all "notes" r)
            })

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", which the rules refuse$")>]
    member _.RenameRefused(from: string, title: string) = given (fun () -> renamed (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\" from the copy it read first$")>]
    member _.RenameStale(_from: string, title: string) = given (fun () -> renamed (w.Get<Note> "firstCopy") title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\" with repo\\.Change$")>]
    member _.ChangeWith(from: string, title: string) = given (fun () -> changedWith (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\" with repo\\.Change, which the rule refuses$")>]
    member _.ChangeRefused(from: string, title: string) = given (fun () -> changedWith (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\" with repo\\.Change, from the copy it read first$")>]
    member _.ChangeStale(_from: string, title: string) = given (fun () -> changedWith (w.Get<Note> "firstCopy") title)

    [<StepDefinition("^C# renames a note that is not there with repo\\.Change$")>]
    member _.ChangeNone() = given (fun () -> changedWith (note "not there") "anything")

    [<StepDefinition("^the note \"(.*)\" is still at version (\\d+)$")>]
    member _.StillAt(title: string, version: int) =
        task {
            let! r = repo().One(Notes.byId (note title).Id)
            equal (Some version) (ok r |> Option.map _.Version) "the stored version"
        }
        |> unit

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", made a view with ChangeResult\\.map$")>]
    member _.RenameView(from: string, title: string) =
        given (fun () -> renamedAs (fun c -> (c |> ChangeResult.map Notes.view).Value) (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", its refusal made Refused with ChangeResult\\.refused$")>]
    member _.RenameRefusedAs(from: string, title: string) =
        let refused (r: NoteRefusal) : Refused =
            match r with
            | TitleTaken _ -> { Code = "TitleTaken"; Message = r.Message }

        given (fun () -> renamedAs (fun c -> (c |> ChangeResult.refused refused).Value) (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"([^\"]*)\"$")>]
    member _.Rename(from: string, title: string) = given (fun () -> renamed (note from) title)

    [<StepDefinition("^C# renames a note that is not there$")>]
    member _.RenameNone() = given (fun () -> renamed (note "not there") "anything")

    [<StepDefinition("^C# renames a note that is not there, as a request answered$")>]
    member _.AnsweredNone() =
        given (fun () ->
            task {
                let n = note "not there"
                let! r = repo().Transact(Notes.renameAsked n.Id n.Version "anything")
                return (Outcome.answered r).Value
            })

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", as a request answered$")>]
    member _.Answered(from: string, title: string) =
        given (fun () ->
            task {
                let n = note from
                let! r = repo().Transact(Notes.renameAsked n.Id n.Version title)
                return (Outcome.answered r).Value
            })

    [<StepDefinition("^C# adds the note \"(.*)\"$")>]
    member _.Add(title: string) =
        given (fun () ->
            task {
                let! r = repo().Transact(Tx.insert { Id = NoteId(Guid.CreateVersion7()); Title = title; Version = 1 }: Tx<_, unit>)
                return (Outcome.added r).Value
            })

    [<StepDefinition("^C# adds the note \"(.*)\", made a view with AddResult\\.map$")>]
    member _.AddView(title: string) =
        given (fun () ->
            task {
                let! r = repo().Transact(Tx.insert { Id = NoteId(Guid.CreateVersion7()); Title = title; Version = 1 }: Tx<_, unit>)
                return (Outcome.added r |> AddResult.map Notes.view).Value
            })

    [<StepDefinition("^C# adds a note whose title the domain refuses$")>]
    member _.AddRefused() =
        // The domain's check, before the database: an empty title is no title.
        given (fun () -> task { return (AddResult<Note>(Invalid.field "title" "is empty")).Value })

    [<StepDefinition("^the keyed Issue (.*)#(\\d+) \"(.*)\", changed since it was read$")>]
    member _.KeyedIssue(repoName: string, number: int, title: string) =
        task {
            let model = CompositeKeys.Schemas.model

            let! _ =
                match w.Db.Kind with
                | Plasm.TestDomain.Database.PostgreSQL -> w.Db.Exec CompositeKeys.Schemas.tables
                | Plasm.TestDomain.Database.SQLite -> w.Db.Exec("drop table if exists keyed_issues; " + Repo(model, w.Db.Provider).Ddl())

            let keyed = w.Db.RepoWith(model = model)
            w.Set("keyedRepo", keyed)
            let first: CompositeKeys.Issue = { Id = { Repo = repoName; Number = number }; Title = title; Version = 1 }
            let! inserted = keyed.Transact(Tx.insert first: Tx<_, unit>)
            let first = ok inserted
            let! _ = keyed.Transact(Tx.update (first, { first with Title = title + " (again)" }): Tx<_, unit>)
            w.Set("firstIssue", first)
        }
        |> unit

    [<StepDefinition("^C# retitles (.*)#(\\d+) from the copy it read first$")>]
    member _.Retitle(_repo: string, _number: int) =
        given (fun () ->
            task {
                let first = w.Get<CompositeKeys.Issue> "firstIssue"
                let! r = w.Get<Repo>("keyedRepo").Transact(Tx.update (first, { first with Title = "Retitled" }): Tx<_, Stop<NoteRefusal>>)
                return (Outcome.changed r |> ChangeResult.map (fun (i: CompositeKeys.Issue) -> i.Title)).Value
            })

    [<StepDefinition("^it is given the NoteView of \"(.*)\"$")>]
    member this.View(title: string) =
        let v = this.Case<NoteView>()
        equal title v.Title "the title"
        let (NoteId id) = (note title).Id
        equal id v.Id "the id"

    [<StepDefinition("^it is given the NoteView of \"(.*)\", at version (\\d+)$")>]
    member this.ViewAt(title: string, version: int) =
        let v = this.Case<NoteView>()
        equal (title, version) (v.Title, v.Version) "the title and version"

    [<StepDefinition("^it is given the Note \"(.*)\"$")>]
    member this.Record(title: string) =
        let n = this.Case<Note>()
        equal ((note title).Id, title) (n.Id, n.Title) "the id and title"

    [<StepDefinition("^it is given the Note \"(.*)\", at version (\\d+)$")>]
    member this.RecordAt(title: string, version: int) =
        let n = this.Case<Note>()
        equal (title, version) (n.Title, n.Version) "the title and version"

    [<StepDefinition("^it is given the Notes \"(.*)\" and \"(.*)\", in that order$")>]
    member this.Records(a: string, b: string) =
        let ns = this.Case<Note[]>()
        equal [| (note a).Id, a; (note b).Id, b |] (ns |> Array.map (fun n -> n.Id, n.Title)) "the ids and titles, in order"

    [<StepDefinition("^it is given no Note$")>]
    member this.NoRecords() = equal 0 (this.Case<Note[]>().Length) "the notes"

    [<StepDefinition("^it is given the refusal TitleTaken \"(.*)\"$")>]
    member this.Refusal(title: string) = equal (TitleTaken title) (this.Case<NoteRefusal>()) "the refusal"

    [<StepDefinition("^it is given Missing, saying \"(.*)\"$")>]
    member this.Missing(what: string) = equal what (this.Case<Missing>().What) "what is missing"

    [<StepDefinition("^it is given Refused, with the code \"(.*)\" and the rules' words$")>]
    member this.Refused(code: string) =
        let r = this.Case<Refused>()
        equal code r.Code "the code"
        equal "a note is already called Groceries" r.Message "the rules' words"

    [<StepDefinition("^it is given Stale, naming Note and the note's id$")>]
    member this.StaleNote() =
        let s = this.Case<Stale>()
        let (NoteId id) = (w.Get<Note> "firstCopy").Id
        equal ("Note", [| id.ToString "D" |]) (s.What, s.Key) "what is stale, and its key"

    [<StepDefinition("^it is given Stale, naming Issue and the parts \"(.*)\" and \"(.*)\"$")>]
    member this.StaleIssue(a: string, b: string) =
        let s = this.Case<Stale>()
        equal ("Issue", [| a; b |]) (s.What, s.Key) "what is stale, and its key's parts"

    [<StepDefinition("^it is given Invalid, with the field \"(.*)\" and \"(.*)\"$")>]
    member this.Invalid(field: string, message: string) =
        let i = this.Case<Invalid>()
        equal [| { Field = field; Message = message } |] i.Problems "the problems"

    [<StepDefinition("^UnavailableException is thrown, naming the read, with the provider's exception inside$")>]
    member this.UnavailableRead() =
        let e = this.Case<UnavailableException>()
        check (e.Message.StartsWith "reading note: ") e.Message
        check (not (isNull e.InnerException)) "no provider exception inside"

    [<StepDefinition("^UnavailableException is thrown, naming the read of \"(.*)\", with the provider's exception inside$")>]
    member this.UnavailableReadAll(what: string) =
        let e = this.Case<UnavailableException>()
        check (e.Message.StartsWith $"reading {what}: ") e.Message
        check (not (isNull e.InnerException)) "no provider exception inside"

    [<StepDefinition("^UnavailableException is thrown, naming the step that failed$")>]
    member this.UnavailableStep() =
        let e = this.Case<UnavailableException>()
        check (e.Message.Contains " failed: ") e.Message
