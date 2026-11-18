/// The native-AOT check (docs/aot.md): each step prints one line, so the
/// runtime's output and the native binary's can be compared line by line.
/// Nothing here formats with printf: it is not available under AOT.
module Plasm.Aot.Program

open System
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.Aot.Shop

/// The application's own records and unions, beside its queries, as an
/// application declares them.
type TicketId = TicketId of int64

type Priority =
    | Low
    | High

type Stage =
    | Todo
    | Doing of who: string * since: DateTimeOffset
    | Done of at: DateTimeOffset

[<Table "tickets">]
type Ticket =
    { Id: TicketId
      Title: string
      Stage: Stage
      Due: DateOnly option
      Points: int64 option
      Priority: Priority option }

/// A row a query returns.
type TicketLine = { Title: string; Points: int64 option }

/// An API contract, as a web application declares one beside its records:
/// functions, which nothing stores.
type TicketApi = { getTicket: int64 -> Async<Ticket option> }

let schema =
    "drop table if exists customers; drop table if exists tickets;
     create table customers (
       id bigint generated always as identity primary key,
       name text not null,
       email text not null constraint customers_email_key unique);
     create table tickets (
       id bigint generated always as identity primary key,
       title text not null,
       stage text not null,
       stage_who text,
       stage_since timestamptz,
       stage_at timestamptz,
       due date,
       points bigint,
       priority text);"

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

/// The application's own word, readable in a query.
[<ReflectedDefinition>]
let named (name: string) (c: Customer) = c.Name = name

let customer (c: Customer) =
    let (CustomerId id) = c.Id
    "Customer " + string id + " " + c.Name + " <" + c.Email + ">"

[<EntryPoint>]
let main argv =
    let cs = argv.[0]

    do
        use conn = new NpgsqlConnection(cs)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

    // Which reader read the unions: not compared, but asserted by run.sh.
    say (
        "reader: "
        + match Shape.reader () with
          | UnionReader.FSharpReflection -> "FSharpReflection"
          | UnionReader.CompiledStructure -> "CompiledStructure"
    )

    let mutable model = None

    step "model" (fun () ->
        let m = Model.buildOrFail [] [ typeof<Customer>; typeof<Ticket> ]
        model <- Some m
        "built, " + string m.Types.Length + " records")

    match model with
    | None -> 1
    | Some m ->
        let repo = Repo.Start(m, Postgres.connect cs, typeof<Customer>.Assembly, RepoOptions.Unchecked).Result
        let mutable ada = None

        step "insert" (fun () ->
            match repo.Transact(Tx.insert { Id = CustomerId 0L; Name = "Ada"; Email = "ada@example.com" }: Tx<_, unit>).Result with
            | Ok c ->
                ada <- Some c
                customer c
            | Error e -> "failed: " + e.Display)

        let read (q: Query<Customer>) =
            match repo.All(q).Result with
            | Ok cs -> String.Join("; ", cs |> List.map customer)
            | Error e -> "failed: " + string e

        step "read by typed id" (fun () ->
            let id = ada.Value.Id
            read (plasm { for c in tableOf<Customer> do where (c.Id = id) }))

        step "read by a reflected member" (fun () -> read (plasm { for c in tableOf<Customer> do where c.AtExample }))
        step "read by a reflected function" (fun () -> read (plasm { for c in tableOf<Customer> do where (named "Ada" c) }))

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
            "Ticket " + string id + " " + t.Title + ", " + stage + ", " + due + ", " + points + ", " + priority

        step "insert an application's record with a union of fields" (fun () ->
            let t = { Id = TicketId 0L; Title = "ship"; Stage = Doing("ada", since); Due = Some(DateOnly(2026, 12, 1)); Points = Some 3L; Priority = Some High }

            match repo.Transact(Tx.insert t: Tx<_, unit>).Result with
            | Ok t -> ticket t
            | Error e -> "failed: " + e.Display)

        step "read it by its case" (fun () ->
            match repo.All(plasm { for t in tableOf<Ticket> do where (t.Stage.IsDoing) }).Result with
            | Ok ts -> String.Join("; ", ts |> List.map ticket)
            | Error e -> "failed: " + string e)

        step "select a row record" (fun () ->
            match repo.All(plasm { for t in tableOf<Ticket> do select { Title = t.Title; Points = t.Points } }).Result with
            | Ok rows -> String.Join("; ", rows |> List.map (fun (r: TicketLine) -> r.Title + " " + (match r.Points with Some p -> string p | None -> "-")))
            | Error e -> "failed: " + string e)

        0
