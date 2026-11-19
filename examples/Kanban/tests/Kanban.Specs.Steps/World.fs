namespace Kanban.Specs

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Reqnroll
open Plasm
open Kanban.Api
open Kanban.Server

/// The application logs what it does not tell the client. A scenario that
/// fails on "unavailable" shows the reason in its output.
type ErrorsToTheTestOutput() =
    interface Microsoft.Extensions.Logging.ILogger with
        member _.BeginScope(_state) = null
        member _.IsEnabled(level) = level >= Microsoft.Extensions.Logging.LogLevel.Error

        member _.Log(level, _id, state, error, formatter) =
            if level >= Microsoft.Extensions.Logging.LogLevel.Error then
                eprintfn "application error: %s" (formatter.Invoke(state, error))

/// Per-scenario state, injected into the binding classes by Reqnroll.
type World() =
    member val Repo: Repo = Unchecked.defaultof<Repo> with get, set
    member val ConnectionString = "" with get, set
    member val Now = DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero) with get, set

    /// Who is signed in. Most scenarios are not about that, so someone is.
    member val Who: string option = Some "Ada" with get, set

    /// How many commands had been sent when the scenario's last request began.
    member val Mark = 0 with get, set

    member val Pages: CardPage list = [] with get, set
    member val Cookie = "" with get, set

    /// The outcome of the scenario's last request.
    member val Last: Result<obj, ApiError> = Ok(box ()) with get, set

    /// Card title -> (board slug, card id).
    member val Cards = Dictionary<string, string * Guid>() with get

    /// Person -> the copy of a card they hold.
    member val Copies = Dictionary<string, CardRef>() with get

    member val Races: (Result<CardView, ApiError> * Result<CardView, ApiError>) list = [] with get, set
    member val App: WebApplication = null with get, set
    member val Http: HttpResponseMessage = null with get, set
    member val StartFailure: exn option = None with get, set
    member val AfterScenario: (unit -> Task) list = [] with get, set

    /// The databases made for this scenario alone, dropped when it ends.
    member val Databases: string list = [] with get, set

    /// The application's API over this scenario's repository and clock, as
    /// used by `person` (or by nobody).
    member w.ApiAs(person: string option) : IKanbanApi =
        let who () =
            person
            |> Option.map (fun name ->
                match Kanban.Person.create name with
                | Ok p -> p
                | Error e -> failwithf "%s is not a person's name: %s" name e)

        // What the database is sent is counted from here. The API's functions
        // start their work when they are called, which is before `Driver.Call`
        // is given the request, so the mark cannot be taken there.
        w.Mark <- Kanban.Specs.Databases.commands.Count

        Service.create
            { Repo = w.Repo
              // The time a step set, then a microsecond later for each request
              // after it: no two requests happen at the same moment unless a
              // scenario says so.
              Clock =
                fun () ->
                    let now = w.Now
                    w.Now <- now.AddTicks 10L
                    now
              NewId = Guid.NewGuid
              Log = ErrorsToTheTestOutput() }
            { Who = who
              SignIn =
                fun p ->
                    w.Who <- Some p.Value
                    Task.CompletedTask
              SignOut =
                fun () ->
                    w.Who <- None
                    Task.CompletedTask }
            CancellationToken.None

    /// The API as used by whoever is signed in.
    member w.Api: IKanbanApi =
        // Signing in and out change who that is while the API is in use.
        let current: IKanbanApi = w.ApiAs w.Who
        current

    /// The API for a read the steps make for themselves (to learn a card's
    /// id, say): what the database is sent keeps being counted from the
    /// scenario's own last request.
    member w.Unmarked: IKanbanApi =
        let mark = w.Mark
        let api = w.Api
        w.Mark <- mark
        api

    /// The commands sent since the API was last asked for (see `ApiAs`).
    member w.Sent = Kanban.Specs.Databases.commands.ToArray() |> Array.skip w.Mark |> List.ofArray

