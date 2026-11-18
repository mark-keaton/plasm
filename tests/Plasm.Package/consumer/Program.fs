/// Reads the fields of F# unions as Plasm does, under the runtime and as a
/// native binary: building the model reads a typed id's field, and a query
/// over a union with fields is translated (no database is needed for either).
module Consumer.Program

open System
open Microsoft.FSharp.Reflection
open Plasm
open Plasm.Postgres

type TicketId = TicketId of int64

type Stage =
    | Todo
    | Doing of assignee: string * since: DateTimeOffset

[<Table "tickets">]
type Ticket = { Id: TicketId; Title: string; Stage: Stage }

let doing = plasm { for t in tableOf<Ticket> do where (t.Id = TicketId 7L && t.Stage.IsDoing) }

[<EntryPoint>]
let main _ =
    try
        let fields = FSharpType.GetUnionCases(typeof<TicketId>).[0].GetFields()
        Console.WriteLine("TicketId's case fields: " + String.Join(", ", fields |> Array.map (fun f -> f.Name)))
        let model = Model.buildOrFail [] [ typeof<Ticket> ]
        Console.WriteLine "model: built"
        let repo = Repo.Start(model, Postgres.connect "Host=localhost", typeof<Ticket>.Assembly, RepoOptions.Unchecked).Result

        match repo.Explain doing with
        | Ok plan -> Console.WriteLine("query: " + plan.Sql.Replace("\n", " "))
        | Error e -> Console.WriteLine("query: refused: " + string e)

        0
    with ex ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        Console.WriteLine("refused: " + inner(ex).Message)
        1
