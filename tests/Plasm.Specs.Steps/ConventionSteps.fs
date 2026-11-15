namespace Plasm.Specs

open System
open Reqnroll
open Plasm

/// A domain of its own for scalar-conventions.feature: none of its types has a
/// scalar mapping registered.
module Conventions =

    type TicketId = TicketId of int64

    type Code = Code of string

    /// A validated text: 1 to 20 characters.
    type Title =
        private
        | Title of string

        member t.Value =
            let (Title v) = t
            v

        static member create(text: string) : Result<Title, string> =
            if String.IsNullOrEmpty text || text.Length > 20 then
                Error "must be 1 to 20 characters"
            else
                Ok(Title text)

    type Priority =
        | Low
        | Normal
        | VeryHigh

    type Ticket =
        { Id: TicketId
          Code: Code
          Title: Title
          Priority: Priority
          Reviewer: Code option }

    /// A private representation and no create function: Plasm cannot read it
    /// without bypassing the domain.
    type Secret = private Secret of string

    type Locked = { Id: TicketId; Secret: Secret }

    /// A create function in a member and another in the type's module.
    type Twice =
        | Twice of string

        static member create(text: string) : Result<Twice, string> = Ok(Twice text)

    module Twice =
        let create (text: string) : Result<Twice, string> = Ok(Twice text)

    type Doubled = { Id: TicketId; Twice: Twice }

    let tickets = schema<Ticket> "tickets" { identityKey (fun t -> t.Id) }

    let table =
        "drop table if exists tickets;
         create table tickets (
           id bigint generated always as identity primary key,
           code text not null,
           title text not null,
           priority text not null,
           reviewer text);"

    let title text =
        match Title.create text with
        | Ok t -> t
        | Error e -> failwith e

    let ticket code priority reviewer =
        { Id = TicketId 0L
          Code = Code code
          Title = title "a ticket"
          Priority = priority
          Reviewer = reviewer |> Option.map Code }

    let parsePriority =
        function
        | "Low" -> Low
        | "Normal" -> Normal
        | "VeryHigh" -> VeryHigh
        | other -> failwith $"not a Priority: {other}"

open Conventions

