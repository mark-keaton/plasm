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
    "drop table if exists shipment_events; drop table if exists shipments; drop table if exists notes; drop table if exists tickets; drop table if exists customers;
     create table shipments (
       carrier text not null,
       number integer not null,
       status text not null,
       version int not null,
       primary key (carrier, number));
     create table shipment_events (
       id bigint generated always as identity primary key,
       shipment_carrier text not null,
       shipment_number integer not null,
       text text not null,
       foreign key (shipment_carrier, shipment_number) references shipments (carrier, number));
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
       labels text[] not null,
       watchers bigint[] not null,
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
    let labels = String.Join("/", t.Labels)
    let watchers = String.Join("/", t.Watchers |> List.map (fun (CustomerId c) -> string c))
    "Ticket " + string id + " " + t.Title + ", " + stage + ", " + due + ", " + points + ", " + priority + ", [" + items + "], [" + notes + "], labels [" + labels + "], watchers [" + watchers + "]"

let tickets (ts: Ticket list) = String.Join("; ", ts |> List.map ticket)

/// What a failed read or write says, without printf.
let refusal (e: DbError) =
    match e with
    | DbError.Database f -> "database: " + f.Message
    | DbError.Translation t -> "translation: " + t.ToString()
    | DbError.Materialization m -> "materialization: " + m.Message
    | DbError.MoreThanOneRow -> "more than one row"

/// The provider: over Npgsql's slim data source builder when compiled with
/// PlasmAotSlim=true (only what Plasm's types need enabled), else connect.
let dataSource (cs: string) : NpgsqlDataSource =
#if PLASM_AOT_SLIM
    NpgsqlSlimDataSourceBuilder(cs).EnableArrays().Build()
#else
    NpgsqlDataSource.Create cs
#endif

let provider (cs: string) : IProvider = PostgresProvider(dataSource cs) :> IProvider

