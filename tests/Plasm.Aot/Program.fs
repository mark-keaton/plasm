/// The native-AOT check (docs/aot.md): each step prints one line, so the
/// runtime's output and the native binary's can be compared line by line.
/// Nothing here formats with printf: it is not available under AOT.
module Plasm.Aot.Program

open System
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.Aot.Shop

let schema =
    "drop table if exists customers;
     create table customers (
       id bigint generated always as identity primary key,
       name text not null,
       email text not null constraint customers_email_key unique);"

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

    let mutable model = None

    step "model" (fun () ->
        let m = Model.buildOrFail [] [ typeof<Customer> ]
        model <- Some m
        "built, " + string m.Types.Length + " record")

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

        0