[<AutoOpen>]
module Support =

    let ok (r: Result<'a, 'e>) =
        match r with
        | Ok v -> v
        | Error e -> failwithf "unexpected error: %A" e

    let equal (expected: 'a) (actual: 'a) (what: string) =
        if expected <> actual then
            failwithf "%s: expected %A but was %A" what expected actual

    let time (text: string) =
        DateTimeOffset.ParseExact(text, "yyyy-MM-dd HH:mm", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal)

    let names (text: string) =
        text.Split(',', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    /// As `names`, keeping the empty ones: "a, , b" is three entries, "" none.
    let entries (text: string) =
        if text = "" then [||] else text.Split(',', StringSplitOptions.TrimEntries)

/// What every step class does with the API: make a request, remember its
/// outcome, find a card.
type Driver(w: World) =

    member _.Call(request: Async<Result<'T, ApiError>>) : Task<Result<'T, ApiError>> =
        task {
            let! result = Async.StartAsTask request
            w.Last <- result |> Result.map box
            return result
        }

    member _.Remember(slug: string, card: Result<CardView, ApiError>) =
        match card with
        | Ok view -> w.Cards.[view.Title] <- (slug, view.Id)
        | Error _ -> ()

    member _.Board(slug: string) : Task<BoardView> =
        task {
            let! view = Async.StartAsTask(w.Unmarked.board slug)
            return ok view
        }

    /// The card as it is stored now.
    member d.Current(title: string) : Task<CardView> =
        task {
            let slug, id = w.Cards.[title]
            let! view = d.Board slug
            return view.Cards |> Array.find (fun c -> c.Id = id)
        }

    member d.Reference(title: string) : Task<CardRef> =
        task {
            let! card = d.Current title
            return { Card = card.Id; Version = card.Version }
        }

    member d.Add(title: string, slug: string) : Task<Result<CardView, ApiError>> =
        task {
            let! card = d.Call(w.Api.addCard { Board = slug; Title = title })
            d.Remember(slug, card)
            return card
        }

    /// A number read from the database, outside the API.
    member _.Count(sql: FormattableString) : Task<int64> =
        task {
            let! rows = w.Repo.Sql<int64>(sql)
            return ok rows |> List.exactlyOne
        }

/// Gives each scenario its database: a sandbox on the shared one (rolled back
/// when the scenario ends), or a database of its own for scenarios that need
/// committed data or start the whole application.
[<Binding>]
type Hooks(w: World, scenario: ScenarioContext) =

    let tagged (tag: string) = scenario.ScenarioInfo.Tags |> Array.contains tag

    [<BeforeScenario(Order = 0)>]
    member _.Before() : Task =
        task {
            if tagged "own-database" then
                // The scenario prepares the database itself.
                ()
            elif tagged "committed" then
                let! connectionString = Databases.migrated ()
                w.Databases <- connectionString :: w.Databases
                w.ConnectionString <- connectionString
                let! repo = Databases.repoFor connectionString
                w.Repo <- repo
            else
                let! _, shared = Databases.shared.Value
                // Repo.Sandbox takes the work as a function. A scenario's steps
                // are separate methods, so the function waits until the scenario
                // has ended, and the steps use the repository it was given.
                let opened = TaskCompletionSource<Repo>()
                let ended = TaskCompletionSource()

                let sandbox =
                    shared.Sandbox(fun repo ->
                        task {
                            opened.SetResult repo
                            do! ended.Task
                        })

                let! repo = opened.Task
                w.Repo <- repo

                w.AfterScenario <-
                    (fun () ->
                        task {
                            ended.SetResult()
                            do! sandbox
                        }
                        :> Task)
                    :: w.AfterScenario
        }

    [<AfterScenario>]
    member _.After() : Task =
        task {
            for finish in w.AfterScenario do
                do! finish ()
        }

    /// After every other hook, so that what used a database has stopped.
    [<AfterScenario(Order = 100000)>]
    member _.DropDatabases() : Task =
        task {
            for connectionString in w.Databases do
                do! Databases.drop connectionString
        }

    [<AfterTestRun>]
    static member DropShared() : Task =
        task {
            if Databases.shared.IsValueCreated then
                let! connectionString, _ = Databases.shared.Value
                do! Databases.drop connectionString
        }
