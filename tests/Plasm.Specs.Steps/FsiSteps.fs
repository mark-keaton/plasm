namespace Plasm.Specs

open System
open System.IO
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain

/// What one submission to F# Interactive produced.
type Submission =
    { Code: string
      /// Everything the session printed, as a person at the REPL would see it.
      Output: string
      Errors: string list
      Value: obj option
      Failure: exn option }

    member s.Succeeded = s.Errors.IsEmpty && s.Failure.IsNone

/// A real F# Interactive session hosted by the test. Code is evaluated one
/// submission at a time, so values and types live across submissions.
type FsiSession() =
    let out = new StringWriter()
    let err = new StringWriter()
    let history = ResizeArray<Submission>()

    let session =
        FsiEvaluationSession.Create(
            FsiEvaluationSession.GetDefaultConfiguration(),
            [| "fsi"; "--noninteractive"; "--nologo" |],
            new StringReader(""),
            out,
            err,
            collectible = false
        )

    member _.History = List.ofSeq history

    member _.Eval(code: string) =
        out.GetStringBuilder().Clear() |> ignore
        let result, diagnostics = session.EvalInteractionNonThrowing code

        let errors =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
            |> Array.map (fun d -> d.Message)
            |> List.ofArray

        let s =
            match result with
            | Choice1Of2 v ->
                { Code = code
                  Output = out.ToString()
                  Errors = errors
                  Value = v |> Option.map (fun v -> v.ReflectionValue)
                  Failure = None }
            | Choice2Of2 ex ->
                { Code = code
                  Output = out.ToString()
                  Errors = errors
                  Value = None
                  Failure = (if errors.IsEmpty then Some ex else None) }

        history.Add s
        s

    /// Evaluates code that must succeed.
    member s.Run(code: string) =
        let r = s.Eval code

        if not r.Succeeded then
            failwithf "submission failed:\n%s\nerrors: %A\nexception: %A" code r.Errors r.Failure

        r

    /// Evaluates an expression that must succeed and returns its value.
    member s.Value<'T>(code: string) : 'T =
        match (s.Run code).Value with
        | Some v -> unbox<'T> v
        | None -> failwithf "submission produced no value:\n%s" code

    interface IDisposable with
        member _.Dispose() = (session :> IDisposable).Dispose()

