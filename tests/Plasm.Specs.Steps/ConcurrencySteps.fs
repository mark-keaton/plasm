namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain

/// A record without a version field, for concurrency.feature.
module Notes =

    type NoteId = NoteId of int64

    type Note =
        { Id: NoteId
          Text: string
          Author: string option }

    let notes = schema<Note> "notes" { identityKey (fun n -> n.Id) }

    let model = Model.buildSchemasOrFail [] [ notes ]

    let table =
        "drop table if exists notes;
         create table notes (id bigint generated always as identity primary key, text text not null, author text);"

open Notes

/// Feature: Changes are checked against what was read
[<Binding>]
type ConcurrencySteps(w: World) =

    let repo () = w.Get<Repo> "noteRepo"
    let read () = w.Get<Note> "note"

    let saved (r: Result<'T, TransactionError<unit>>) =
        w.Set("noteWrite", r |> Result.map ignore |> Result.mapError (fun e -> string e, (match e.Failure with TxFailure.Conflict _ -> true | _ -> false)))

    let readNote (author: string option) =
        task {
            let! inserted = repo().Transact(Tx.insert { Id = NoteId 0L; Text = "first"; Author = author }: Tx<_, unit>)
            let! stored = repo().One(plasm { for n in notes do where (n.Id = (ok inserted).Id) })
            w.Set("note", (ok stored).Value)
        }

    [<StepDefinition("a notes table")>]
    member _.Table() =
        task {
            let! _ = w.Db.Exec Notes.table
            w.Set("noteRepo", w.Db.RepoWith(model = Notes.model))
        }
        |> unit

    [<StepDefinition("a Note read from the database")>]
    member _.ReadNote() = readNote (Some "ada") |> unit

    [<StepDefinition("a Note without an author read from the database")>]
    member _.ReadNoteWithoutAuthor() = readNote None |> unit

    [<StepDefinition("another writer changes that Note's text")>]
    member _.OtherText() = w.Db.Exec "update notes set text = 'theirs'" |> unit

    [<StepDefinition("another writer gives that Note an author")>]
    member _.OtherAuthor() = w.Db.Exec "update notes set author = 'bob'" |> unit

    [<StepDefinition("I change the text of the Note I read")>]
    member _.Change() =
        task {
            w.MarkCommands()
            let! r = repo().Transact(read () |> edit (fun n -> { n with Text = "mine" }))
            saved r
        }
        |> unit

    [<StepDefinition("I delete the Note I read")>]
    member _.Delete() =
        task {
            let! r = repo().Transact(Tx.delete (read ()): Tx<_, unit>)
            saved r
        }
        |> unit

    [<StepDefinition("I change the Note's text by its key alone")>]
    member _.ChangeByKey() =
        task {
            // A change by key alone is a bulk update: it names rows, not a record read.
            let id = (read ()).Id
            let! r = repo().Transact(Tx.execute (Bulk.update (plasm { for n in notes do where (n.Id = id) }, fun n -> { n with Text = "mine" })): Tx<int, unit>)

            match r with
            | Ok 1 -> w.Set("noteWrite", (Ok() : Result<unit, string * bool>))
            | other -> w.Set("noteWrite", (Error(string other, false) : Result<unit, string * bool>))
        }
        |> unit

    [<StepDefinition("the change is saved")>]
    member _.Saved() =
        match w.Get<Result<unit, string * bool>> "noteWrite" with
        | Ok() -> ()
        | Error(e, _) -> failwith $"the change failed: {e}"

    [<StepDefinition("^the (?:change|delete) fails as a conflict$")>]
    member _.Conflict() =
        match w.Get<Result<unit, string * bool>> "noteWrite" with
        | Error(_, true) -> ()
        | other -> failwithf "expected a conflict, got %A" other

    [<StepDefinition("the other writer's text is kept")>]
    member _.TheirsKept() =
        task {
            let! text = w.Db.Scalar<string>("select text from notes")
            equal "theirs" text "text"
        }
        |> unit

    [<StepDefinition("one command was sent, comparing every column with what was read")>]
    member _.OneCommand() =
        let commands = w.CommandsSinceMark
        equal 1 commands.Length "commands"
        let sql = commands.Head.Sql
        check (sql.StartsWith "UPDATE" && sql.Contains "\"text\" IS NOT DISTINCT FROM" && sql.Contains "\"author\" IS NOT DISTINCT FROM") sql

    [<StepDefinition("another writer changes its total without changing its version")>]
    member _.OtherTotal() = w.Db.Exec "update orders set total = total + 1" |> unit

    [<StepDefinition("I change the status of the Order I read")>]
    member _.ChangeOrder() =
        task {
            let order = w.Get<Order> "order"
            let! r = w.Repo.Transact(order |> edit (fun o -> { o with Status = Published }))
            saved r
        }
        |> unit
