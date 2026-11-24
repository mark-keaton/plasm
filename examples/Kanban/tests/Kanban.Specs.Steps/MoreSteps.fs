namespace Kanban.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Kanban
open Kanban.Api

/// Boards made with cards, bulk changes, work across boards, and pages.
[<Binding>]
type BoardSteps(w: World, d: Driver) =

    let sent (prefix: string) =
        w.Sent |> List.filter (fun sql -> sql.StartsWith(prefix, StringComparison.Ordinal)) |> List.length

    let work (api: IKanbanApi) : Task = d.Call(api.myWork ())

    let page (slug: string) (number: int) (size: int) =
        d.Call(w.Api.finished { Board = slug; Page = number; Size = size })

    // -- A board with its first cards --------------------------------------------

    [<When("a board {string} named {string} is created allowing {int} cards in progress, with the cards {string}")>]
    member _.CreateWithCards(slug: string, name: string, limit: int, titles: string) : Task =
        task {
            let! created = d.Call(w.Api.createBoard { Slug = slug; Name = name; WipLimit = limit; Cards = entries titles })

            // So that later steps can name the cards.
            match created with
            | Ok _ ->
                let! view = d.Board slug

                for card in view.Cards do
                    w.Cards.[card.Title] <- (slug, card.Id)
            | Error _ -> ()
        }

    [<Then("no board {string} is stored")>]
    member _.ThenNoBoard(slug: string) : Task =
        task {
            let! n = d.Count $"select count(*) from boards where slug = {slug}"
            equal 0L n "boards stored"
        }

    [<Then("no card {string} is stored")>]
    member _.ThenNoCard(title: string) : Task =
        task {
            let! n = d.Count $"select count(*) from cards where title = {title}"
            equal 0L n "cards stored"
        }

    // -- What the database was sent ------------------------------------------------

    [<Then("the database was sent {int} INSERT into {string} and {int} into {string}")>]
    member _.ThenInserts(first: int, firstTable: string, second: int, secondTable: string) =
        equal (first, second) (sent $"INSERT INTO \"{firstTable}\"", sent $"INSERT INTO \"{secondTable}\"") "INSERT statements"

    [<Then("the database was sent {int} UPDATE of {string}")>]
    member _.ThenUpdates(count: int, table: string) = equal count (sent $"UPDATE \"{table}\"") "UPDATE statements"

    [<Then("the database was sent {int} SELECT")>]
    member _.ThenSelects(count: int) =
        // A request that failed before reaching the database sent nothing;
        // say that, not "expected 1 but was 0".
        match w.Last with
        | Error problem -> failwithf "the request failed, so what it sent says nothing: %A" problem
        | Ok _ ->
            if sent "SELECT" <> count then
                failwithf "SELECT statements: expected %d but was %d; mark %d of %d; sent since the request began:\n%s" count (sent "SELECT") w.Mark Kanban.Specs.Databases.commands.Count (String.concat "\n" w.Sent)

    // -- Everything in progress, at once -------------------------------------------

    [<StepDefinition("everything in progress on {string} is sent back")>]
    member _.SendAllBack(slug: string) : Task = d.Call(w.Api.sendAllBack slug)

    [<Then("{int} cards were sent back")>]
    member _.ThenSentBack(count: int) = equal (Ok(box count)) w.Last "cards sent back"

    // -- Work across boards --------------------------------------------------------

    [<When("""^(\w+) asks what (?:she|he) is working on$""")>]
    member _.AskWork(person: string) : Task = work (w.ApiAs(Some person))

    [<When("they ask what they are working on")>]
    member _.AskOwnWork() : Task = work w.Api

    [<Then("the work is {string}")>]
    member _.ThenWork(expected: string) =
        match w.Last with
        | Ok(:? (WorkItem[]) as items) -> equal (names expected) [ for i in items -> $"{i.Board}: {i.Title}" ] "the work"
        | other -> failwithf "expected a list of work, got %A" other

    // -- Pages of done cards -------------------------------------------------------

    [<Given("""^(\d+) cards finished on "(.*)", (an hour apart|at the same moment)$""")>]
    member _.GivenFinished(count: int, slug: string, spacing: string) : Task =
        task {
            let start = DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero)

            for i in 1..count do
                let! card = d.Add($"Card {i}", slug)
                let card = ok card
                w.Now <- if spacing = "an hour apart" then start.AddHours(float i) else start
                let! dropped = d.Call(w.Api.drop { Ref = { Card = card.Id; Version = card.Version }; Reason = "finished" })
                ok dropped |> ignore
        }

    [<When("page {int} of the done cards on {string} is read, {int} to a page")>]
    member _.ReadPage(number: int, slug: string, size: int) : Task = page slug number size

    [<When("every page of the done cards on {string} is read, {int} to a page")>]
    member _.ReadEveryPage(slug: string, size: int) : Task =
        task {
            let! first = page slug 1 size
            let first = ok first
            let pages = ResizeArray [ first ]

            for number in 2 .. first.Pages do
                let! next = page slug number size
                pages.Add(ok next)

            w.Pages <- List.ofSeq pages
        }

    [<Then("the page has the cards {string}")>]
    member _.ThenPage(titles: string) =
        match w.Last with
        | Ok(:? CardPage as p) -> equal (names titles) [ for c in p.Cards -> c.Title ] "the page's cards"
        | other -> failwithf "expected a page of cards, got %A" other

    [<Then("there are {int} done cards on {int} pages")>]
    member _.ThenTotals(total: int, pages: int) =
        match w.Last with
        | Ok(:? CardPage as p) -> equal (total, pages) (p.Total, p.Pages) "cards and pages"
        | other -> failwithf "expected a page of cards, got %A" other

    [<Then("together the pages have each of the {int} cards once")>]
    member _.ThenEachOnce(count: int) =
        let titles = [ for p in w.Pages do for c in p.Cards -> c.Title ]
        equal [ for i in 1..count -> $"Card {i}" ] (List.sort titles) "the cards on all pages"

