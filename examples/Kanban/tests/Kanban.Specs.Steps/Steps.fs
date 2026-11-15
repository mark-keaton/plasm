namespace Kanban.Specs

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Reqnroll
open Plasm
open Kanban
open Kanban.Api
open Kanban.Server

/// Feature: A kanban board with an F# domain at its centre
[<Binding>]
type KanbanSteps(w: World, d: Driver) =

    let call request = d.Call request
    let remember slug card = d.Remember(slug, card)
    let board slug = d.Board slug
    let current title = d.Current title
    let reference title = d.Reference title
    let add title slug = d.Add(title, slug)

    let start (person: string) (title: string) =
        task {
            let! ref = reference title
            return! call ((w.ApiAs(Some person)).start ref)
        }

    let refusal () =
        match w.Last with
        | Error(Rejected refusal) -> DomainError.describe refusal
        | other -> failwithf "expected a refusal by the domain, got %A" other

    // -- Boards -----------------------------------------------------------------

    [<Given("the board {string} allows {int} cards in progress")>]
    member _.GivenBoard(slug: string, limit: int) : Task =
        task {
            let! created = call (w.Api.createBoard { Slug = slug; Name = slug; WipLimit = limit; Cards = [||] })
            ok created |> ignore
        }

    [<When("a board {string} named {string} is created allowing {int} cards in progress")>]
    member _.CreateBoard(slug: string, name: string, limit: int) : Task =
        call (w.Api.createBoard { Slug = slug; Name = name; WipLimit = limit; Cards = [||] })

    [<When("the board {string} is read")>]
    member _.ReadBoard(slug: string) : Task = call (w.Api.board slug)

    [<When("the boards are listed")>]
    member _.ListBoards() : Task = call (w.Api.boards ())

    [<Then("the board {string} is listed with {int} in the backlog, {int} in progress and {int} done")>]
    member _.ThenListed(slug: string, backlog: int, doing: int, isDone: int) =
        match w.Last with
        | Ok(:? (BoardSummary[]) as boards) ->
            let b = boards |> Array.find (fun b -> b.Slug = slug)
            equal (backlog, doing, isDone) (b.Backlog, b.Doing, b.Done) "cards by stage"
        | other -> failwithf "expected a list of boards, got %A" other

    [<Then("the board {string} has the cards {string}")>]
    member _.ThenBoardHas(slug: string, titles: string) : Task =
        task {
            let! view = board slug
            equal (names titles) [ for c in view.Cards -> c.Title ] "the board's cards"
        }

    [<Then("the board {string} is not found")>]
    member _.ThenNotFound(slug: string) =
        equal (Error(NotFound $"board {slug}")) w.Last "the outcome"

    // -- Cards ------------------------------------------------------------------

    [<Given("the card {string} on {string}")>]
    member _.GivenCard(title: string, slug: string) : Task =
        task {
            let! card = add title slug
            ok card |> ignore
        }

    [<When("a card {string} is added to {string}")>]
    member _.AddCard(title: string, slug: string) : Task = add title slug

    [<Given("""^(\w+) is working on "(.*)" on "(.*)"$""")>]
    member _.GivenWorking(person: string, title: string, slug: string) : Task =
        task {
            let! card = add title slug
            ok card |> ignore
            let! started = start person title
            ok started |> ignore
        }

    [<StepDefinition("""^(\w+) starts "([^"]*)"$""")>]
    member _.Start(person: string, title: string) : Task = start person title

    [<StepDefinition("{string} is shipped")>]
    member _.Ship(title: string) : Task =
        task {
            let! ref = reference title
            return! call (w.Api.ship ref)
        }

    [<StepDefinition("{string} is dropped because {string}")>]
    member _.Drop(title: string, reason: string) : Task =
        task {
            let! ref = reference title
            return! call (w.Api.drop { Ref = ref; Reason = reason })
        }

    [<StepDefinition("{string} is sent back")>]
    member _.SendBack(title: string) : Task =
        task {
            let! ref = reference title
            return! call (w.Api.sendBack ref)
        }

    [<StepDefinition("{string} is reopened")>]
    member _.Reopen(title: string) : Task =
        task {
            let! ref = reference title
            return! call (w.Api.reopen ref)
        }

    [<StepDefinition("{string} is renamed {string}")>]
    member _.Rename(title: string, renamed: string) : Task =
        task {
            let! ref = reference title
            let! card = call (w.Api.rename { Ref = ref; Title = renamed })
            remember (fst w.Cards.[title]) card
        }

    [<Given("""^the time is (\d{4}-\d\d-\d\d \d\d:\d\d)$""")>]
    member _.GivenTime(at: string) = w.Now <- time at

    [<Then("the card {string} is in the backlog")>]
    member _.ThenBacklog(title: string) : Task =
        task {
            let! card = current title
            equal InBacklog card.Stage "the card's stage"
        }

    [<Then("""^the card "(.*)" is in progress for (\w+)$""")>]
    member _.ThenInProgress(title: string, person: string) : Task =
        task {
            let! card = current title

            match card.Stage with
            | InProgress(assignee, _) -> equal person assignee "the assignee"
            | other -> failwithf "expected the card in progress, but it is %A" other
        }

    [<Then("""^the card "(.*)" is in progress for (\w+) since (\d{4}-\d\d-\d\d \d\d:\d\d)$""")>]
    member _.ThenInProgressSince(title: string, person: string, since: string) : Task =
        task {
            let! card = current title
            equal (InProgress(person, time since)) card.Stage "the card's stage"
        }

    [<Then("""^the card "(.*)" was shipped at (\d{4}-\d\d-\d\d \d\d:\d\d)$""")>]
    member _.ThenShipped(title: string, at: string) : Task =
        task {
            let! card = current title
            equal (WasShipped(time at)) card.Stage "the card's stage"
        }

    [<Then("""^the card "(.*)" was dropped at (\d{4}-\d\d-\d\d \d\d:\d\d) because "(.*)"$""")>]
    member _.ThenDropped(title: string, at: string, reason: string) : Task =
        task {
            let! card = current title
            equal (WasDropped(reason, time at)) card.Stage "the card's stage"
        }

    // -- Outcomes ---------------------------------------------------------------

    [<Then("the request is invalid for the fields {string}")>]
    member _.ThenInvalidFields(fields: string) =
        match w.Last with
        | Error(Invalid errors) -> equal (names fields) [ for field, _ in errors -> field ] "the invalid fields"
        | other -> failwithf "expected invalid input, got %A" other

    [<Then("the request is invalid for the field {string}: {string}")>]
    member _.ThenInvalidField(field: string, message: string) =
        equal (Error(Invalid [| field, message |])) w.Last "the outcome"

    [<Then("the request is refused: {string}")>]
    member _.ThenRefused(message: string) = equal message (refusal ()) "the refusal"

    [<Then("the request is stale")>]
    member _.ThenStale() = equal (Error Stale) w.Last "the outcome"

    [<Then("the request fails as unavailable")>]
    member _.ThenUnavailable() =
        match w.Last with
        | Error(Unavailable _) -> ()
        | other -> failwithf "expected the request to fail as unavailable, got %A" other

    // -- Stored facts that contradict each other ---------------------------------

    [<Given("the database loses the assignee of {string}")>]
    member _.GivenCorrupted(title: string) : Task =
        task {
            let _, id = w.Cards.[title]
            // Inside the scenario's sandbox, so the constraint is back when it ends.
            let behindTheApplication: Tx<int, unit> =
                transaction {
                    let! _ = Tx.executeSql $"alter table cards drop constraint cards_stage_check"
                    return! Tx.executeSql $"update cards set stage_assignee = null where id = {id}"
                }

            let! updated = w.Repo.Transact behindTheApplication
            equal 1 (ok updated) "rows changed"
        }

    // -- Two people at the same moment ------------------------------------------

    [<When("""^(\w+) starts "(.*)" and (\w+) starts "(.*)" at the same moment, (\d+) times over$""")>]
    member _.Race(first: string, left: string, second: string, right: string, times: int) : Task =
        task {
            let races = ResizeArray()

            for _ in 1..times do
                let! leftRef = reference left
                let! rightRef = reference right
                let a = Async.StartAsTask((w.ApiAs(Some first)).start leftRef)
                let b = Async.StartAsTask((w.ApiAs(Some second)).start rightRef)
                let! a = a
                let! b = b
                races.Add((a, b))

                // Put the winner back, so the next round starts the same way.
                for title, outcome in [ left, a; right, b ] do
                    match outcome with
                    | Ok card ->
                        let! back = Async.StartAsTask(w.Api.sendBack { Card = card.Id; Version = card.Version })
                        ok back |> ignore
                    | Error _ -> ignore title

            w.Races <- List.ofSeq races
        }

    [<Then("every time exactly one of them succeeded")>]
    member _.ThenOneWinner() =
        for a, b in w.Races do
            match a, b with
            | Ok _, Error _
            | Error _, Ok _ -> ()
            | other -> failwithf "expected exactly one to succeed, got %A" other

    [<Then("every time the other was refused: {string}")>]
    member _.ThenLoserRefused(message: string) =
        for a, b in w.Races do
            match a, b with
            | Error(Rejected refusal), Ok _
            | Ok _, Error(Rejected refusal) -> equal message (DomainError.describe refusal) "the refusal"
            | other -> failwithf "expected one refusal by the domain, got %A" other

    // -- Old copies --------------------------------------------------------------

    [<Given("""^(\w+) has an old copy of "(.*)"$""")>]
    member _.GivenCopy(person: string, title: string) : Task =
        task {
            let! ref = reference title
            w.Copies.[person] <- ref
        }

    [<When("""^(\w+) renames (?:her|his|their) copy "(.*)"$""")>]
    member _.RenameCopy(person: string, renamed: string) : Task =
        call (w.Api.rename { Ref = w.Copies.[person]; Title = renamed })

    // -- Comments ----------------------------------------------------------------

    [<StepDefinition("""^(\w+) comments "(.*)" on "(.*)"$""")>]
    member _.Comment(person: string, body: string, title: string) : Task =
        call ((w.ApiAs(Some person)).comment { Card = snd w.Cards.[title]; Body = body })

    [<Then("the card {string} has the comments {string}")>]
    member _.ThenComments(title: string, comments: string) : Task =
        task {
            let! card = current title
            equal (names comments) [ for c in card.Comments -> $"{c.Author}: {c.Body}" ] "the card's comments"
        }

    // -- The application ----------------------------------------------------------

    member private _.StartApplication(connectionString: string) : Task =
        task {
            try
                let! app =
                    Host.build [||] (fun builder ->
                        builder.WebHost.UseTestServer() |> ignore
                        builder.Configuration.["ConnectionStrings:Kanban"] <- connectionString)

                do! app.StartAsync()
                w.App <- app
                w.AfterScenario <- (fun () -> app.DisposeAsync().AsTask()) :: w.AfterScenario
            with e ->
                w.StartFailure <- Some e
        }

    [<Given("the application is running on its own database")>]
    member this.GivenRunning() : Task =
        task {
            // Empty: the application migrates it as it starts.
            let! connectionString = Databases.empty ()
            do! this.StartApplication connectionString

            match w.StartFailure with
            | Some e -> raise e
            | None -> ()
        }

    [<When("a browser asks for the boards over HTTP")>]
    member _.AskOverHttp() : Task =
        task {
            let client = w.App.GetTestClient()
            let! response = client.GetAsync(Route.builder "IKanbanApi" "boards")
            w.Http <- response
        }

    [<Then("the answer is an empty list")>]
    member _.ThenEmptyList() : Task =
        task {
            let! body = w.Http.Content.ReadAsStringAsync()
            equal (200, """{"Ok":[]}""") (int w.Http.StatusCode, body) "the HTTP answer"
        }

    member private _.Post(method: string, json: string, cookie: string) : Task =
        task {
            let request = new HttpRequestMessage(HttpMethod.Post, Route.builder "IKanbanApi" method)
            request.Content <- new StringContent(json, Text.Encoding.UTF8, "application/json")

            if cookie <> "" then
                request.Headers.Add("Cookie", cookie)

            let! response = w.App.GetTestClient().SendAsync request
            w.Http <- response
        }

    [<Given("a browser that registered as {string} with the password {string}")>]
    member this.GivenRegisteredBrowser(name: string, password: string) : Task =
        task {
            do! this.Post("register", $"""[{{"Name":"{name}","Password":"{password}"}}]""", "")

            match w.Http.Headers.TryGetValues "Set-Cookie" with
            | true, cookies -> w.Cookie <- Seq.head cookies
            | _ -> failwith "registering set no cookie"
        }

    [<When("that browser creates the board {string} over HTTP")>]
    member this.CreateWithCookie(slug: string) : Task =
        // A browser sends back the cookie's name and value, not its attributes.
        this.Post("createBoard", $"""[{{"Slug":"{slug}","Name":"{slug}","WipLimit":2,"Cards":[]}}]""", w.Cookie.Split(';').[0])

    [<When("a browser that never signed in creates the board {string} over HTTP")>]
    member this.CreateWithoutCookie(slug: string) : Task =
        this.Post("createBoard", $"""[{{"Slug":"{slug}","Name":"{slug}","WipLimit":2,"Cards":[]}}]""", "")

    [<Then("the answer is the board {string}")>]
    member _.ThenBoardAnswer(slug: string) : Task =
        task {
            let! body = w.Http.Content.ReadAsStringAsync()

            if not (body.StartsWith $"""{{"Ok":{{"Slug":"{slug}",""") then
                failwithf "expected the board %s, got %s" slug body
        }

    [<Then("the cookie cannot be read by scripts")>]
    member _.ThenHttpOnly() =
        let attributes = w.Cookie.ToLowerInvariant()

        if not (attributes.Contains "httponly" && attributes.Contains "samesite=strict") then
            failwithf "expected an HttpOnly, SameSite=Strict cookie, got %s" w.Cookie

    [<Then("the answer is to sign in")>]
    member _.ThenSignInAnswer() : Task =
        task {
            let! body = w.Http.Content.ReadAsStringAsync()
            equal """{"Error":"SignInRequired"}""" body "the HTTP answer"
        }

    [<Given("a database where the column {string} is missing")>]
    member _.GivenDrift(column: string) : Task =
        task {
            let! connectionString = Databases.migrated ()
            let parts = column.Split '.'
            do! Databases.execute connectionString $"alter table {parts.[0]} drop column {parts.[1]} cascade"
            w.ConnectionString <- connectionString
        }

    [<When("the application starts")>]
    member this.WhenStarts() : Task = this.StartApplication w.ConnectionString

    [<Then("it fails to start, naming {string}")>]
    member _.ThenFailsToStart(column: string) =
        match w.StartFailure with
        | Some(Host.SchemaMismatch differences) ->
            if not (differences |> List.exists (fun d -> d.Contains column)) then
                failwithf "expected a difference naming %s, got %A" column differences
        | other -> failwithf "expected the application to refuse to start, got %A" other
