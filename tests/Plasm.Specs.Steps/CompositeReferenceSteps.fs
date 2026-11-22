/// A domain of its own for composite-references.feature: a tracker whose
/// tables are keyed by several columns and refer to one another.
namespace Plasm.Specs.CompositeReferences

open Plasm

type TicketKey = { Project: string; Number: int }
type NoteId = NoteId of int64

[<Table "tickets">]
type Ticket =
    { Id: TicketKey
      Title: string
      Tags: string list
      Watchers: string list
      [<NotReference>]
      Parent: TicketKey option
      [<Version>]
      Version: int
      Notes: Note list }

and [<Table "ticket_notes">] Note =
    { Id: NoteId
      [<Column("project", "ticket_number")>]
      Ticket: TicketKey
      Body: string }

type ReleaseKey = { Project: string; Number: int }

[<Table "releases">]
type Release =
    { Id: ReleaseKey
      Name: string
      [<Version>]
      Version: int }

type StepKey = { Release: ReleaseKey; Position: int }

[<Table "release_steps">]
type Step = { Id: StepKey; Task: string }

type ShippedKey =
    { Release: ReleaseKey
      [<Column("ticket_project", "ticket_number")>]
      [<NotReference>]
      Ticket: TicketKey }

[<Table "release_tickets">]
type Shipped = { Id: ShippedKey }

type BlockerKey =
    { Ticket: TicketKey
      [<Column("blocker_project", "blocker_number")>]
      [<NotReference>]
      Blocker: TicketKey }

[<Table "ticket_blockers">]
type Blocker = { Id: BlockerKey }

module Schemas =
    let model =
        Model.buildOrFail [] [ typeof<Ticket>; typeof<Release>; typeof<Step>; typeof<Shipped>; typeof<Blocker> ]

    /// Written by hand, as an existing tracker has them; constraint names are
    /// PostgreSQL's defaults.
    let tables =
        "drop table if exists ticket_blockers, release_tickets, release_steps, releases, ticket_notes, tickets;
         create table tickets (
           project text not null,
           number integer not null,
           title text not null,
           tags text[] not null default '{}',
           watchers text[] not null default '{}',
           parent_project text,
           parent_number integer,
           version integer not null default 1,
           primary key (project, number));
         create table ticket_notes (
           id bigint generated always as identity primary key,
           project text not null,
           ticket_number integer not null,
           body text not null,
           foreign key (project, ticket_number) references tickets (project, number) on delete cascade);
         create index on ticket_notes (project, ticket_number);
         create table releases (
           project text not null,
           number integer not null,
           name text not null,
           version integer not null default 1,
           primary key (project, number));
         create table release_steps (
           project text not null,
           number integer not null,
           position integer not null,
           task text not null,
           primary key (project, number, position),
           foreign key (project, number) references releases (project, number));
         create table release_tickets (
           project text not null,
           number integer not null,
           ticket_project text not null,
           ticket_number integer not null,
           primary key (project, number, ticket_project, ticket_number),
           foreign key (project, number) references releases (project, number));
         create table ticket_blockers (
           project text not null,
           number integer not null,
           blocker_project text not null,
           blocker_number integer not null,
           primary key (project, number, blocker_project, blocker_number),
           foreign key (project, number) references tickets (project, number));"

/// What the attributes cannot say, each refused when the model is built.
module Unclear =
    type TwoNamesId = TwoNamesId of int64

    [<Table "two_names">]
    type TwoNames = { Id: TwoNamesId; [<Column("a", "b")>] Title: string }

    type ShortNamesId = ShortNamesId of int64

    [<Table "short_names">]
    type ShortNames = { Id: ShortNamesId; [<Column("project")>] Ticket: TicketKey }

    type Labelled = { [<Column "label_text">] Text: string; Weight: int }
    type ValueColumnId = ValueColumnId of int64

    [<Table "value_column">]
    type ValueColumn = { Id: ValueColumnId; Label: Labelled }

    type TwinKey = { First: TicketKey; Second: TicketKey }

    [<Table "twins">]
    type Twins = { Id: TwinKey }

    let models =
        Map
            [ "two column names on a field of one column", [ typeof<TwoNames> ]
              "one column name for a key of two parts", [ typeof<ShortNames>; typeof<Ticket> ]
              "Column inside a plain value", [ typeof<ValueColumn> ]
              "two key parts of an Id with the same columns", [ typeof<Twins>; typeof<Ticket> ] ]

namespace Plasm.Specs

open System
open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.Specs.CompositeReferences