/// Feature: Domain scalars are stored by convention
[<Binding>]
type ConventionSteps(w: World) =

    let repo () = w.Get<Repo> "ticketRepo"

    let insert (t: Ticket) =
        task {
            let! r = repo().Transact(Tx.insert t: Tx<_, unit>)
            return ok r
        }

    let all () =
        task {
            let! r = repo().All(plasm { for t in tickets do orderBy t.Id })
            return r
        }

    let refusal (build: unit -> Result<Model, ConfigError list>) =
        match build () with
        | Ok _ -> failwith "the model was built"
        | Error errors -> w.Set("refusal", errors)

    [<StepDefinition("a tickets table")>]
    member _.Table() = w.Db.Exec Conventions.table |> unit

    [<StepDefinition("Tickets in a model built with no scalar mappings")>]
    member _.NoMappings() =
        let model = Model.buildSchemasOrFail [] [ tickets ]
        w.Set("ticketRepo", w.Db.RepoWith(model = model))

    [<StepDefinition("^Tickets in a model built with the mapping Scalar\\.enum \\[ Low, \"lo\"; Normal, \"mid\"; VeryHigh, \"hi\" \\]$")>]
    member _.Override() =
        let model = Model.buildSchemasOrFail [ Scalar.enum [ Low, "lo"; Normal, "mid"; VeryHigh, "hi" ] ] [ tickets ]
        w.Set("ticketRepo", w.Db.RepoWith(model = model))

    [<StepDefinition("I insert a Ticket with code \"(.*)\"")>]
    member _.InsertCode(code: string) =
        task {
            let! t = insert (ticket code Normal None)
            w.Set("inserted", t)
        }
        |> unit

    [<StepDefinition("^I insert a Ticket with priority (Low|Normal|VeryHigh)$")>]
    member _.InsertPriority(priority: string) =
        task {
            let! t = insert (ticket "T-1" (parsePriority priority) None)
            w.Set("inserted", t)
        }
        |> unit

    [<StepDefinition("the tickets table holds \"(.*)\" in its (\\w+) column")>]
    member _.Holds(text: string, column: string) =
        task {
            let! stored = w.Db.Scalar<string>($"select {column} from tickets limit 1")
            check (stored = text) $"the {column} column holds '{stored}'"
        }
        |> unit

    [<StepDefinition("reading the Ticket back gives the code \"(.*)\"")>]
    member _.ReadCode(code: string) =
        task {
            let! r = all ()
            let read = ok r |> List.exactlyOne
            check (read.Code = Code code) $"read back %A{read.Code}"
        }
        |> unit

    [<StepDefinition("I insert a Ticket with reviewer \"(.*)\" and one without a reviewer")>]
    member _.InsertReviewers(reviewer: string) =
        task {
            let! _ = insert (ticket "T-1" Normal (Some reviewer))
            let! _ = insert (ticket "T-2" Normal None)
            return ()
        }
        |> unit

    [<StepDefinition("reading them back gives the reviewer Some \"(.*)\" and None")>]
    member _.ReadReviewers(reviewer: string) =
        task {
            let! r = all ()
            let reviewers = ok r |> List.map (fun t -> t.Reviewer)
            check (reviewers = [ Some(Code reviewer); None ]) $"read back %A{reviewers}"
        }
        |> unit

    [<StepDefinition("Tickets with the codes \"(.*)\" and \"(.*)\"")>]
    member _.Codes(a: string, b: string) =
        task {
            let! _ = insert (ticket a Normal None)
            let! _ = insert (ticket b Normal None)
            return ()
        }
        |> unit

    [<StepDefinition("I keep the Tickets whose code is \"(.*)\"")>]
    member _.KeepCode(code: string) =
        task {
            let wanted = Code code
            let! r = repo().All(plasm { for t in tickets do where (t.Code = wanted) })
            w.Set("kept", ok r)
        }
        |> unit

    [<StepDefinition("only the Ticket with code \"(.*)\" is returned")>]
    member _.OnlyCode(code: string) =
        let kept = w.Get<Ticket list> "kept"
        check (kept |> List.map (fun t -> t.Code) = [ Code code ]) $"kept %A{kept |> List.map (fun t -> t.Code)}"

    [<StepDefinition("a ticket row whose title is empty text")>]
    member _.EmptyTitle() =
        w.Db.Exec "insert into tickets (code, title, priority) values ('T-1', '', 'normal')" |> unit

    [<StepDefinition("I read the Tickets")>]
    member _.Read() =
        task {
            let! r = all ()
            w.Set("read", r)
        }
        |> unit

    [<StepDefinition("the read fails for the Title field, with the domain's reason \"(.*)\"")>]
    member _.ReadFails(reason: string) =
        match w.Get<Result<Ticket list, DbError>> "read" with
        | Error(DbError.Materialization m) ->
            check (m.Property = "Title") $"the failure names {m.Property}"
            check (m.Message.Contains reason) $"the failure says {m.Message}"
        | other -> failwithf "expected a materialization error, got %A" other

    [<StepDefinition("I build a model of a record with a field of a private wrapper type without create")>]
    member _.BuildPrivate() =
        refusal (fun () -> Model.buildSchemas [] [ schema<Locked> "locked" { identityKey (fun l -> l.Id) } ])

    [<StepDefinition("I build a model of a record with a field of a type with a create function in a member and in its module")>]
    member _.BuildTwice() =
        refusal (fun () -> Model.buildSchemas [] [ schema<Doubled> "doubled" { identityKey (fun d -> d.Id) } ])

    [<StepDefinition("the model is refused for that field, saying (.*)")>]
    member _.Refused(saying: string) =
        let errors = w.Get<ConfigError list> "refusal"
        let expected = saying.Replace("the type has ", "")

        check
            (errors |> List.exists (fun e -> e.Property.IsSome && e.Message.Contains expected))
            $"the model was refused with %A{errors}"

    [<StepDefinition("Tickets with the priorities VeryHigh, Low and Normal")>]
    member _.Priorities() =
        task {
            for p in [ VeryHigh; Low; Normal ] do
                let! _ = insert (ticket "T" p None)
                ()
        }
        |> unit

    [<StepDefinition("I order the Tickets by priority")>]
    member _.OrderByPriority() =
        task {
            let! r = repo().All(plasm { for t in tickets do orderBy t.Priority })
            w.Set("ordered", ok r |> List.map (fun t -> t.Priority))
        }
        |> unit

    [<StepDefinition("they come back as Low, Normal and VeryHigh")>]
    member _.Ordered() =
        let ordered = w.Get<Priority list> "ordered"
        check (ordered = [ Low; Normal; VeryHigh ]) $"ordered %A{ordered}"