/// Feature: Cards carry labels
[<Binding>]
type LabelSteps(w: World, d: Driver) =

    [<StepDefinition("{string} is labelled {string}")>]
    member _.Label(title: string, labels: string) : Task =
        task {
            let! ref = d.Reference title
            return! d.Call(w.Api.label { Ref = ref; Labels = entries labels })
        }

    [<When("""^(\w+) labels (?:her|his|their) copy "(.*)"$""")>]
    member _.LabelCopy(person: string, labels: string) : Task =
        d.Call(w.Api.label { Ref = w.Copies.[person]; Labels = entries labels })

    [<Then("the card {string} has the labels {string}")>]
    member _.ThenLabels(title: string, labels: string) : Task =
        task {
            let! card = d.Current title
            equal (names labels) (List.ofArray card.Labels) "the card's labels"
        }

    [<Then("the label {string} is stored once")>]
    member _.ThenStoredOnce(label: string) : Task =
        task {
            let! n = d.Count $"select count(*) from labels where name = {label}"
            equal 1L n "labels stored"
        }

    [<When("the cards on {string} labelled {string} are read")>]
    member _.ReadLabelled(slug: string, label: string) : Task =
        d.Call(w.Api.labelled { Board = slug; Label = label })

    [<Then("the cards found are {string}")>]
    member _.ThenFound(titles: string) =
        match w.Last with
        | Ok(:? (CardView[]) as cards) -> equal (names titles) [ for c in cards -> c.Title ] "the cards found"
        | other -> failwithf "expected cards, got %A" other

