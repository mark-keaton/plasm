namespace Plasm.Specs.CSharpOutcomes

open System
open Plasm

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

/// Why a note's transaction stops: the project's own reasons, which it maps
/// to the case each means.
type NoteStop =
    | Gone
    | TitleTaken of string

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
    let rename (id: NoteId) (seen: int) (title: string) : Tx<Note, NoteStop> =
        transaction {
            let! note = Tx.oneAsSeen (byId id, seen) |> Tx.orReject Gone

            if note.Title = title then
                return! Tx.fail (TitleTaken title)
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

    let rejected (stop: NoteStop) : Changed<NoteView> =
        match stop with
        | Gone -> Changed { What = "note" }
        | TitleTaken title -> Changed { Code = "TitleTaken"; Message = $"a note is already called {title}" }

    let renamed (n: Note) (title: string) =
        task {
            let! r = repo().Transact(Notes.rename n.Id n.Version title)

            match r with
            | Ok stored -> remember stored
            | Error _ -> ()

            return (Outcome.changed Notes.view rejected r).Value
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
                return (Outcome.found "note" Notes.view r).Value
            })

    [<StepDefinition("^C# reads a note that is not there$")>]
    member _.ReadNone() =
        given (fun () ->
            task {
                let! r = repo().One(Notes.byId (NoteId(Guid.CreateVersion7())))
                return (Outcome.found "note" Notes.view r).Value
            })

    [<StepDefinition("^C# reads every note, ordered by title$")>]
    member _.ReadAll() =
        given (fun () ->
            task {
                let! r = repo().All Notes.byTitle
                return box (Outcome.all "notes" Notes.view r)
            })

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", which the rules refuse$")>]
    member _.RenameRefused(from: string, title: string) = given (fun () -> renamed (note from) title)

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\" from the copy it read first$")>]
    member _.RenameStale(_from: string, title: string) = given (fun () -> renamed (w.Get<Note> "firstCopy") title)

    [<StepDefinition("^C# renames \"(.*)\" to \"([^\"]*)\"$")>]
    member _.Rename(from: string, title: string) = given (fun () -> renamed (note from) title)

    [<StepDefinition("^C# renames a note that is not there$")>]
    member _.RenameNone() = given (fun () -> renamed (note "not there") "anything")

    [<StepDefinition("^C# renames a note that is not there, as a request answered$")>]
    member _.AnsweredNone() =
        given (fun () ->
            task {
                let n = note "not there"
                let! r = repo().Transact(Notes.rename n.Id n.Version "anything")

                let rejected =
                    function
                    | Gone -> Answered { What = "note" }
                    | TitleTaken title -> Answered(Invalid.field "title" $"is already {title}")

                return (Outcome.answered Notes.view rejected r).Value
            })

    [<StepDefinition("^C# renames \"(.*)\" to \"(.*)\", as a request answered$")>]
    member _.Answered(from: string, title: string) =
        given (fun () ->
            task {
                let n = note from
                let! r = repo().Transact(Notes.rename n.Id n.Version title)
                let rejected = function Gone -> Answered { What = "note" } | TitleTaken t -> Answered(Invalid.field "title" $"is already {t}")
                return (Outcome.answered Notes.view rejected r).Value
            })

    [<StepDefinition("^C# adds the note \"(.*)\"$")>]
    member _.Add(title: string) =
        given (fun () ->
            task {
                let! r = repo().Transact(Tx.insert { Id = NoteId(Guid.CreateVersion7()); Title = title; Version = 1 }: Tx<_, unit>)
                return (Outcome.added Notes.view r).Value
            })

    [<StepDefinition("^C# adds a note whose title the domain refuses$")>]
    member _.AddRefused() =
        // The domain's check, before the database: an empty title is no title.
        given (fun () -> task { return (Added<NoteView>(Invalid.field "title" "is empty")).Value })

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
                let! r = w.Get<Repo>("keyedRepo").Transact(Tx.update (first, { first with Title = "Retitled" }): Tx<_, unit>)
                let rejected () : Changed<string> = failwith "nothing rejects a retitle"
                return (Outcome.changed (fun (i: CompositeKeys.Issue) -> i.Title) rejected r).Value
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

    [<StepDefinition("^it is given the NoteViews of \"(.*)\" and \"(.*)\", in that order$")>]
    member this.Views(a: string, b: string) =
        let vs = this.Case<NoteView[]>()
        equal [| a; b |] (vs |> Array.map _.Title) "the titles, in order"
        let id t = let (NoteId id) = (note t).Id in id
        equal [| id a; id b |] (vs |> Array.map _.Id) "the ids"

    [<StepDefinition("^it is given no NoteView$")>]
    member this.NoViews() = equal 0 (this.Case<NoteView[]>().Length) "the views"

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
