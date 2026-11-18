/// A function over tickets: an event names a ticket, and may ask someone to
/// start it; the answer is the ticket as stored, or why not. The connection
/// string is PLASM_CS. With PLASM_INIT=init (the default) the data source,
/// the model, the repository's start (its query check) and the first
/// connection happen before the runtime asks for the first event; with
/// PLASM_INIT=invocation, in the first invocation. Each phase's time goes to
/// standard error, as a function's log.
module Function.Program

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Amazon.Lambda.Core
open Amazon.Lambda.RuntimeSupport
open Npgsql
open Plasm
open Plasm.Postgres

type CustomerId = CustomerId of int64
type TicketId = TicketId of int64
type NoteId = NoteId of int64

type Stage =
    | Todo
    | Doing of assignee: string * since: DateTimeOffset
    | Done of at: DateTimeOffset

[<Table "customers">]
type Customer = { Id: CustomerId; Name: string }

[<Table "notes">]
type Note = { Id: NoteId; Ticket: TicketId; Text: string }

[<Table "tickets">]
type Ticket =
    { Id: TicketId
      Customer: CustomerId
      Title: string
      Stage: Stage
      [<OrderedBy "Id">]
      Notes: Note list
      [<Version>]
      Version: int }

module Queries =
    let ticket = Query.prepare (fun (id: Arg<TicketId>) -> plasm { for t in tableOf<Ticket> do where (t.Id = id.Value) })

    let customer =
        Query.prepare (fun (id: Arg<CustomerId>) -> plasm { for c in tableOf<Customer> do where (c.Id = id.Value) })

type Refused =
    | NoTicket of int64
    | AlreadyStarted of string
    | Finished

let clock = Stopwatch.StartNew()

let log (line: string) = Console.Error.WriteLine line

let phase (name: string) (since: TimeSpan) =
    log (name + " " + (clock.Elapsed - since).TotalMilliseconds.ToString("F1", Globalization.CultureInfo.InvariantCulture) + " ms")
    clock.Elapsed

let open' () : Task<Repo> =
    task {
        let at = clock.Elapsed
        let cs = Environment.GetEnvironmentVariable "PLASM_CS"
        let ds = NpgsqlSlimDataSourceBuilder(cs).EnableArrays().EnableTransportSecurity().Build()
        let model = Model.buildOrFail [] [ typeof<Customer>; typeof<Ticket> ]
        let at = phase "model" at
        let! repo = Repo.Start(model, PostgresProvider ds, typeof<Ticket>.Assembly, { RepoOptions.Default with Schema = StartupCheck.Crash })
        let at = phase "repo start (queries and schema checked, first connection)" at
        let! _ = repo.One(Queries.ticket (TicketId 0L))
        let _ = phase "first read" at
        return repo
    }

let eager = Environment.GetEnvironmentVariable "PLASM_INIT" <> "invocation"
let repo: Lazy<Task<Repo>> = lazy (open' ())

let stage (s: Stage) =
    match s with
    | Todo -> "todo"
    | Doing(who, _) -> "doing by " + who
    | Done _ -> "done"

let start (id: TicketId) (who: string) : Tx<Ticket, Refused> =
    transaction {
        let (TicketId n) = id
        let! t = Tx.one (Queries.ticket id) |> Tx.orReject (NoTicket n)

        match t.Stage with
        | Todo -> return! Tx.update (t, { t with Stage = Doing(who, DateTimeOffset.UtcNow) })
        | Doing(other, _) -> return! Tx.fail (AlreadyStarted other)
        | Done _ -> return! Tx.fail Finished
    }

let answer (repo: Repo) (input: string) : Task<string> =
    task {
        use doc = JsonDocument.Parse input
        let root = doc.RootElement
        let id = TicketId(root.GetProperty("ticket").GetInt64())
        let (TicketId n) = id
        let out = new MemoryStream()
        use w = new Utf8JsonWriter(out)
        w.WriteStartObject()

        let! started =
            match root.TryGetProperty "start" with
            | true, who ->
                task {
                    let! r = repo.Transact(start id (who.GetString()))
                    return Some r
                }
            | _ -> Task.FromResult None

        match started with
        | Some(Error e) ->
            match e.Failure with
            | TxFailure.Rejected(NoTicket n) -> w.WriteString("missing", "ticket " + string n)
            | TxFailure.Rejected(AlreadyStarted who) -> w.WriteString("refused", who + " has started it")
            | TxFailure.Rejected Finished -> w.WriteString("refused", "it is done")
            | other -> w.WriteString("failed", other.ToString())
        | _ ->
            match! repo.One(Queries.ticket id) with
            | Ok(Some t) ->
                let! owner = repo.One(Queries.customer t.Customer)

                w.WriteNumber("ticket", n)
                w.WriteString("title", t.Title)
                w.WriteString("stage", stage t.Stage)
                w.WriteNumber("version", t.Version)
                w.WriteString("customer", (match owner with Ok(Some c) -> c.Name | _ -> "?"))
                w.WriteStartArray "notes"
                for note in t.Notes do w.WriteStringValue note.Text
                w.WriteEndArray()
            | Ok None -> w.WriteString("missing", "ticket " + string n)
            | Error e -> w.WriteString("failed", e.ToString())

        w.WriteEndObject()
        w.Flush()
        return Encoding.UTF8.GetString(out.ToArray())
    }

let handler (input: Stream) (_: ILambdaContext) : Task<Stream> =
    task {
        let at = clock.Elapsed
        use reader = new StreamReader(input)
        let! text = reader.ReadToEndAsync()
        let! r = repo.Value
        let! result = answer r text
        phase "invocation" at |> ignore
        return new MemoryStream(Encoding.UTF8.GetBytes result) :> Stream
    }

[<EntryPoint>]
let main _ =
    log ("to main " + (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds.ToString("F1", Globalization.CultureInfo.InvariantCulture) + " ms")
    if eager then repo.Value.GetAwaiter().GetResult() |> ignore
    use bootstrap = LambdaBootstrapBuilder.Create(Func<Stream, ILambdaContext, Task<Stream>>(handler)).Build()
    bootstrap.RunAsync().GetAwaiter().GetResult()
    0