/// Cold start (tests/Plasm.Aot/coldstart.sh): the phases of a process that
/// builds its model, starts a repository and runs one prepared query, timed to
/// stderr as "phase ms" lines. The schema is the check's, created beforehand.
let coldStart (cs: string) (queries: StartupCheck) (schemaCheck: bool) =
    let sinceStart = (DateTime.Now - Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds
    let clock = Diagnostics.Stopwatch.StartNew()
    let mutable last = 0.0
    let mark (phase: string) =
        let now = clock.Elapsed.TotalMilliseconds
        Console.Error.WriteLine(phase + " " + (now - last).ToString("F1", Globalization.CultureInfo.InvariantCulture))
        last <- now
    Console.Error.WriteLine("to main " + sinceStart.ToString("F1", Globalization.CultureInfo.InvariantCulture))
    do
        use ds = dataSource cs
        use conn = ds.OpenConnection()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "select 1"
        cmd.ExecuteScalar() |> ignore
    mark "npgsql first connection"
    let m = Model.buildOrFail [] [ typeof<Customer>; typeof<Ticket>; typeof<Shipment> ]
    mark "model build"
    let repo = Repo.Start(m, provider cs, typeof<Ticket>.Assembly, { RepoOptions.Unchecked with Queries = queries }).Result
    mark "repo start"
    if schemaCheck then
        repo.CheckSchema().Result |> ignore
        mark "schema check"
    let first = repo.All(Queries.ticketById (TicketId 1L)).Result
    mark "first prepared query"
    let again = repo.All(Queries.ticketById (TicketId 1L)).Result
    mark "same query again"
    let other = repo.All(Queries.withOpenNotes).Result
    mark "another query"
    Console.Error.WriteLine("total " + (sinceStart + clock.Elapsed.TotalMilliseconds).ToString("F1", Globalization.CultureInfo.InvariantCulture))
    match first, again, other with
    | Ok _, Ok _, Ok _ -> 0
    | _ -> 1

[<EntryPoint>]
let main argv =
    // run.sh asks each run what it ran as: the runtime as shipped generates
    // code, a native binary cannot (docs/aot.md).
    if argv.[0] = "mode" then
        Console.WriteLine(if Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported then "dynamic code on" else "dynamic code off")
        exit 0

    let cs = argv.[0]
    if argv.Length > 1 && argv.[1] = "coldstart" then
        let queries = if argv.[2] = "check" then StartupCheck.Crash else StartupCheck.Ignore
        exit (coldStart cs queries (argv.Length > 3 && argv.[3] = "schema"))

    do
        use ds = dataSource cs
        use conn = ds.OpenConnection()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

    let mutable model = None

    step "model" (fun () ->
        let m = Model.buildOrFail [] [ typeof<Customer>; typeof<Ticket>; typeof<Shipment> ]
        model <- Some m
        "built, " + string m.Types.Length + " records")

    match model with
    | None -> 1
    | Some m ->
        let mutable repo = None

        // Every query value of the application translated as it starts.
        step "start, checking every query" (fun () ->
            let r = Repo.Start(m, provider cs, typeof<Ticket>.Assembly, { RepoOptions.Unchecked with Queries = StartupCheck.Crash }).Result
            repo <- Some r
            "started")

        let repo =
            match repo with
            | Some r -> r
            | None -> Repo.Start(m, provider cs, typeof<Ticket>.Assembly, RepoOptions.Unchecked).Result

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
                  Labels = [ "urgent"; "q4" ]
                  Watchers = [ ada.Id ]
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
                    Labels = []
                    Watchers = []
                    Checklist = []
                    Notes = [ { Id = NoteId 0L; TicketId = TicketId 0L; Text = "ask"; Done = false } ] }

            match repo.Transact(Tx.insert t: Tx<_, unit>).Result with
            | Ok t -> ticket t
            | Error e -> "failed: " + e.Display)

        step "read it by its case" (fun () -> all (plasm { for t in tableOf<Ticket> do where t.Stage.IsDoing }) ticket)

        step "a value in an array column" (fun () ->
            all (plasm { for t in tableOf<Ticket> do where (List.contains "urgent" t.Labels) }) ticket)

        step "an empty array column" (fun () -> all (plasm { for t in tableOf<Ticket> do where (List.isEmpty t.Labels) }) ticket)

        step "a prepared argument in an array column of typed ids" (fun () -> all (Queries.watchedBy ada.Id) ticket)

        let shipment (x: Shipment) =
            "Shipment " + x.Id.Carrier + "#" + string x.Id.Number + " " + x.Status + " v" + string x.Version
            + " [" + String.Join("/", x.Events |> List.map (fun e -> e.Text)) + "]"

        step "insert records keyed by several parts" (fun () ->
            let made =
                [ for carrier, number, status in [ "ups", 1, "packed"; "ups", 2, "sent"; "dhl", 1, "packed" ] ->
                      let key = { Carrier = carrier; Number = number }
                      let events = if number = 1 then [ { Id = EventId 0L; Shipment = key; Text = "picked up" }; { Id = EventId 0L; Shipment = key; Text = carrier + " depot" } ] else []

                      match repo.Transact(Tx.insert { Id = key; Status = status; Version = 1; Events = events }: Tx<_, unit>).Result with
                      | Ok x -> shipment x
                      | Error e -> "failed: " + e.Display ]

            String.Join("; ", made))

        step "read by a whole key, a prepared argument" (fun () -> all (Queries.shipmentByKey { Carrier = "ups"; Number = 2 }) shipment)

        step "an owned list under a key of several parts, loaded for every owner at once" (fun () ->
            all (plasm { for x in tableOf<Shipment> do orderBy x.Id.Carrier; thenBy x.Id.Number }) shipment)

        step "update found by every part and the version" (fun () ->
            match repo.One(Queries.shipmentByKey { Carrier = "dhl"; Number = 1 }).Result with
            | Ok(Some before) ->
                match repo.Transact(Tx.update (before, { before with Status = "sent" }): Tx<_, unit>).Result with
                | Ok x -> shipment x
                | Error e -> "failed: " + e.Display
            | other -> "not read: " + string other)

        step "select a row record" (fun () ->
            selected
                (plasm {
                    for t in tableOf<Ticket> do
                        orderBy t.Title
                        select { Title = t.Title; Points = t.Points }
                 })
                (fun (r: TicketLine) -> r.Title + " " + (match r.Points with Some p -> string p | None -> "-")))

        step "a prepared query by typed id" (fun () -> all (Queries.ticketById first.Id) ticket)

        step "a locked read in a transaction" (fun () ->
            match repo.Transact(Tx.one Queries.claimTicket: Tx<_, unit>).Result with
            | Ok(Some t) -> ticket t
            | other -> "not read: " + string other)
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

        step "filtered and boolean aggregates" (fun () ->
            selected Queries.sizeFacts (fun (n, total, any, all) -> $"{n} {total} {any} {all}"))

        step "list and text aggregates in a stated order" (fun () ->
            selected Queries.sizesInOrder (fun (up, down, titles, reversed) ->
                let ints (xs: int list) = xs |> List.map string |> String.concat ","
                $"[{ints up}] [{ints down}] {titles} | {reversed}"))

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

        step "a raw fragment, exists, and loads ordered, paged and distinct" (fun () ->
            selected Queries.customersWithTickets2 (fun r ->
                r.Name + ": " + String.Join(", ", r.Ordered) + "; sizes " + String.Join(", ", r.Sizes |> List.sort)))
        step "a load that joins and groups is refused" (fun () -> selected (Queries.refusedLoad ()) (fun r -> r.Name))
        step "a refused query's message shows its values" (fun () -> all (Queries.refusedWithValues ()) ticket)
        step "values a query computes before it runs" (fun () ->
            let owner = ada
            let n = 1
            let shape = {| Size = 3; Points = Some 3L; Ref = ref 5 |}

            all
                (plasm {
                    for t in tableOf<Ticket> do
                        where (
                            t.Owner = Some owner.Id
                            && t.Price > 7.25m * 1m - 0.5m
                            && t.Created < since.AddDays(float n)
                            && t.Due <> Some(DateOnly(2000, 1, 1))
                            && t.Size > abs (n - 2)
                            && t.Size <= shape.Size + shape.Ref.Value
                            && t.Points <> Some(shape.Points.Value + 10L)
                            && t.Title <> Queries.labelled "a" 1m "b" 1L 1.0 'c' false
                        )

                        orderBy t.Title
                        take (n * 2)
                })
                ticket)
        step "a prepared argument computed in the condition" (fun () -> all (Queries.withPointsTimesThree 1) ticket)
        step "a load of the record a row refers to" (fun () ->
            selected Queries.ticketsWithOwners (fun r -> r.Title + " by " + defaultArg r.Owner "nobody"))
        step "every aggregate" (fun () ->
            selected Queries.priceRanges (fun (owner, lo, hi, avg, sizes) ->
                let d (x: decimal option) = match x with Some v -> v.ToString(Globalization.CultureInfo.InvariantCulture) | None -> "-"
                let o = match owner with Some(CustomerId n) -> string n | None -> "-"
                o + ": " + d lo + ".." + d hi + ", avg " + d avg + ", " + string sizes + " sizes"))
        step "a query built at run time from declared fields, checked" (fun () ->
            let title = QueryField.ordered "title" <@ fun (t: Ticket) -> t.Title @>
            let doing = QueryField.equalVia "doing" <@ fun (t: Ticket) -> t.Stage.IsDoing @> (fun (s: string) -> Ok(s = "yes")) "yes"
            repo.CheckFields [ title; doing ]
            let c = QueryCondition.Or(title.Condition(QueryOperator.Equal, box "plan"), doing.Condition(QueryOperator.Equal, box "yes"))
            all (title.OrderBy(Query.from tableOf<Ticket> |> BuildQuery.where (c.ToExpr()), true)) ticket)
        step "the fields a caller chose, read and checked" (fun () ->
            let title = ReadField.value "title" <@ fun (t: Ticket) -> t.Title @>
            let stage = ReadField.via "stage" <@ fun (t: Ticket) -> t.Stage @> (fun s -> match s with Todo -> "todo" | Doing _ -> "doing" | Done _ -> "done")
            let notes = ReadField.via "notes" <@ fun (t: Ticket) -> t.Notes @> (List.map (fun n -> n.Text))
            repo.CheckFields [ title; stage; notes ]
            let query = Query.from tableOf<Ticket> |> BuildQuery.orderBy <@ fun t -> t.Title @>

            selected (query |> BuildQuery.select [ title; stage; notes ]) (fun r ->
                r.Get<string> title + " " + r.Get<string> stage + " [" + String.Join("/", r.Get<string list> notes) + "]"))
        step "each owner's first ticket, chosen fields paged per value" (fun () ->
            let title = ReadField.value "title" <@ fun (t: Ticket) -> t.Title @>
            let owner = ReadField.via "owner" <@ fun (t: Ticket) -> t.Owner @> (fun o -> match o with Some(CustomerId n) -> string n | None -> "-")

            let query =
                Query.from tableOf<Ticket>
                |> BuildQuery.orderBy <@ fun t -> t.Title @>
                |> BuildQuery.pageEach <@ fun t -> t.Owner @> 0 1

            selected (query |> BuildQuery.select [ owner; title ]) (fun r -> r.Get<string> owner + " " + r.Get<string> title))
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

        step "a rejection carrying a value type" (fun () ->
            let missing: Tx<Ticket, int> = Tx.one (Queries.ticketById (TicketId 404L)) |> Tx.orReject 404

            match repo.Transact(missing).Result with
            | Ok t -> ticket t
            | Error e -> "failed: " + e.Display)

        step "a rejection carrying a record, a union with fields, an option and a list" (fun () ->
            let refusal: Queries.Refusal =
                { Reason = "busy"
                  Stage = Doing("ada", DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero))
                  Points = Some 3L
                  Sizes = [ 1; 2 ] }

            match repo.Transact(Tx.fail refusal: Tx<unit, Queries.Refusal>).Result with
            | Ok() -> "committed"
            | Error e -> "failed: " + e.Display)

        step "a rejection carrying a union case" (fun () ->
            match repo.Transact(Tx.fail (Done(DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero))): Tx<unit, Stage>).Result with
            | Ok() -> "committed"
            | Error e -> "failed: " + e.Display)

        step "a stored value the domain does not know is named in the error" (fun () ->
            do
                use ds = dataSource cs
                use conn = ds.OpenConnection()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "update tickets set priority = 'urgent' where id = 2"
                cmd.ExecuteNonQuery() |> ignore

            match repo.All(Queries.ticketById (TicketId 2L)).Result with
            | Ok ts -> tickets ts
            | Error e -> "failed: " + refusal e)

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
