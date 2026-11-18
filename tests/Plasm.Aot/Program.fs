/// The native-AOT check (docs/aot.md): each step prints one line, so the
/// runtime's output and the native binary's can be compared line by line.
/// Nothing here formats with printf: it is not available under AOT. The
/// shapes are Kanban's (examples/Kanban): an application's own records beside
/// its queries, a module of query values, and its write workflows.
module Plasm.Aot.Program

open System
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.Aot.Shop
open Plasm.Aot.App

let schema =
    "drop table if exists notes; drop table if exists tickets; drop table if exists customers;
     create table customers (
       id bigint generated always as identity primary key,
       name text not null,
       email text not null constraint customers_email_key unique);
     create table tickets (
       id bigint generated always as identity primary key,
       title text not null,
       owner bigint references customers(id),
       stage text not null,
       stage_who text,
       stage_since timestamptz,
       stage_at timestamptz,
       due date,
       points bigint,
       priority text,
       created timestamptz not null,
       size int not null,
       price numeric(12,2) not null,
       ref uuid not null,
       checklist jsonb not null);
     create table notes (
       id bigint generated always as identity primary key,
       ticket_id bigint not null references tickets(id),
       text text not null,
       done boolean not null);"

let say (line: string) = Console.WriteLine line

let step (name: string) (f: unit -> string) =
    try
        say (name + ": " + f ())
    with ex ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        let i = inner ex
        say (name + ": threw " + i.GetType().FullName + ": " + i.Message)

        // PLASM_AOT_TRACE=1 adds where it was thrown (not compared: the
        // runtime's frames are not the binary's).
        if Environment.GetEnvironmentVariable "PLASM_AOT_TRACE" = "1" then
            Console.Error.WriteLine(i.StackTrace)

let customer (c: Customer) =
    let (CustomerId id) = c.Id
    "Customer " + string id + " " + c.Name + " <" + c.Email + ">"

let since = DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)

let ticket (t: Ticket) =
    let (TicketId id) = t.Id

    let stage =
        match t.Stage with
        | Todo -> "todo"
        | Doing(who, at) -> "doing by " + who + " since " + at.ToString("o")
        | Done at -> "done at " + at.ToString("o")

    let due = match t.Due with Some d -> d.ToString("yyyy-MM-dd") | None -> "no date"
    let points = match t.Points with Some p -> string p | None -> "no points"
    let priority = match t.Priority with Some Low -> "low" | Some High -> "high" | None -> "no priority"
    let items = String.Join("/", t.Checklist |> List.map (fun i -> i.Text + (if i.Done then "+" else "-")))
    let notes = String.Join("/", t.Notes |> List.map (fun n -> n.Text + (if n.Done then "+" else "-")))
    "Ticket " + string id + " " + t.Title + ", " + stage + ", " + due + ", " + points + ", " + priority + ", [" + items + "], [" + notes + "]"

let tickets (ts: Ticket list) = String.Join("; ", ts |> List.map ticket)

/// What a failed read or write says, without printf.
let refusal (e: DbError) =
    match e with
    | DbError.Database f -> "database: " + f.Message
    | DbError.Translation t -> "translation: " + t.ToString()
    | DbError.Materialization m -> "materialization: " + m.Message
    | DbError.MoreThanOneRow -> "more than one row"