/// Feature: A key of several parts is referred to, and owns, as a key of one does
[<Binding>]
type CompositeReferenceSteps(w: World) =

    let repo () = w.Get<Repo> "trackerRepo"

    let key (text: string) : TicketKey =
        match text.Split '#' with
        | [| p; n |] -> { Project = p; Number = int n }
        | _ -> failwith $"not a key: {text}"

    let releaseKey (text: string) : ReleaseKey =
        let k = key text
        { Project = k.Project; Number = k.Number }

    let quoted (text: string) = [ for m in Regex.Matches(text, "\"([^\"]+)\"") -> m.Groups.[1].Value ]

    let tickets () =
        if w.Has "trackerTickets" then w.Get<Map<TicketKey, Ticket>> "trackerTickets" else Map.empty

    let remember (t: Ticket) = w.Set("trackerTickets", (tickets ()).Add(t.Id, t))

    let insert (t: Ticket) =
        task {
            let! r = repo().Transact(Tx.insert t: Tx<_, unit>)
            remember (ok r)
        }

    let write (x: 'T) =
        task {
            let! r = repo().Transact(Tx.insert x: Tx<_, unit>)
            ok r |> ignore
        }

    let ticket (k: TicketKey) (title: string) (notes: string list) (parent: TicketKey option) =
        { Id = k
          Title = title
          Tags = []
          Watchers = []
          Parent = parent
          Version = 1
          Notes = [ for b in notes -> { Id = NoteId 0L; Ticket = k; Body = b } ] }

    let readTicket (k: TicketKey) =
        task {
            let! r = repo().One(plasm { for t in tableOf<Ticket> do where (t.Id = k) })
            return (ok r).Value
        }

    let change (k: TicketKey) (f: Ticket -> Ticket) =
        task {
            let! before = readTicket k
            let! r = repo().Transact(Tx.update (before, f before): Tx<_, unit>)
            remember (ok r)
        }

    let show (t: Ticket) =
        let notes = t.Notes |> List.map (fun n -> "\"" + n.Body + "\"") |> String.concat ", "
        $"{t.Id.Project}#{t.Id.Number} \"{t.Title}\" with {notes}"

    let ddlOf (table: string) (ddl: string) =
        let start = ddl.IndexOf $"create table {table} ("
        if start < 0 then failwith $"no create table {table} in:\n{ddl}"
        ddl.Substring(start, ddl.IndexOf(");", start) - start)

    [<StepDefinition("a tracker's tables, written by hand")>]
    member _.Tables() =
        task {
            do! w.Db.MakeTables(Schemas.tables, Schemas.model)
            w.Set("trackerRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("I start a repository over the tracker, checking its tables")>]
    member _.Start() =
        task {
            let outcome =
                try
                    let options = { RepoOptions.Default with Queries = StartupCheck.Ignore }
                    Repo.Start(Schemas.model, w.Db.Provider, typeof<Ticket>.Assembly, options).Result |> ignore
                    "started"
                with ex ->
                    let rec inner (e: exn) = if isNull e.InnerException then e else inner e.InnerException
                    (inner ex).Message

            w.Set("trackerStart", outcome)
        }
        |> unit

    [<StepDefinition("it starts")>]
    member _.Started() = equal "started" (w.Get<string> "trackerStart") "the start"

    [<StepDefinition("I print the DDL of the tracker model")>]
    member _.Ddl() = w.Set("trackerDdl", repo().Ddl())

    [<StepDefinition("^it has primary key \\((.*)\\) for (.*)$")>]
    member _.PrimaryKey(columns: string, tables: string) =
        let ddl = w.Get<string> "trackerDdl"

        for t in tables.Split(", ") do
            check ((ddlOf t ddl).Contains $"primary key ({columns})") (ddlOf t ddl)

    [<StepDefinition("^it has foreign key \\((.*)\\) references (\\w+) \\((.*)\\)$")>]
    member _.ForeignKey(columns: string, table: string, target: string) =
        let ddl = w.Get<string> "trackerDdl"
        check (ddl.Contains $"foreign key ({columns}) references {table} ({target})") ddl

    [<StepDefinition("^it has foreign key \\((.*)\\) references (\\w+) \\((.*)\\) for (.*)$")>]
    member _.ForeignKeyFor(columns: string, table: string, target: string, holders: string) =
        let ddl = w.Get<string> "trackerDdl"

        for h in holders.Replace(" and ", ", ").Split(", ") do
            // After the tables (PostgreSQL), or in the table's own (SQLite's
            // foreign keys are in their tables).
            let lines = Array.append (ddl.Split('\n') |> Array.filter (fun l -> l.StartsWith $"alter table {h} ")) ((ddlOf h ddl).Split('\n'))
            check (lines |> Array.exists (fun l -> l.Contains $"foreign key ({columns}) references {table} ({target})")) ddl

    [<StepDefinition("^it has no foreign key over (.*)$")>]
    member _.NoForeignKey(columns: string) =
        let ddl = w.Get<string> "trackerDdl"

        for c in columns.Replace(" or ", ", ").Split(", ") do
            check (not (ddl.Contains $"foreign key ({c}")) ddl

    [<StepDefinition("the foreign key from ticket_notes to tickets is dropped")>]
    member _.DropReference() =
        task {
            do! w.Db.DropConstraint("ticket_notes", "ticket_notes_project_ticket_number_fkey")
            ()
        }
        |> unit

    [<StepDefinition("I check the tracker model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("trackerIssues", ok r)
        }
        |> unit

    [<StepDefinition("a missing reference is reported for Note.Ticket")>]
    member _.MissingReference() =
        let issues = w.Get<SchemaIssue list> "trackerIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingReference && i.Model = "Note" && i.Field = Some "Ticket"))
            $"issues: %A{issues}"

    [<StepDefinition("^the ticket (\\w+#\\d+) \"([^\"]+)\" with the notes? (.*)$")>]
    member _.GivenTicket(k: string, title: string, notes: string) =
        insert (ticket (key k) title (quoted notes) None) |> unit

    [<StepDefinition("^the ticket (\\w+#\\d+) \"([^\"]+)\" with no notes$")>]
    member _.GivenBareTicket(k: string, title: string) = insert (ticket (key k) title [] None) |> unit

    [<StepDefinition("^the ticket (\\w+#\\d+) \"([^\"]+)\" under (\\w+#\\d+)$")>]
    member _.Under(k: string, title: string, parent: string) =
        insert (ticket (key k) title [] (Some(key parent))) |> unit

    [<StepDefinition("^I read the tickets of the project \"(\\w+)\"$")>]
    member _.ReadProject(p: string) =
        task {
            w.MarkCommands()
            let! r = repo().All(plasm { for t in tableOf<Ticket> do where (t.Id.Project = p); orderBy t.Id.Number })
            w.Set("trackerRead", r)
        }
        |> unit

    [<StepDefinition("^the tickets are (.*)$")>]
    member _.TicketsAre(expected: string) =
        let read = ok (w.Get<Result<Ticket list, DbError>> "trackerRead")
        equal expected (read |> List.map show |> String.concat " and ") "tickets"

    [<StepDefinition("the notes were read in one command, for every ticket at once")>]
    member _.NotesOnce() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.filter (fun s -> s.Contains "FROM \"ticket_notes\"")
        equal 1 sql.Length $"commands reading notes: %A{sql}"

    [<StepDefinition("^the note \"(.*)\" is added to (\\w+#\\d+)$")>]
    member _.AddNote(body: string, k: string) =
        let k = key k
        change k (fun t -> { t with Notes = t.Notes @ [ { Id = NoteId 0L; Ticket = k; Body = body } ] }) |> unit

    [<StepDefinition("^the note \"(.*)\" is removed from (\\w+#\\d+)$")>]
    member _.RemoveNote(body: string, k: string) =
        change (key k) (fun t -> { t with Notes = t.Notes |> List.filter (fun n -> n.Body <> body) }) |> unit

    [<StepDefinition("^the note row \"(.*)\" holds project (\\w+) and ticket_number (\\d+)$")>]
    member _.NoteRow(body: string, p: string, n: int) =
        task {
            let! stored = w.Db.Scalar<obj>("select project || '#' || ticket_number from ticket_notes where body = @b", [ "b", box body ])
            equal $"{p}#{n}" (string stored) "the note's columns"
        }
        |> unit

    [<StepDefinition("^the ticket (\\w+#\\d+) is read with the notes? (.*)$")>]
    member _.ReadWith(k: string, notes: string) =
        task {
            let! t = readTicket (key k)
            equal (quoted notes) (t.Notes |> List.map (fun n -> n.Body)) "notes"
        }
        |> unit

    [<StepDefinition("^the parent columns of (\\w+#\\d+) hold (\\w+) and (\\d+)$")>]
    member _.ParentColumns(k: string, p: string, n: int) =
        task {
            let k = key k

            let! stored =
                w.Db.Scalar<obj>(
                    "select parent_project || '#' || parent_number from tickets where project = @p and number = @n",
                    [ "p", box k.Project; "n", box k.Number ]
                )

            equal $"{p}#{n}" (string stored) "the parent columns"
        }
        |> unit

    [<StepDefinition("^the parent columns of (\\w+#\\d+) are both NULL$")>]
    member _.ParentNull(k: string) =
        task {
            let k = key k

            let! stored =
                w.Db.Scalar<obj>(
                    "select CASE WHEN parent_project is null and parent_number is null THEN 'true' ELSE 'false' END from tickets where project = @p and number = @n",
                    [ "p", box k.Project; "n", box k.Number ]
                )

            equal "true" (string stored) "both parent columns NULL"
        }
        |> unit

    [<StepDefinition("^the ticket (\\w+#\\d+) is read under (\\w+#\\d+)$")>]
    member _.ReadUnder(k: string, parent: string) =
        task {
            let! t = readTicket (key k)
            equal (Some(key parent)) t.Parent "the parent"
        }
        |> unit

    [<StepDefinition("^the parent_number of (\\w+#\\d+) is set behind Plasm's back to (\\d+)$")>]
    member _.PartialParent(k: string, n: int) =
        task {
            let k = key k

            // Past the check SQLite's Ddl holds the reference's parts with.
            if w.Db.Kind = Plasm.TestDomain.Database.SQLite then
                do! w.Db.DropConstraint("tickets", "tickets_parent_present")

            let! _ =
                w.Db.Exec(
                    "update tickets set parent_number = @v where project = @p and number = @n",
                    [ "v", box n; "p", box k.Project; "n", box k.Number ]
                )

            ()
        }
        |> unit

    [<StepDefinition("^reading the tickets fails, naming (\\w+)$")>]
    member _.ReadFails(field: string) =
        match w.Get<Result<Ticket list, DbError>> "trackerRead" with
        | Ok rows -> failwithf "the read succeeded: %A" rows
        | Error e -> check ((string e).Contains field) (string e)

    [<StepDefinition("^the release (\\w+#\\d+) \"([^\"]+)\" with no steps$")>]
    member this.GivenBareRelease(k: string, name: string) = this.GivenRelease(k, name, "")

    [<StepDefinition("^the release (\\w+#\\d+) \"([^\"]+)\" with the steps? (.*)$")>]
    member _.GivenRelease(k: string, name: string, steps: string) =
        task {
            let rk = releaseKey k
            do! write { Release.Id = rk; Name = name; Version = 1 }

            if not (String.IsNullOrEmpty steps) then
                for m in Regex.Matches(steps, "(\\d+) \"([^\"]+)\"") do
                    do! write { Step.Id = { Release = rk; Position = int m.Groups.[1].Value }; Task = m.Groups.[2].Value }
        }
        |> unit

    [<StepDefinition("^I read the steps of the release (\\w+#\\d+), ordered by position$")>]
    member _.Steps(k: string) =
        task {
            let rk = releaseKey k
            w.MarkCommands()
            let! r = repo().All(plasm { for s in tableOf<Step> do where (s.Id.Release = rk); orderBy s.Id.Position })
            w.Set("trackerSteps", ok r)
        }
        |> unit

    [<StepDefinition("^the steps are (.*)$")>]
    member _.StepsAre(expected: string) =
        let steps = w.Get<Step list> "trackerSteps"
        equal expected (steps |> List.map (fun s -> $"{s.Id.Position} \"{s.Task}\"") |> String.concat " and ") "steps"

    [<StepDefinition("^the release (\\w+#\\d+) ships the tickets (\\w+#\\d+) and (\\w+#\\d+)$")>]
    member _.Ships(k: string, a: string, b: string) =
        task {
            for t in [ a; b ] do
                do! write { Shipped.Id = { Release = releaseKey k; Ticket = key t } }
        }
        |> unit

    [<StepDefinition("^the release (\\w+#\\d+) ships (\\w+#\\d+) and (\\w+#\\d+)$")>]
    member _.Shipped(k: string, a: string, b: string) =
        task {
            let rk = releaseKey k
            let! r = repo().All(plasm { for s in tableOf<Shipped> do where (s.Id.Release = rk); orderBy s.Id.Ticket.Project })
            let shipped = ok r |> List.map (fun s -> $"{s.Id.Ticket.Project}#{s.Id.Ticket.Number}")
            equal [ a; b ] shipped "tickets shipped"
        }
        |> unit

    [<StepDefinition("^(\\w+#\\d+) is blocked by (\\w+#\\d+)$")>]
    member _.Block(k: string, by: string) = write { Blocker.Id = { Ticket = key k; Blocker = key by } } |> unit

    [<StepDefinition("^(\\w+#\\d+) is read as blocked by (\\w+#\\d+)$")>]
    member _.Blocked(k: string, by: string) =
        task {
            let tk = key k
            let! r = repo().All(plasm { for b in tableOf<Blocker> do where (b.Id.Ticket = tk) })
            equal [ by ] (ok r |> List.map (fun b -> $"{b.Id.Blocker.Project}#{b.Id.Blocker.Number}")) "blockers"
        }
        |> unit

    [<StepDefinition("I build a tracker model of (.*)")>]
    member _.Build(records: string) =
        w.Set("storedBuild", Model.build [] Unclear.models.[records])