/// Feature: Using Plasm from F# Interactive
[<Binding>]
type FsiSteps(w: World) =

    let assemblies =
        [ typeof<Plasm.Query>.Assembly.Location
          typeof<Plasm.Postgres.PostgresProvider>.Assembly.Location
          typeof<Npgsql.NpgsqlConnection>.Assembly.Location ]

    let domainSource =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Plasm.TestDomain", "Domain.fs"))

    let fsi () = w.Get<FsiSession> "fsi"

    let start () =
        if w.Has "fsi" then
            (fsi () :> IDisposable).Dispose()

        w.Set("fsi", new FsiSession())

    let reference () =
        for a in assemblies do
            fsi().Run($"#r @\"{a}\"") |> ignore

    /// Defines the test domain in the session (as source, so its types are
    /// session types) and a repository that records its commands.
    let mappings () =
        fsi().Run($"#load @\"{domainSource}\"") |> ignore

        fsi().Run(
            String.concat
                "\n"
                [ "open Plasm"
                  "open Plasm.Interactive"
                  "open Plasm.TestDomain.Domain"
                  "let commands = ResizeArray<string>()"
                  $"let repo = Repo(Schemas.model, Plasm.Postgres.Postgres.connect @\"{w.Db.ConnectionString};Pooling=false\", {{ RepoOptions.Default with OnCommand = Some(fun e -> commands.Add e.Sql) }})" ]
        )
        |> ignore

    let commandCount () = fsi().Value<int> "commands.Count"

    let markCommands () = w.Set("fsiMark", commandCount ())

    let display (s: Submission) = w.Set("display", s)
    let displayed () = (w.Get<Submission> "display").Output

    let contains (text: string) (output: string) =
        check (output.Contains text) $"expected the session to display {text}, but it displayed:\n{output}"

    let lacks (text: string) (output: string) =
        check (not (output.Contains text)) $"expected the session not to display {text}, but it displayed:\n{output}"

    let occurrences (text: string) (output: string) =
        (output.Length - output.Replace(text, "").Length) / text.Length

    let names (repo: string) =
        fsi().Value<string>(
            $"match {repo}.All(plasm {{ for c in Schemas.customers do orderBy c.Name }}) |> wait with Ok cs -> cs |> List.map (fun c -> c.Name) |> String.concat \"|\" | Error e -> failwithf \"%%A\" e"
        )
        |> fun (s: string) -> s.Split('|', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let committedNamed (name: string) =
        w.Db.Scalar<int64>("select count(*) from customers where name = @n", [ "n", box name ]).Result

    let insertInPlayground (name: string) =
        fsi().Run($"pg.Insert(Changeset.insert (New.customer \"{name}\" \"{name.ToLowerInvariant()}@example.com\")) |> wait")

    let openPlayground () =
        fsi().Run "let pg = repo.Playground()" |> ignore

    let seed () =
        task {
            let! c = w.Repo.Insert(Changeset.insert (New.customer "Ada" "ada@example.com"))
            let c = ok c
            let! o = w.Repo.Insert(Changeset.insert (New.order (Some c.Id) Published 12.5m))
            ok o |> ignore
        }

    let scaffold (table: string) =
        fsi().Run $"repo.Scaffold \"{table}\" |> wait" |> display

        w.Set(
            "scaffold",
            fsi().Value<string>($"match repo.Scaffold \"{table}\" |> wait with Ok s -> s.Code | Error e -> failwithf \"%%A\" e")
        )

        w.Set(
            "schemaName",
            fsi().Value<string>($"match repo.Scaffold \"{table}\" |> wait with Ok s -> s.SchemaName | Error e -> failwithf \"%%A\" e")
        )

    let code () = w.Get<string> "scaffold"

    // -- Background ----------------------------------------------------------

    [<StepDefinition("an F# Interactive session that references Plasm")>]
    member _.Session() =
        start ()
        reference ()

    [<StepDefinition("mappings for Customers, Orders, OrderLines, Products, and Payments evaluated in the session")>]
    member _.Mappings() = mappings ()

    // -- Setup ---------------------------------------------------------------

    [<StepDefinition("a new F# Interactive session")>]
    member _.NewSession() = start ()

    [<StepDefinition("I reference Plasm and the PostgreSQL provider")>]
    [<StepDefinition("I reference Plasm and the PostgreSQL provider again")>]
    member _.Reference() = reference ()

    [<StepDefinition("I evaluate the mappings and a repository")>]
    member _.EvaluateMappings() = mappings ()

    [<StepDefinition("no setup call was needed")>]
    member _.NoSetup() =
        for s in fsi().History do
            lacks "fsi." s.Code
            lacks "AddPrinter" s.Code

    [<StepDefinition("the session reports no errors")>]
    member _.NoErrors() =
        for s in fsi().History do
            check s.Succeeded $"submission failed:\n{s.Code}\n%A{s.Errors} %A{s.Failure}"

    [<StepDefinition("the query is displayed once")>]
    member _.DisplayedOnce() =
        equal 1 (occurrences "SELECT" (displayed ())) "SELECT commands displayed"

    // -- Displays ------------------------------------------------------------

    [<StepDefinition("I evaluate a query for a customer's published Orders")>]
    member _.QueryPublished() =
        markCommands ()

        fsi().Run "let q = plasm { for o in Schemas.orders do where (o.Status = Published && o.CustomerId = Some (CustomerId 42L)) }"
        |> display

    [<StepDefinition("the session displays the SQL with each parameter and its value")>]
    member _.DisplaysSql() =
        let out = displayed ()
        contains "SELECT" out
        contains "FROM \"orders\"" out
        contains "@p0 = \"published\"" out
        contains "@p1 = 42L" out

    [<StepDefinition("no database command was executed")>]
    member _.NoCommand() =
        equal (w.Get<int> "fsiMark") (commandCount ()) "commands run by the session"

    [<StepDefinition("I evaluate a query for Orders preloading their Lines and Products")>]
    member _.QueryWithLoads() =
        markCommands ()

        fsi().Run
            "let q = plasm { for o in Schemas.orders do preload o.Lines (plasm { for l in Schemas.orderLines do preload l.Product }) }"
        |> display

    [<StepDefinition("the session displays the main command and one command for each load, in execution order")>]
    member _.DisplaysLoads() =
        let out = displayed ()
        equal 3 (occurrences "SELECT" out) "SELECT commands displayed"
        let at (table: string) = out.IndexOf($"FROM \"{table}\"")
        check (at "orders" >= 0 && at "orders" < at "order_lines" && at "order_lines" < at "products") $"commands out of order:\n{out}"

    [<StepDefinition("I evaluate a query whose filter calls an ordinary F# function on a field")>]
    member _.Untranslatable() =
        markCommands ()
        fsi().Run "let shout (s: string) = s.ToUpperInvariant() + \"!\"" |> ignore
        fsi().Run "let q = plasm { for c in Schemas.customers do where (shout c.Name = \"ADA!\") }" |> display

    [<StepDefinition("the session displays the translation error with the expression identified")>]
    member _.DisplaysTranslationError() =
        let out = displayed ()
        contains "cannot be translated" out
        contains "shout" out

    [<StepDefinition("the session does not display SQL")>]
    member _.NoSql() = lacks "SELECT" (displayed ())

    [<StepDefinition("I read Orders without preloading their Lines")>]
    member _.ReadOrders() =
        seed().Wait()
        fsi().Run "repo.All(plasm { for o in Schemas.orders do preload o.Customer }) |> wait" |> display

    [<StepDefinition("each Order's Lines are displayed as not loaded")>]
    member _.LinesNotLoaded() = contains "Lines = NotLoaded" (displayed ())

    [<StepDefinition("loaded relationships display their records")>]
    member _.LoadedDisplayed() =
        let out = displayed ()
        contains "Customer = Loaded" out
        contains "\"Ada\"" out

    // -- Awaiting ------------------------------------------------------------

    [<StepDefinition("Customers with Orders exist")>]
    member _.CustomersWithOrders() = seed().Wait()

    [<StepDefinition("I evaluate a read of Customers through the interactive helpers")>]
    member _.ReadCustomers() =
        fsi().Run "repo.All(plasm { for c in Schemas.customers do orderBy c.Name }) |> wait" |> display

    [<StepDefinition("the session displays the Customers, not a task")>]
    member _.DisplaysCustomers() =
        let out = displayed ()
        contains "Ok [{ Id = CustomerId" out
        contains "\"Ada\"" out
        lacks "Task" out

    [<StepDefinition("a Customer with a given email exists")>]
    member _.CustomerWithEmail() =
        w.Repo.Insert(Changeset.insert (New.customer "Ada" "ada@example.com")).Result |> ok |> ignore

    [<StepDefinition("I evaluate an insert of another Customer with the same email through the interactive helpers")>]
    member _.DuplicateInsert() =
        fsi().Run "repo.Insert(Changeset.insert (New.customer \"Other\" \"ada@example.com\")) |> wait" |> display

    [<StepDefinition("the session displays a unique constraint violation naming the constraint")>]
    member _.DisplaysViolation() =
        let out = displayed ()
        contains "UniqueViolation" out
        contains "customers_email_key" out

    [<StepDefinition("the session remains usable")>]
    member _.Usable() =
        equal [ "Ada" ] (names "repo") "customers read after the failure"

    // -- Redefinition --------------------------------------------------------

    [<StepDefinition("a query for Customers evaluated in the session")>]
    member _.EarlierQuery() =
        fsi().Run "let earlier = plasm { for c in Schemas.customers do where (c.Name = \"Ada\") }" |> ignore

    [<StepDefinition("I redefine the Customer record in a later submission and re-evaluate the mappings")>]
    member _.Redefine() = mappings ()

    [<StepDefinition("I run the earlier query with the new repository")>]
    member _.RunEarlier() =
        markCommands ()
        fsi().Run "repo.All earlier |> wait" |> display

    [<StepDefinition("the error says the query uses Customer from an earlier submission and must be re-evaluated")>]
    member _.RedefinedError() =
        let out = displayed ()
        contains "RedefinedType" out
        contains "Customer" out
        contains "earlier F# Interactive submission" out
        contains "Re-evaluate the query" out

    [<StepDefinition("the earlier query now displays the same explanation instead of SQL")>]
    member _.EarlierDisplay() =
        let out = (fsi().Run "earlier").Output
        contains "earlier F# Interactive submission" out
        lacks "SELECT" out

    [<StepDefinition("I evaluate and run a new query for Customers")>]
    member _.NewQuery() =
        seed().Wait()

        w.Set(
            "newDefinition",
            fsi().Value<string>(
                "match repo.All(plasm { for c in Schemas.customers do orderBy c.Name }) |> wait with Ok cs -> sprintf \"%d %b\" cs.Length (cs.GetType().GetGenericArguments().[0] = typeof<Customer>) | Error e -> sprintf \"%A\" e"
            )
        )

    [<StepDefinition("the Customers are returned using the new definition")>]
    member _.NewDefinition() =
        equal "1 true" (w.Get<string> "newDefinition") "count and element type"

    // -- Playground ----------------------------------------------------------

    [<StepDefinition("I open a playground and insert a Customer")>]
    [<StepDefinition("a playground in which a Customer was inserted")>]
    member _.PlaygroundWithCustomer() =
        openPlayground ()
        insertInPlayground "Pia" |> ignore

    [<StepDefinition("a playground")>]
    member _.Playground() = openPlayground ()

    [<StepDefinition("I read Customers from the playground in a later submission")>]
    member _.ReadPlayground() = w.Set("seen", names "pg")

    [<StepDefinition("the inserted Customer is returned")>]
    member _.Returned() = equal [ "Pia" ] (w.Get<string list> "seen") "customers in the playground"

    [<StepDefinition("the Customer is not visible outside the playground")>]
    [<StepDefinition("the Customer does not exist")>]
    member _.NotOutside() = equal 0L (committedNamed "Pia") "committed customers named Pia"

    [<StepDefinition("I reset the playground")>]
    member _.Reset() = fsi().Run "pg.Reset()" |> ignore

    [<StepDefinition("the Customer no longer exists in the playground")>]
    member _.GoneInPlayground() = equal [] (names "pg") "customers in the playground"

    [<StepDefinition("later work in the playground succeeds")>]
    member _.LaterWork() =
        insertInPlayground "Quinn" |> ignore
        equal [ "Quinn" ] (names "pg") "customers in the playground"

    [<StepDefinition("I close the playground")>]
    member _.Close() = fsi().Run "pg.Close()" |> ignore

    [<StepDefinition("using the closed playground fails with an explanation instead of a connection error")>]
    member _.ClosedFails() =
        let s = fsi().Run "pg.All(plasm { for c in Schemas.customers do orderBy c.Name }) |> wait"
        contains "RepositoryClosed" s.Output
        contains "playground was closed" s.Output
        contains "repo.Playground()" s.Output
        lacks "ConnectionFailure" s.Output

    [<StepDefinition("code in the playground hits a unique constraint violation and then continues")>]
    member _.PlaygroundViolation() =
        insertInPlayground "Pia" |> ignore
        fsi().Run "pg.Insert(Changeset.insert (New.customer \"Again\" \"pia@example.com\")) |> wait" |> display
        insertInPlayground "Quinn" |> ignore

    [<StepDefinition("the violation is displayed as usual")>]
    member _.ViolationDisplayed() =
        let out = displayed ()
        contains "UniqueViolation" out
        contains "customers_email_key" out

    [<StepDefinition("earlier changes in the playground are kept")>]
    member _.EarlierKept() = equal [ "Pia"; "Quinn" ] (names "pg") "customers in the playground"

    // -- Scaffolding ---------------------------------------------------------

    [<StepDefinition("I scaffold the \"customers\" table")>]
    member _.ScaffoldCustomers() = scaffold "customers"

    [<StepDefinition("the session displays an F# record and a schema for it")>]
    member _.DisplaysScaffold() =
        let out = displayed ()
        contains "type Customer =" out
        contains "Email: string" out
        contains "schema<Customer> \"customers\"" out

    [<StepDefinition("evaluating them in the session succeeds")>]
    [<StepDefinition("evaluating the scaffolded code in the session succeeds")>]
    member _.EvaluateScaffold() = fsi().Run(code ()) |> ignore

    [<StepDefinition("the schema check reports no problems for them")>]
    [<StepDefinition("the schema check reports no problems for it")>]
    member _.ScaffoldChecks() =
        let name = w.Get<string> "schemaName"

        let issues =
            fsi().Value<string>(
                $"match Repo(Model.buildOrFail [] [ {name} ], Plasm.Postgres.Postgres.connect @\"{w.Db.ConnectionString};Pooling=false\").CheckSchema() |> wait with Ok [] -> \"none\" | other -> sprintf \"%%A\" other"
            )

        equal "none" issues "schema check of the scaffolded mapping"

    [<StepDefinition("I scaffold a table with a nullable column, a generated key and a column of an unsupported type")>]
    member _.ScaffoldGadgets() =
        w.Db.Exec("create table gadgets (id bigint generated always as identity primary key, label text not null, note text, location point)").Wait()
        scaffold "gadgets"

    [<StepDefinition("the nullable column becomes an option field")>]
    member _.OptionField() =
        contains "Note: string option" (code ())
        contains "Label: string\n" (code ())

    [<StepDefinition("the generated key is declared as the identity key")>]
    member _.IdentityKey() =
        contains "Id: int64" (code ())
        contains "identityKey (fun r -> r.Id)" (code ())

    [<StepDefinition("the unsupported column is left out with a comment naming its database type")>]
    member _.UnsupportedColumn() =
        contains "// location: point" (code ())
        lacks "Location:" (code ())

    [<StepDefinition("I evaluate a prepared query for the Orders of a Customer, bound to one Customer")>]
    member _.PreparedDisplay() =
        markCommands ()

        fsi().Run
            "let ordersOf = Query.prepare (fun (c: Arg<CustomerId>) -> plasm { for o in Schemas.orders do where (o.CustomerId = Some c.Value) })"
        |> ignore

        fsi().Run "let q = ordersOf (CustomerId 42L)" |> display

    [<StepDefinition("the session displays the SQL with each argument's bound value and position")>]
    member _.BoundDisplayed() =
        let out = displayed ()
        contains "SELECT" out
        contains "@p0 = 42L  (argument 1)" out

    [<StepDefinition("I evaluate a query and display it")>]
    member _.DisplayOnly() =
        fsi().Run "let shown = plasm { for c in Schemas.customers do where (c.Name = \"Ada\") }" |> display
        contains "SELECT" (displayed ())

    [<StepDefinition("no repository has planned it")>]
    member _.NotPlanned() = equal 0 (fsi().Value<Repo>("repo").PlansMade) "plans made by the session's repository"

    [<StepDefinition("running it afterwards plans it once")>]
    member _.PlannedOnce() =
        fsi().Run "repo.All shown |> wait" |> ignore
        fsi().Run "repo.All shown |> wait" |> ignore
        equal 1 (fsi().Value<Repo>("repo").PlansMade) "plans made by the session's repository"

    [<StepDefinition("a sandbox inside the playground inserts another Customer")>]
    member _.SandboxInPlayground() =
        fsi().Run
            "pg.Sandbox(fun r -> task { let! _ = r.Insert(Changeset.insert (New.customer \"Quinn\" \"quinn@example.com\")) in return () }) |> wait"
        |> ignore

    [<StepDefinition("after the sandbox ends the playground still has only its own Customer")>]
    member _.PlaygroundOwnOnly() = equal [ "Pia" ] (names "pg") "customers in the playground"

    [<StepDefinition("the playground's database connection is terminated")>]
    member _.Terminate() =
        w.Db.Scalar<int64>(
            "select count(*) from (select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and state like 'idle in transaction%') t"
        ).Result
        |> fun n -> equal 1L n "terminated connections"

    [<StepDefinition("the next use of the playground reports that its changes were lost")>]
    member _.ReportsLost() =
        let out = (fsi().Run "pg.All(plasm { for c in Schemas.customers do orderBy c.Name }) |> wait").Output
        contains "ConnectionFailure" out
        contains "changes were discarded" out

    [<StepDefinition("I scaffold a table named \"type\" with columns \"Weird Col\" and \"2fa\"")>]
    member _.ScaffoldOddNames() =
        w.Db.Exec("create table \"type\" (id bigint generated always as identity primary key, \"Weird Col\" text, \"2fa\" boolean not null)").Wait()
        scaffold "type"

    [<StepDefinition("I scaffold a table whose only column has an unsupported type")>]
    member _.ScaffoldUnmappable() =
        w.Db.Exec("create table shapes (outline point)").Wait()
        fsi().Run "repo.Scaffold \"shapes\" |> wait" |> display

    [<StepDefinition("scaffolding fails because no column can be mapped")>]
    member _.NoMappable() =
        let out = displayed ()
        contains "NoMappableColumns" out
        contains "\"outline\", \"point\"" out

    [<StepDefinition("I scaffold a table that does not exist")>]
    member _.ScaffoldMissing() = fsi().Run "repo.Scaffold \"nowhere\" |> wait" |> display

    [<StepDefinition("scaffolding fails because the table does not exist")>]
    member _.Missing() = contains "TableNotFound \"nowhere\"" (displayed ())

    [<AfterScenario>]
    member _.DisposeSession() =
        if w.Has "fsi" then
            // Sessions are not collectible, so their values outlive them: end
            // any playground, which holds a connection and a transaction.
            fsi().Eval "pg.Close()" |> ignore
            (fsi () :> IDisposable).Dispose()

/// A record mapped only by the display scenarios, so no other repository in the
/// test process can describe its queries.
type DisplayProbe = { Id: int64; Name: string; Email: string }

/// Feature: Using Plasm from F# Interactive (compiled-code display scenarios)
[<Binding>]
type CompiledDisplaySteps(w: World) =

    let probes = schema<DisplayProbe> "customers" { identityKey (fun c -> c.Id) }

    [<StepDefinition("a repository that does not include parameter values in diagnostics")>]
    member _.Hidden() =
        w.Set("displayRepo", Repo(Model.buildOrFail [] [ probes ], w.Db.Provider))

    [<StepDefinition("a repository that includes parameter values in diagnostics")>]
    member _.Shown() =
        w.Set("displayRepo", Repo(Model.buildOrFail [] [ probes ], w.Db.Provider, { RepoOptions.Default with IncludeParameterValues = true }))

    [<StepDefinition("compiled code formats a query with %A")>]
    member _.Format() =
        let secret = "s3cr3t-token"
        let q = plasm { for c in probes do where (c.Name = secret) }
        w.Set("formatted", sprintf "%A" q)
        // Keep the repository alive until after formatting.
        System.GC.KeepAlive(w.Get<Repo> "displayRepo")

    [<StepDefinition("the SQL is shown with its parameters but not their values")>]
    member _.NoValues() =
        let text = w.Get<string> "formatted"
        check (text.Contains "SELECT" && text.Contains "@p0") text
        check (not (text.Contains "s3cr3t")) $"the parameter value was shown: {text}"

    [<StepDefinition("the SQL is shown with each parameter's value")>]
    member _.Values() =
        let text = w.Get<string> "formatted"
        check (text.Contains "@p0 = \"s3cr3t-token\"") text
