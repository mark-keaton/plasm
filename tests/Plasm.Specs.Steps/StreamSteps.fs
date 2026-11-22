namespace Plasm.Specs

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A domain of its own for streaming.feature: people with a place, whose
/// rows the scenarios count, and a provider that counts open connections.
module StreamDomain =

    type PersonId = PersonId of int64

    [<Table "stream_people">]
    type Person = { Id: PersonId; Name: string; Place: int }

    let model = Model.buildOrFail [] [ typeof<Person>; typeof<Order> ]

    let tables =
        "create table stream_people (id bigint generated always as identity primary key, name text not null, place int not null);
         insert into stream_people (name, place) values ('Cal', 3), ('Ann', 1), ('Eve', 5), ('Bea', 2), ('Dee', 4);"

    /// Counts the connections it has handed out that are still open.
    type Counting(inner: IProvider) =
        let mutable open' = 0

        member _.Open = open'

        interface IProvider with
            member _.Dialect = inner.Dialect
            member _.Describe(c, ct) = inner.Describe(c, ct)
            member _.Classify ex = inner.Classify ex
            member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
            member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

            member _.OpenConnection ct =
                task {
                    let! c = inner.OpenConnection ct
                    Interlocked.Increment(&open') |> ignore
                    let mutable closed = false

                    c.StateChange.Add(fun e ->
                        if e.CurrentState = ConnectionState.Closed && not closed then
                            closed <- true
                            Interlocked.Decrement(&open') |> ignore)

                    return c
                }

open StreamDomain

/// Feature: A query's rows can be read as a stream
[<Binding>]
type StreamSteps(w: World) =
    let events = ResizeArray<CommandEvent>()
    let mutable counting: Counting = Unchecked.defaultof<_>
    let mutable repo: Repo = Unchecked.defaultof<_>
    let mutable enumerator: IAsyncEnumerator<Result<obj, DbError>> = null
    let mutable cts: CancellationTokenSource = null
    let seen = ResizeArray<Result<obj, DbError>>()

    let statements () = events |> Seq.filter (fun e -> e.Kind = CommandKind.Statement) |> List.ofSeq

    let byName = plasm { for p in tableOf<Person> do orderBy p.Name }

    /// Begins enumerating `stream`, boxing its rows.
    let start (stream: IAsyncEnumerable<Result<'T, DbError>>) (token: CancellationToken) =
        let inner = stream.GetAsyncEnumerator token

        enumerator <-
            { new IAsyncEnumerator<Result<obj, DbError>> with
                member _.Current = inner.Current |> Result.map box
                member _.MoveNextAsync() = inner.MoveNextAsync()
                member _.DisposeAsync() = inner.DisposeAsync() }

    /// Reads `n` more elements, or to the end when `n` is None.
    let read (n: int option) =
        task {
            let mutable go = true
            let mutable taken = 0

            while go && (n.IsNone || taken < n.Value) do
                let! more = enumerator.MoveNextAsync()

                if more then
                    seen.Add enumerator.Current
                    taken <- taken + 1
                else
                    go <- false
        }

    let finish () =
        task {
            do! read None
            do! enumerator.DisposeAsync()
        }

    let values () =
        seen |> Seq.choose (function Ok v -> Some v | Error _ -> None) |> List.ofSeq

    let name (v: obj) =
        match v with
        | :? Person as p -> p.Name
        | :? string as s -> s
        | other -> string other

    let streamAll (s: IAsyncEnumerable<Result<'T, DbError>>) =
        task {
            start s CancellationToken.None
            do! finish ()
        }

    [<StepDefinition("five customers named Ann, Bea, Cal, Dee and Eve")>]
    member _.People() =
        task {
            do! w.Db.MakeTables(StreamDomain.tables, StreamDomain.model)
            counting <- Counting(w.Db.Provider)
            repo <- Repo(model, counting, { RepoOptions.Default with OnCommand = Some events.Add })
        }
        :> Task

    [<StepDefinition("I stream the customers ordered by name")>]
    member _.StreamByName() = streamAll (repo.Stream byName) :> Task

    [<StepDefinition("^the stream gives Ann, Bea, Cal, Dee and Eve, each as a value$")>]
    member _.GivesFive() =
        check (seen |> Seq.forall Result.isOk) $"expected only values: %A{seen}"
        equal [ "Ann"; "Bea"; "Cal"; "Dee"; "Eve" ] (values () |> List.map name) "the stream"

    [<StepDefinition("^the stream was reported once, with (\\d+) rows$")>]
    member _.ReportedOnce(rows: int) =
        match statements () with
        | [ e ] -> equal rows e.Rows "the rows reported"
        | other -> failwithf "expected one command, got %A" (other |> List.map (fun e -> e.Sql))

    [<StepDefinition("I make a stream of the customers and do not read it")>]
    member _.Unread() =
        let s = repo.Stream byName
        w.Set("unread", s)

    [<StepDefinition("no command was sent")>]
    member _.NoCommand() = equal 0 (statements ()).Length "commands sent"

    [<StepDefinition("no connection is open")>]
    member _.NoneOpen() = equal 0 counting.Open "open connections"

    [<StepDefinition("one connection is open")>]
    member _.OneOpen() = equal 1 counting.Open "open connections"

    [<StepDefinition("I stream the customers ordered by name and read the first row")>]
    member _.First() =
        task {
            start (repo.Stream byName) CancellationToken.None
            do! read (Some 1)
            equal [ "Ann" ] (values () |> List.map name) "the first row"
        }
        :> Task

    [<StepDefinition("the stream has not been reported yet")>]
    member _.NotYet() = equal 0 (statements ()).Length "commands reported"

    [<StepDefinition("I read the rest of the stream")>]
    member _.Rest() =
        task {
            do! finish ()
            equal 5 (values ()).Length "rows read"
        }
        :> Task

    [<StepDefinition("I stream the customers ordered by name, read two rows and stop")>]
    member _.Stop() =
        task {
            start (repo.Stream byName) CancellationToken.None
            do! read (Some 2)
            do! enumerator.DisposeAsync()
        }
        :> Task

    [<StepDefinition("I stream the customers ordered by name, read one row and cancel")>]
    member _.Cancel() =
        task {
            cts <- new CancellationTokenSource()
            start (repo.Stream(byName, cancellationToken = cts.Token)) CancellationToken.None
            do! read (Some 1)
            cts.Cancel()
        }
        :> Task

    [<StepDefinition("asking for the next row raises OperationCanceledException")>]
    member _.Raises() =
        task {
            let mutable raised = false

            try
                let! _ = enumerator.MoveNextAsync()
                ()
            with :? OperationCanceledException ->
                raised <- true

            do! enumerator.DisposeAsync()
            check raised "the next row did not raise OperationCanceledException"
        }
        :> Task

    [<StepDefinition("I stream the customers' names ordered by name")>]
    member _.Names() =
        streamAll (repo.Stream(plasm { for p in tableOf<Person> do orderBy p.Name; select p.Name })) :> Task

    [<StepDefinition("I stream the orders")>]
    member _.Orders() = streamAll (repo.Stream(Query.from tableOf<Order>)) :> Task

    [<StepDefinition("^the stream gives one error, saying Order.Lines is read by another command$")>]
    member _.ListRefused() =
        match List.ofSeq seen with
        | [ Error(DbError.Translation(TranslationError.InvalidQuery m)) ] ->
            check (m.Contains "Order.Lines" && m.Contains "another command") m
        | other -> failwithf "expected one refusal, got %A" other

    [<StepDefinition("I stream the customers filtered by a function SQL cannot express")>]
    member _.Untranslatable() =
        let lucky (n: string) = n.GetHashCode() = 7
        streamAll (repo.Stream(plasm { for p in tableOf<Person> do where (lucky p.Name) })) :> Task

    [<StepDefinition("the stream gives one error, an unsupported expression")>]
    member _.Unsupported() =
        match List.ofSeq seen with
        | [ Error(DbError.Translation(TranslationError.UnsupportedExpression _)) ] -> ()
        | other -> failwithf "expected one unsupported expression, got %A" other

    // Unordered, so the database computes each row as it sends it: an ORDER BY
    // would compute them all, and fail, before the first was sent.
    [<StepDefinition("I stream 100 divided by each customer's place less five, in the order they were written, the third being zero")>]
    member _.Divide() =
        streamAll (repo.Stream(plasm { for p in tableOf<Person> do select (100 / (p.Place - 5)) })) :> Task

    [<StepDefinition("the stream gives two values and then a database error")>]
    member _.ThenDatabase() =
        match List.ofSeq seen with
        | [ Ok _; Ok _; Error(DbError.Database f) ] -> equal (Some "22012") f.SqlState "the failure's SQLSTATE"
        | other -> failwithf "expected two values and a database error, got %A" other

    [<StepDefinition("three order lines whose quantities are 1, 2 and 0")>]
    member _.Lines() =
        task {
            let! _ =
                w.Db.Exec
                    "insert into products (sku, name) values ('s-pen', 'Pen');
                     insert into orders (status, total, placed_at) values ('Draft', 0, now());
                     insert into order_lines (order_id, product_id, quantity, unit_price)
                     select (select max(id) from orders), (select id from products where sku = 's-pen'), q, 1
                     from unnest(array[1, 2, 0]) as q;"

            ()
        }
        :> Task

    [<StepDefinition("I stream the order lines' quantities in the order they were written")>]
    member _.Quantities() =
        streamAll (repo.Stream(plasm { for l in tableOf<OrderLine> do orderBy l.Id; select l.Quantity })) :> Task

    [<StepDefinition("the stream gives two values and then a materialization error naming OrderLine.Quantity")>]
    member _.ThenMaterialization() =
        match List.ofSeq seen with
        | [ Ok _; Ok _; Error(DbError.Materialization m) ] -> equal "OrderLine.Quantity" $"{m.Model}.{m.Property}" "the field"
        | other -> failwithf "expected two values and a materialization error, got %A" other

    [<StepDefinition("a sandbox adds a customer named Fay and streams the customers ordered by name")>]
    member _.Sandboxed() =
        task {
            let! names =
                repo.Sandbox(fun r ->
                    task {
                        let! added = r.Transact(Tx.insert { Id = PersonId 0L; Name = "Fay"; Place = 6 }: Tx<Person, unit>)
                        ok added |> ignore
                        start (r.Stream byName) CancellationToken.None
                        do! finish ()
                        return values () |> List.map name
                    })

            w.Set("sandboxed", names)
        }
        :> Task

    [<StepDefinition("the sandbox's stream gives Ann, Bea, Cal, Dee, Eve and Fay")>]
    member _.SandboxSaw() =
        equal [ "Ann"; "Bea"; "Cal"; "Dee"; "Eve"; "Fay" ] (w.Get<string list> "sandboxed") "inside the sandbox"

    [<StepDefinition("outside the sandbox there are five customers")>]
    member _.Outside() =
        task {
            let! n = repo.Count(Query.from tableOf<Person>)
            equal 5L (ok n) "outside"
        }
        :> Task

    [<StepDefinition("I listen to Plasm's activities and stream the customers ordered by name")>]
    member _.Listened() =
        task {
            let recorded = Collections.Concurrent.ConcurrentQueue<Diagnostics.Activity>()

            use listener =
                new Diagnostics.ActivityListener(
                    ShouldListenTo = (fun s -> s.Name = "Plasm" || s.Name = "Plasm.Specs.Stream"),
                    Sample = (fun _ -> Diagnostics.ActivitySamplingResult.AllDataAndRecorded),
                    ActivityStopped = (fun a -> recorded.Enqueue a)
                )

            Diagnostics.ActivitySource.AddActivityListener listener
            use source = new Diagnostics.ActivitySource("Plasm.Specs.Stream")
            use parent = source.StartActivity("scenario")
            let trace = parent.TraceId
            let plain = Repo(model, counting, RepoOptions.Default)
            do! streamAll (plain.Stream byName)
            w.Set("activities", recorded |> Seq.filter (fun a -> a.Source.Name = "Plasm" && a.TraceId = trace) |> List.ofSeq)
        }
        :> Task

    [<StepDefinition("^one activity was reported for the stream, with (\\d+) rows$")>]
    member _.OneActivity(rows: int) =
        match w.Get<Diagnostics.Activity list> "activities" with
        | [ a ] -> equal (box rows) (a.GetTagItem "db.response.returned_rows") "db.response.returned_rows"
        | other -> failwithf "expected one activity, got %A" (other |> List.map (fun a -> a.DisplayName))
