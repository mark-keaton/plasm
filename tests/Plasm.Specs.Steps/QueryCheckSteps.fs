/// Feature: A repository starts only when its queries and the database fit.
/// The queries are in assemblies of their own (Plasm.Specs.Queries.Good and
/// .Bad), since Repo.Start checks a whole assembly.
namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain

[<Binding>]
type QueryCheckSteps(w: World) =

    let goodAssembly = typeof<Plasm.Specs.Queries.Good.Marker>.Assembly
    let badAssembly = typeof<Plasm.Specs.Queries.Bad.Marker>.Assembly

    let start (assembly: Reflection.Assembly) (configure: RepoOptions -> RepoOptions) =
        task {
            let reported = ResizeArray<StartupProblem>()

            let options =
                configure
                    { RepoOptions.Default with
                        OnCommand = Some w.Db.Recorder.Record
                        OnProblem = Some reported.Add }

            w.MarkCommands()

            try
                let! repo = Repo.Start(w.Db.Model, w.Db.Provider, queriesIn = assembly, options = options)
                w.Set("startedRepo", repo)
            with :? PlasmStartupException as e ->
                w.Set("startFailure", e)

            w.Set("reported", List.ofSeq reported)
        }
        |> unit

    let warnBoth (o: RepoOptions) = { o with Queries = StartupCheck.Warn; Schema = StartupCheck.Warn }

    let failure () =
        if w.Has "startFailure" then w.Get<PlasmStartupException> "startFailure"
        else failwith "start-up did not fail"

    let reported () = w.Get<StartupProblem list> "reported"

    [<StepDefinition("I start a repository for the assembly whose queries all translate")>]
    member _.StartGood() = start goodAssembly id

    [<StepDefinition("I start a repository for the assembly whose queries all translate, warning")>]
    member _.StartGoodWarn() = start goodAssembly warnBoth

    [<StepDefinition("I start a repository for the assembly whose queries all translate, warning only for the schema")>]
    member _.StartGoodWarnSchema() = start goodAssembly (fun o -> { o with Schema = StartupCheck.Warn })

    [<StepDefinition("I start a repository for the assembly with queries Plasm cannot translate")>]
    member _.StartBad() = start badAssembly id

    [<StepDefinition("I start a repository for the assembly with queries Plasm cannot translate, warning")>]
    member _.StartBadWarn() = start badAssembly warnBoth

    [<StepDefinition("I start a repository for the assembly with queries Plasm cannot translate, ignoring both")>]
    member _.StartBadIgnore() =
        start badAssembly (fun o -> { o with Queries = StartupCheck.Ignore; Schema = StartupCheck.Ignore })

    [<StepDefinition("I start a repository for the assembly with queries Plasm cannot translate, warning, with no OnProblem")>]
    member _.StartBadStderr() =
        task {
            let original = Console.Error
            use captured = new IO.StringWriter()
            Console.SetError captured

            try
                do! start badAssembly (fun o -> { warnBoth o with OnProblem = None })
            finally
                Console.SetError original

            w.Set("stderr", captured.ToString())
        }
        |> unit

    [<StepDefinition("I start a repository for an assembly that defines no queries")>]
    member _.StartNone() = start typeof<TableAttribute>.Assembly id

    [<StepDefinition("the customers table has lost its email column")>]
    member _.DropEmail() =
        w.Db.Exec "alter table customers drop column email" |> unit

    [<StepDefinition("the repository starts")>]
    member _.Starts() =
        if w.Has "startFailure" then
            failwithf "start-up failed: %s" (w.Get<PlasmStartupException> "startFailure").Message

        check (w.Has "startedRepo") "no repository"

    [<StepDefinition("it reads the Customers")>]
    member _.Reads() =
        task {
            let! r = (w.Get<Repo> "startedRepo").All Plasm.Specs.Queries.Good.everyone
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("^start-up fails, saying \"(.*)\"$")>]
    member _.Fails(text: string) =
        let e = failure ()
        check (e.Message.Contains text) e.Message

    [<StepDefinition("^the failure names the queries \"(.*)\", \"(.*)\" and \"(.*)\", in that order, each with why$")>]
    member _.NamesQueries(a: string, b: string, c: string) =
        let names =
            [ for p in (failure ()).Problems do
                  match p with
                  | StartupProblem.Query q ->
                      check (string q.Error <> "") "no reason given"
                      q.Query
                  | _ -> () ]

        equal 3 names.Length "queries named"
        List.zip names [ a; b; c ] |> List.iter (fun (n, e) -> check (n.EndsWith e) $"{n} is not {e}")

    [<StepDefinition("the failure names the missing column customers.email")>]
    member _.NamesColumn() =
        let e = failure ()

        check
            ((failure ()).Problems
             |> List.exists (function
                 | StartupProblem.Schema s -> s.Table = "customers" && s.Column = Some "email"
                 | _ -> false))
            e.Message

    [<StepDefinition("the failure lists the three queries by name, then the missing column")>]
    member _.Ordered() =
        let kinds =
            (failure ()).Problems
            |> List.map (function
                | StartupProblem.Query _ -> "query"
                | StartupProblem.Schema _ -> "schema"
                | other -> string other)

        equal [ "query"; "query"; "query"; "schema" ] kinds "the order of the problems"
        let message = (failure ()).Message
        check (message.IndexOf "Bad.normalized" < message.IndexOf "customers") message

    [<StepDefinition("^a note says \"(.*)\"$")>]
    member _.Note(text: string) =
        let notes =
            [ for p in reported () do
                  match p with
                  | StartupProblem.UncheckedFunctions _ -> string p
                  | _ -> () ]

        check (notes |> List.exists (fun n -> n.Contains "1 function returning a query is not checked" && n.Contains text.[text.LastIndexOf ' ' + 1 ..])) $"%A{notes}"

    [<StepDefinition("^(\\d+) problems are reported, naming \"(.*)\", \"(.*)\" and \"(.*)\"$")>]
    member _.Reported(n: int, a: string, b: string, c: string) =
        let queries = [ for p in reported () do match p with StartupProblem.Query q -> q.Query | _ -> () ]
        equal n queries.Length "problems reported"
        List.zip queries [ a; b; c ] |> List.iter (fun (q, e) -> check (q.EndsWith e) $"{q} is not {e}")

    [<StepDefinition("1 problem is reported, naming the missing column customers.email")>]
    member _.ReportedColumn() =
        match reported () with
        | [ StartupProblem.Schema s ] -> check (s.Table = "customers" && s.Column = Some "email") (string s)
        | other -> failwithf "expected one schema difference, got %A" other

    [<StepDefinition("^standard error names \"(.*)\"$")>]
    member _.Stderr(text: string) =
        let written = w.Get<string> "stderr"
        check (written.Contains text) written

    [<StepDefinition("I compile code that makes a repository with Repo\\(model, provider\\)")>]
    member _.CompileConstructor() =
        task {
            let! r = CompileCheck.check "let repo = Repo(Schemas.model, Unchecked.defaultof<IProvider>)"
            w.Set("compiled", r)
        }
        |> unit

    [<StepDefinition("^Customers named \"([^\"]*)\" and \"([^\"]*)\"$")>]
    member _.TwoCustomers(a: string, b: string) =
        task {
            for name in [ a; b ] do
                let! r = w.Repo.Transact(Tx.insert (New.customer name $"{name.ToLower()}@example.com"): Tx<_, unit>)
                ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read the names with the checked prepared projection for the name \"(.*)\"")>]
    member _.ReadProjection(name: string) =
        task {
            let! r = w.Repo.All(Plasm.Specs.Queries.Good.namesOf name)
            w.Set("names", ok r)
        }
        |> unit

    [<StepDefinition("only the name \"(.*)\" is returned")>]
    member _.OnlyName(name: string) = equal [ name ] (w.Get<string list> "names") "names"

    // A module's queries checked by the internal CheckQueries (including,
    // distinct, exists and json features).

    [<StepDefinition("no problem is reported")>]
    member _.NoProblem() = equal [] (w.Get<QueryProblem list> "problems") "problems"

    [<StepDefinition("one problem is reported, for the query \"(.*)\", saying why it cannot be translated")>]
    member _.OneProblem(name: string) =
        match w.Get<QueryProblem list> "problems" with
        | [ p ] ->
            check (p.Query.EndsWith name) $"the problem is for {p.Query}"

            match p.Error with
            | TranslationError.UnsupportedExpression(_, reason) -> check (reason <> "") "no reason given"
            | other -> failwithf "expected an unsupported expression, got %A" other
        | other -> failwithf "expected one problem, got %A" other