/// Feature: People sign in, and changes are made in their name
[<Binding>]
type AccountSteps(w: World, d: Driver) =

    let register (name: string) (password: string) =
        d.Call(w.Api.register { Name = name; Password = password })

    let nobody () : Task =
        task {
            let! who = Async.StartAsTask(w.Api.whoAmI ())
            equal (Error SignInRequired) who "who is signed in"
        }

    [<Given("nobody is signed in")>]
    member _.GivenNobody() = w.Who <- None

    [<Then("nobody is signed in any more")>]
    member _.ThenNobody() : Task = nobody ()

    [<When("someone registers as {string} with the password {string}")>]
    member _.Register(name: string, password: string) : Task = register name password

    [<Given("the account {string} with the password {string}")>]
    member _.GivenAccount(name: string, password: string) : Task =
        task {
            let! registered = register name password
            ok registered |> ignore
            // The account exists; nobody is using it.
            w.Who <- None
        }

    [<Given("{string} is signed in with an account")>]
    member _.GivenSignedIn(name: string) : Task =
        task {
            let! registered = register name "a-long-enough-password"
            ok registered |> ignore
        }

    [<When("someone signs in as {string} with the password {string}")>]
    member _.SignIn(name: string, password: string) : Task =
        d.Call(w.Api.signIn { Name = name; Password = password })

    [<When("they sign out")>]
    member _.SignOut() : Task = d.Call(w.Api.signOut ())

    [<Then("{string} is signed in")>]
    member _.ThenSignedIn(name: string) : Task =
        task {
            let! who = Async.StartAsTask(w.Api.whoAmI ())
            equal (Ok name) who "who is signed in"
        }

    [<Then("the name or the password is wrong")>]
    member _.ThenWrong() = equal (Error WrongNameOrPassword) w.Last "the outcome"

    [<Then("the request succeeds")>]
    member _.ThenSucceeds() =
        match w.Last with
        | Ok _ -> ()
        | Error e -> failwithf "expected the request to succeed, got %A" e

    [<Then("the request is refused until someone signs in")>]
    member _.ThenSignInRequired() = equal (Error SignInRequired) w.Last "the outcome"

    [<Then("no stored account contains the text {string}")>]
    member _.ThenNotStored(text: string) : Task =
        task {
            let! rows = w.Repo.Sql<string>($"select name || ' ' || password_hash from accounts")
            let rows = ok rows
            equal 1 rows.Length "accounts stored"

            if rows |> List.exists (fun row -> row.Contains text) then
                failwithf "an account's row contains %s" text
        }

    [<When("they start {string}")>]
    member _.Start(title: string) : Task =
        task {
            let! ref = d.Reference title
            return! d.Call(w.Api.start ref)
        }

    [<When("they comment {string} on {string}")>]
    member _.Comment(body: string, title: string) : Task =
        d.Call(w.Api.comment { Card = snd w.Cards.[title]; Body = body })

[<Binding>]
type QuerySteps(w: World) =
    let mutable problems: string list = []

    /// Starts a repository over the server's queries with the query check
    /// turned down to Warn, so every problem is collected instead of the
    /// first start-up failing. No queries found is a problem too. The tables
    /// are left out: this scenario is about the queries, so nothing is sent
    /// to the database and the data source is never opened.
    [<When("every query the application declares is checked against its model")>]
    member _.Check() : Task =
        task {
            let found = ResizeArray<string>()

            let options =
                { RepoOptions.Default with
                    Queries = StartupCheck.Warn
                    Schema = StartupCheck.Ignore
                    OnProblem =
                        Some(fun problem ->
                            match problem with
                            | StartupProblem.Query _
                            | StartupProblem.NoQueries _ -> found.Add(string problem)
                            | _ -> ()) }

            let! _ =
                Repo.Start(
                    Kanban.Server.Db.model,
                    Plasm.Postgres.PostgresProvider(Npgsql.NpgsqlDataSource.Create "Host=localhost"),
                    queriesIn = typeof<Kanban.Server.Workflows.BoardLine>.Assembly,
                    options = options
                )

            problems <- List.ofSeq found
        }

    [<Then("none of them is refused")>]
    member _.NoneRefused() =
        if not problems.IsEmpty then
            failwithf "Plasm cannot translate:\n%s" (String.concat "\n" problems)
