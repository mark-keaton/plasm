namespace Plasm.Specs

open System
open System.Diagnostics
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// activities.feature: Plasm's activities, recorded by a listener. Scenarios
/// run side by side, so each runs its commands under an activity of its own
/// and keeps only the activities of that trace.
[<Binding>]
type ActivitySteps(w: World) =
    static let scenarios = new ActivitySource("Plasm.Specs")
    let recorded = Collections.Concurrent.ConcurrentQueue<Activity>()
    let mutable listener: ActivityListener = null
    let mutable trace = ActivityTraceId()

    let plain () = Repo(w.Db.Model, w.Db.Provider, RepoOptions.Default)

    /// `work`, under an activity of this scenario's own.
    let traced (work: unit -> Task<'a>) =
        task {
            use parent = scenarios.StartActivity("scenario")
            trace <- parent.TraceId
            return! work ()
        }

    let activities () =
        recorded |> Seq.filter (fun a -> a.Source.Name = "Plasm" && a.TraceId = trace) |> List.ofSeq

    let tag (a: Activity) (name: string) = a.GetTagItem name

    [<AfterScenario>]
    member _.StopListening() =
        if not (isNull listener) then listener.Dispose()

    [<StepDefinition("something listening to Plasm's activities")>]
    member _.Listen() =
        listener <-
            new ActivityListener(
                ShouldListenTo = (fun s -> s.Name = "Plasm" || s.Name = "Plasm.Specs"),
                Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
                ActivityStopped = (fun a -> recorded.Enqueue a)
            )

        ActivitySource.AddActivityListener listener

    [<StepDefinition("^I count the Customers named \"(.*)\" through a repository with no OnCommand$")>]
    member _.Count(name: string) =
        task {
            let! n = traced (fun () -> plain().Count(plasm { for c in tableOf<Customer> do where (c.Name = name) }))
            ok n |> ignore
        }
        :> Task

    [<StepDefinition("^one Client activity is recorded, named \"(.*)\"$")>]
    member _.One(name: string) =
        match activities () with
        | [ a ] ->
            equal name a.DisplayName "the activity's name"
            equal ActivityKind.Client a.Kind "the activity's kind"
        | other -> failwithf "expected one activity, got %A" (other |> List.map (fun a -> a.DisplayName))

    [<StepDefinition("^it is tagged db.system.name \"(.*)\", db.operation.name \"(.*)\", db.collection.name \"(.*)\"$")>]
    member _.Tags(system: string, operation: string, collection: string) =
        let a = activities () |> List.exactlyOne
        equal (box system) (tag a "db.system.name") "db.system.name"
        equal (box operation) (tag a "db.operation.name") "db.operation.name"
        equal (box collection) (tag a "db.collection.name") "db.collection.name"

    [<StepDefinition("^its db.query.text is the statement sent, with \"(.*)\" and not \"(.*)\" in it$")>]
    member _.QueryText(parameter: string, value: string) =
        let text = string (tag (activities () |> List.exactlyOne) "db.query.text")
        check (text.StartsWith "SELECT" && text.Contains parameter && not (text.Contains value)) text

    [<StepDefinition("^its db.response.returned_rows is (\\d+)$")>]
    member _.Rows(n: int) =
        equal (box n) (tag (activities () |> List.exactlyOne) "db.response.returned_rows") "db.response.returned_rows"

    [<StepDefinition("a transaction inserts a Customer through a repository with no OnCommand")>]
    member _.Insert() =
        task {
            let! r = traced (fun () -> plain().Transact(Tx.insert (New.customer "Ada" "ada.traced@example.com"): Tx<_, unit>))
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("^the activities recorded are named \"(.*)\", \"(.*)\" and \"(.*)\", in order$")>]
    member _.Named(a: string, b: string, c: string) =
        equal [ a; b; c ] (activities () |> List.map (fun x -> x.DisplayName)) "activities"

    [<StepDefinition("^a transaction inserts another Customer with the email \"(.*)\" through a repository with no OnCommand$")>]
    member _.Duplicate(email: string) =
        task {
            let! r = traced (fun () -> plain().Transact(Tx.insert (New.customer "Ada again" email): Tx<_, unit>))
            check (Result.isError r) "the duplicate was stored"
        }
        :> Task

    [<StepDefinition("^the INSERT's activity has error.type \"(.*)\" and the status Error$")>]
    member _.Failed(errorType: string) =
        let insert = activities () |> List.find (fun a -> a.DisplayName.StartsWith "INSERT")
        equal (box errorType) (tag insert "error.type") "error.type"
        equal ActivityStatusCode.Error insert.Status "status"

    [<StepDefinition("OnCommand reported a Begin, a Statement and a Commit")>]
    member _.Reported() =
        let events = w.Db.Recorder.All |> List.skip (w.Get<int> "events-from")
        equal [ CommandKind.Begin; CommandKind.Statement; CommandKind.Commit ] (events |> List.map (fun e -> e.Kind)) "kinds"
