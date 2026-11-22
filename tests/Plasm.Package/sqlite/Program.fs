/// Plasm.Sqlite from its package alone (tests/Plasm.Package/run.sh): a
/// SQLite file made from the model's DDL, one record written with a decimal,
/// a time, a list and a document, and read back, under the runtime and as a
/// native binary. The two must print the same.
module SqliteConsumer.Program

open System
open System.Threading
open Plasm
open Plasm.Sqlite

type NoteId = NoteId of int64

type Detail = { Kind: string; Weight: decimal }

[<Table "notes">]
type Note =
    { Id: NoteId
      Amount: decimal
      At: DateTime
      Tags: string list
      [<Json>]
      Detail: Detail }

let all = plasm { for n in tableOf<Note> do select n }

let text (d: decimal) = d.ToString(Globalization.CultureInfo.InvariantCulture)

[<EntryPoint>]
let main argv =
    try
        let provider = Sqlite.connect ("Data Source=" + argv.[0])
        let model = Model.buildOrFail [] [ typeof<Note> ]
        let repo = Repo.Start(model, provider, typeof<Note>.Assembly, RepoOptions.Unchecked).Result

        do
            use conn = (provider :> IProvider).OpenConnection(CancellationToken.None).Result
            use cmd = conn.CreateCommand()
            cmd.CommandText <- repo.Ddl()
            cmd.ExecuteNonQueryAsync().Result |> ignore

        Console.WriteLine "tables: made from the Ddl"

        match repo.CheckSchema().Result with
        | Ok [] -> Console.WriteLine "schema: the tables are the model's"
        | Ok problems -> Console.WriteLine("schema: " + string problems.Length + " problem(s)")
        | Error e -> Console.WriteLine("schema: " + string e)

        let note =
            { Id = NoteId 0L
              Amount = 12345678901234567.89m
              At = DateTime(2026, 11, 21, 9, 30, 15, 250, DateTimeKind.Utc)
              Tags = [ "first"; "second" ]
              Detail = { Kind = "parcel"; Weight = 0.125m } }

        match repo.Transact(Tx.insert note: Tx<_, unit>).Result with
        | Ok _ -> Console.WriteLine "write: one note"
        | Error e -> Console.WriteLine("write: failed: " + e.Display)

        match repo.All(all).Result with
        | Ok notes ->
            for n in notes do
                let (NoteId id) = n.Id
                Console.WriteLine(
                    "read: note " + string id + ", amount " + text n.Amount
                    + ", at " + n.At.ToString("o") + " (" + string n.At.Kind + ")"
                    + ", tags " + String.Join("|", n.Tags)
                    + ", detail " + n.Detail.Kind + " " + text n.Detail.Weight)

            Console.WriteLine(if notes = [ { note with Id = notes.Head.Id } ] then "read: as written" else "read: not as written")
        | Error e -> Console.WriteLine("read: failed: " + string e)

        0
    with ex ->
        let rec inner (x: exn) = if isNull x.InnerException then x else inner x.InnerException
        Console.WriteLine("refused: " + inner(ex).Message)
        1
