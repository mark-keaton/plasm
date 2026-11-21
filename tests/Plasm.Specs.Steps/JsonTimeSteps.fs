/// A document's times are UTC, as a column's are (json-columns.feature).
namespace Plasm.Specs.JsonTimes

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs

type LogbookId = LogbookId of int64

type Note = { Text: string; Seen: DateTime option }
type Entry = { At: DateTimeOffset; Note: Note option }

[<Table "logbooks">]
type Logbook =
    { Id: LogbookId
      Name: string
      [<Json>]
      Entries: Entry list
      [<Json>]
      Stamps: DateTime list }

module Logbooks =
    let model = Model.buildOrFail [] [ typeof<Logbook> ]
    let eight = DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc)
    let atEight = DateTimeOffset(eight)

    let utc =
        { Id = LogbookId 0L
          Name = "utc"
          Entries = [ { At = atEight; Note = Some { Text = "seen"; Seen = Some eight } } ]
          Stamps = [ eight ] }

[<Binding>]
type JsonTimeSteps(w: World) =

    let repo () = w.Get<Repo> "logbookRepo"

    let write (book: Logbook) =
        task {
            let! r = repo().Transact(Tx.insert book: Tx<_, unit>)

            w.Set(
                "logbookOutcome",
                match r with
                | Ok _ -> "stored"
                | Error e ->
                    match e.Failure with
                    | TxFailure.InvalidChanges(entity, errors) -> "InvalidChanges " + String.concat "; " [ for er in errors -> $"{entity}.{er.Field}: {er.Message}" ]
                    | other -> sprintf "%A" other
            )
        }
        :> Task

    [<StepDefinition("a table of logbooks, each with its entries and stamps as documents")>]
    member _.Table() =
        task {
            let repo = w.Db.RepoWith(model = Logbooks.model)
            let! _ = w.Db.Exec("drop table if exists logbooks; " + repo.Ddl())
            w.Set("logbookRepo", repo)
        }
        :> Task

    [<StepDefinition("^I write a logbook whose (first entry's time is at \\+01:00|first entry's note was seen at 08:00 with Kind Unspecified|second stamp is 08:00 with Kind Local)$")>]
    member _.WriteOff(what: string) =
        let book = Logbooks.utc

        let book =
            match what with
            | "first entry's time is at +01:00" -> { book with Entries = [ { book.Entries.Head with At = Logbooks.atEight.ToOffset(TimeSpan.FromHours 1.0) } ] }
            | "first entry's note was seen at 08:00 with Kind Unspecified" ->
                { book with Entries = [ { book.Entries.Head with Note = Some { Text = "seen"; Seen = Some(DateTime(2026, 11, 21, 8, 0, 0)) } } ] }
            | _ -> { book with Stamps = [ Logbooks.eight; DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Local) ] }

        write book

    [<StepDefinition("^the write is refused as an invalid change of (Logbook\\.\\w+), at (\\S+) in the document, saying \"(.*)\"$")>]
    member _.Refused(field: string, path: string, text: string) =
        let outcome = w.Get<string> "logbookOutcome"
        check (outcome.StartsWith "InvalidChanges" && outcome.Contains $"{field}: at {path} in the document" && outcome.Contains text) outcome

    [<StepDefinition("I write a logbook whose times are all UTC")>]
    member _.WriteUtc() = write Logbooks.utc

    [<StepDefinition("it is read back with each time at the offset zero and of Kind Utc")>]
    member _.ReadBack() =
        task {
            let! r = repo().All(Query.from (tableOf<Logbook>))
            let book = List.exactlyOne (ok r)
            let entry = book.Entries.Head
            check (entry.At = Logbooks.atEight && entry.At.Offset = TimeSpan.Zero) $"At {entry.At:o}"
            let seen = entry.Note.Value.Seen.Value
            check (seen = Logbooks.eight && seen.Kind = DateTimeKind.Utc) $"Seen {seen:o} {seen.Kind}"
            check (book.Stamps = [ Logbooks.eight ] && book.Stamps.Head.Kind = DateTimeKind.Utc) $"Stamps %A{book.Stamps}"
        }
        :> Task

    [<StepDefinition("^I read the logbooks (with an entry at 08:00 at \\+01:00|stamped 08:00 with Kind Unspecified)$")>]
    member _.ReadBy(how: string) =
        task {
            let! r =
                if how.StartsWith "with an entry" then
                    let at = Logbooks.atEight.ToOffset(TimeSpan.FromHours 1.0)
                    repo().All(plasm { for l in tableOf<Logbook> do where (l.Entries |> List.exists (fun e -> e.At = at)) })
                else
                    let t = DateTime(2026, 11, 21, 8, 0, 0)
                    repo().All(plasm { for l in tableOf<Logbook> do where (List.contains t l.Stamps) })

            w.Set(
                "logbookRead",
                match r with
                | Ok xs -> $"read {xs.Length}"
                | Error(DbError.Translation t) -> "Translation " + string t
                | Error e -> string e
            )
        }
        :> Task

    [<StepDefinition("^the read of the logbooks is refused as a query that cannot be translated, saying \"(.*)\"$")>]
    member _.ReadRefused(text: string) =
        let outcome = w.Get<string> "logbookRead"
        check (outcome.StartsWith "Translation" && outcome.Contains text) outcome