[<EntryPoint>]
let main argv =
    let cs = argv.[0]

    do
        use conn = new NpgsqlConnection(cs)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

    let mutable model = None

    step "model" (fun () ->
        let m = Model.buildOrFail [] [ typeof<Customer>; typeof<Ticket> ]
        model <- Some m
        "built, " + string m.Types.Length + " records")

    match model with
    | None -> 1
    | Some m ->
        let mutable repo = None

        // Every query value of the application translated as it starts.
        step "start, checking every query" (fun () ->
            let r = Repo.Start(m, Postgres.connect cs, typeof<Ticket>.Assembly, { RepoOptions.Unchecked with Queries = StartupCheck.Crash }).Result
            repo <- Some r
            "started")

        let repo =
            match repo with
            | Some r -> r
            | None -> Repo.Start(m, Postgres.connect cs, typeof<Ticket>.Assembly, RepoOptions.Unchecked).Result

        let all (q: Query<'T>) (show: 'T -> string) =
            match repo.All(q).Result with
            | Ok rows -> String.Join("; ", rows |> List.map show)
            | Error e -> "failed: " + refusal e

        let selected (q: Projection<'R>) (show: 'R -> string) =
            match repo.All(q).Result with
            | Ok rows -> String.Join("; ", rows |> List.map show)
            | Error e -> "failed: " + refusal e

        let mutable ada = None

        step "insert" (fun () ->
            match repo.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }: Tx<_, unit>).Result with
            | Ok c ->
                ada <- Some c
                customer c
            | Error e -> "failed: " + e.Display)

        let ada = ada.Value

        step "read by typed id" (fun () -> all (plasm { for c in tableOf<Customer> do where (c.Id = ada.Id) }) customer)
        step "read by a reflected member" (fun () -> all (plasm { for c in tableOf<Customer> do where c.AtExample }) customer)
        step "read by a reflected function" (fun () -> all (plasm { for c in tableOf<Customer> do where (named "Ada" c) }) customer)

        let mutable first = None

        step "insert an application's record: a union of fields, options, a JSON list, owned rows" (fun () ->
            let t =
                { Id = TicketId 0L
                  Title = "ship"
                  Owner = Some ada.Id
                  Stage = Doing("ada", since)
                  Due = Some(DateOnly(2026, 12, 1))
                  Points = Some 3L
                  Priority = Some High
                  Created = since
                  Size = 3
                  Price = 12.5m
                  Ref = Guid("00000000-0000-0000-0000-000000000001")
                  Checklist = [ { Text = "build"; Done = true }; { Text = "test"; Done = false } ]
                  Notes = [ { Id = NoteId 0L; TicketId = TicketId 0L; Text = "started"; Done = true } ] }

            match repo.Transact(Tx.insert t: Tx<_, unit>).Result with
            | Ok t ->
                first <- Some t
                ticket t
            | Error e -> "failed: " + e.Display)

        let first = first.Value

        step "insert another" (fun () ->
            let t =
                { first with
                    Id = TicketId 0L
                    Title = "plan"
                    Stage = Todo
                    Due = None
                    Points = None
                    Priority = None
                    Created = since.AddHours 1.0
                    Size = 5
                    Price = 7.25m
                    Ref = Guid("00000000-0000-0000-0000-000000000002")
                    Checklist = []
                    Notes = [ { Id = NoteId 0L; TicketId = TicketId 0L; Text = "ask"; Done = false } ] }

            match repo.Transact(Tx.insert t: Tx<_, unit>).Result with
            | Ok t -> ticket t
            | Error e -> "failed: " + e.Display)

        step "read it by its case" (fun () -> all (plasm { for t in tableOf<Ticket> do where t.Stage.IsDoing }) ticket)

        step "select a row record" (fun () ->
            selected
                (plasm {
                    for t in tableOf<Ticket> do
                        orderBy t.Title
                        select { Title = t.Title; Points = t.Points }
                 })
                (fun (r: TicketLine) -> r.Title + " " + (match r.Points with Some p -> string p | None -> "-")))

        step "a prepared query by typed id" (fun () -> all (Queries.ticketById first.Id) ticket)
        step "a prepared query through a reflected member over a case's field" (fun () -> all (CaseFieldQueries.assignedTo "ada") ticket)
        step "a list over owned rows" (fun () -> all Queries.withOpenNotes ticket)
        step "a list in a JSON document" (fun () -> all Queries.withOpenItems ticket)

        step "loads bound with let! and and!, with take" (fun () ->
            match repo.All(Queries.customersWithTickets).Result with
            | Ok rows ->
                String.Join(
                    "; ",
                    rows
                    |> List.map (fun r -> r.Customer.Name + ": " + String.Join(", ", r.Titles) + " (started: " + String.Join(", ", r.Started) + ")")
                )
            | Error e -> "failed: " + refusal e)

        step "a join selecting a record" (fun () ->
            match repo.All(CaseFieldQueries.work).Result with
            | Ok rows -> String.Join("; ", rows |> List.map (fun r -> r.Customer + " " + r.Ticket + " " + (match r.Who with Some w -> w | None -> "-")))
            | Error e -> "failed: " + refusal e)

        step "a page" (fun () -> all (Queries.page 1 1) ticket)

        step "grouped by an int" (fun () -> selected Queries.bySize (fun (size, n) -> string size + ": " + string n))
        step "a tuple select with an int" (fun () -> selected Queries.titlesAndSizes (fun (title, size) -> title + " " + string size))
        step "a record select with an int" (fun () -> selected Queries.sizes (fun r -> r.Title + " " + string r.Size))

        step "an aggregate over decimal" (fun () ->
            selected Queries.totalPrice (fun (owner, total) ->
                (match owner with Some(CustomerId id) -> string id | None -> "-") + ": " + total.ToString(Globalization.CultureInfo.InvariantCulture)))

        step "a captured list of Guids" (fun () -> all (Queries.withRefs [ Guid("00000000-0000-0000-0000-000000000002") ]) ticket)
        step "an option of a value type compared with Some" (fun () -> all Queries.withPoints ticket)
        step "a prepared argument of a value type in Some" (fun () -> all (Queries.withPointsOf 3L) ticket)

        step "grouped by a field and a reflected function, selecting a tuple with a count" (fun () ->
            selected Queries.phaseCounts (fun (owner, p, n) ->
                let phase =
                    match p with
                    | Phase.Waiting -> "waiting"
                    | Phase.Working -> "working"
                    | Phase.Finished -> "finished"

                (match owner with Some(CustomerId id) -> string id | None -> "-") + " " + phase + ": " + string n))

        step "a count" (fun () ->
            match repo.Count(Queries.ticketsOf ada.Id).Result with
            | Ok n -> string n
            | Error e -> "failed: " + refusal e)

        step "read, decide and update in one transaction" (fun () ->
            let finish: Tx<Ticket, string> =
                transaction {
                    let! t = Tx.one (Queries.ticketById first.Id) |> Tx.orReject "no such ticket"

                    match t.Stage with
                    | Doing _ ->
                        return!
                            Tx.update (
                                t,
                                { t with
                                    Stage = Done(since.AddDays 1.0)
                                    Notes = t.Notes @ [ { Id = NoteId 0L; TicketId = t.Id; Text = "shipped"; Done = true } ] }
                            )
                    | _ -> return! Tx.fail "not in progress"
                }

            match repo.Transact(finish).Result with
            | Ok t -> ticket t
            | Error e -> "failed: " + e.Display)

        step "insert what is missing" (fun () ->
            let people = [ { ada with Id = CustomerId 0L }; { Id = CustomerId 0L; Name = "Bob"; Email = "bob@example.com" } ]

            match repo.Transact(Tx.execute (Bulk.insertMissing (people, fun c -> c.Email)): Tx<_, unit>).Result with
            | Ok inserted -> String.Join("; ", inserted |> List.map customer)
            | Error e -> "failed: " + e.Display)

        step "a broken constraint is the field's error" (fun () ->
            match repo.Transact(Tx.insert { ada with Id = CustomerId 0L; Name = "Ada again" }: Tx<_, unit>).Result with
            | Ok c -> "inserted " + customer c
            | Error e -> "refused: " + e.Display)

        0
