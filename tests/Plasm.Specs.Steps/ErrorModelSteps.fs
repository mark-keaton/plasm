namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Failures are values with qualified names
[<Binding>]
type ErrorModelSteps(w: World) =

    [<StepDefinition("a repository kept from a sandbox that has ended")>]
    member _.KeptFromSandbox() =
        task {
            let mutable kept = Unchecked.defaultof<Repo>

            do!
                w.Repo.Sandbox(fun repo ->
                    kept <- repo
                    Task.FromResult())

            w.Set("closedRepo", kept)
        }
        |> unit

    [<StepDefinition("a playground that has been closed")>]
    member _.ClosedPlayground() =
        let pg = w.Repo.Playground()
        pg.Close()
        w.Set("closedRepo", pg :> Repo)

    [<StepDefinition("I read Customers through it")>]
    member _.ReadThrough() =
        task {
            try
                let! r = w.Get<Repo>("closedRepo").All(plasm { for c in tableOf<Customer> do orderBy c.Name })
                w.Set("closedRead", (Choice1Of2 r: Choice<_, exn>))
            with ex ->
                w.Set("closedRead", (Choice2Of2 ex: Choice<Result<Customer list, DbError>, exn>))
        }
        |> unit

    [<StepDefinition("the read returns an error saying the repository is closed")>]
    member _.ReturnsClosed() =
        match w.Get<Choice<Result<Customer list, DbError>, exn>> "closedRead" with
        | Choice1Of2(Error(DbError.Database f)) ->
            equal DbFailureKind.RepositoryClosed f.Kind "failure kind"
            check (f.Message.Contains "can no longer be used" || f.Message.Contains "was closed") f.Message
        | other -> failwithf "expected a closed-repository error, got %A" other

    [<StepDefinition("no exception is thrown")>]
    member _.NoException() =
        for key in [ "closedRead"; "unreadable"; "controlled"; "childLoads" ] do
            if w.Has key then
                match w.Get<obj> key with
                | :? Choice<Result<Customer list, DbError>, exn> as c ->
                    match c with
                    | Choice2Of2 ex -> failwithf "an exception was thrown: %s" ex.Message
                    | _ -> ()
                | :? Choice<Result<OrderLine, TransactionError<unit>>, exn> as c ->
                    match c with
                    | Choice2Of2 ex -> failwithf "an exception was thrown: %s" ex.Message
                    | _ -> ()
                | :? Choice<Result<unit, TransactionError<string>>, exn> as c ->
                    match c with
                    | Choice2Of2 ex -> failwithf "an exception was thrown: %s" ex.Message
                    | _ -> ()
                | :? Choice<Order list list, exn> as c ->
                    match c with
                    | Choice2Of2 ex -> failwithf "an exception was thrown: %s" ex.Message
                    | _ -> ()
                | _ -> ()

    [<StepDefinition("the database changes an inserted OrderLine's quantity to zero")>]
    member _.ZeroingTrigger() =
        task {
            let! _ =
                w.Db.Exec(
                    "create function zero_quantity() returns trigger as $$ begin new.quantity := 0; return new; end $$ language plpgsql; "
                    + "create trigger zero_quantity before insert on order_lines for each row execute function zero_quantity();"
                )

            return ()
        }
        |> unit

    [<StepDefinition("I insert an OrderLine")>]
    member _.InsertLine() =
        task {
            let! product = Seed.product w.Db "SKU-1"
            let! order = Seed.order w.Db None "draft" 10m (Seed.at 1)

            try
                let! r = w.Repo.Transact(Tx.insert { New.line product 2 5m with OrderId = order }: Tx<_, unit>)
                w.Set("unreadable", (Choice1Of2 r: Choice<_, exn>))
            with ex ->
                w.Set("unreadable", (Choice2Of2 ex: Choice<Result<OrderLine, TransactionError<unit>>, exn>))
        }
        |> unit

    [<StepDefinition("the insert returns a materialisation error naming OrderLine.Quantity")>]
    member _.MaterialisationError() =
        match w.Get<Choice<Result<OrderLine, TransactionError<unit>>, exn>> "unreadable" with
        | Choice1Of2(Error { Failure = TxFailure.Failed(DbError.Materialization m) }) ->
            equal "OrderLine" m.Model "model"
            equal "Quantity" m.Property "property"
        | other -> failwithf "expected a materialisation error, got %A" other

    [<StepDefinition("I compile a domain union with cases named Translation, Database, Materialization and UnmappedType alongside Plasm")>]
    member _.CompileDomainUnion() =
        task {
            let domain =
                String.concat
                    "\n"
                    [ "type Outcome = Translation | Database | Materialization | UnmappedType"
                      "let classify (r: Result<int, DbError>) ="
                      "    match r with"
                      "    | Ok _ -> Translation"
                      "    | Error(DbError.Database _) -> Database"
                      "    | Error(DbError.Translation(TranslationError.UnmappedType _)) -> UnmappedType"
                      "    | Error _ -> Materialization"
                      "let chosen: Outcome = Database" ]

            let unqualified =
                String.concat
                    "\n"
                    [ "let kind (e: DbError) ="
                      "    match e with"
                      "    | Database _ -> 1"
                      "    | _ -> 0" ]

            let! a = CompileCheck.check domain
            let! b = CompileCheck.check unqualified
            w.Set("domainCompile", a)
            w.Set("unqualifiedCompile", b)
        }
        |> unit

    [<StepDefinition("the domain cases are used without qualification")>]
    member _.DomainCompiles() =
        let r = w.Get<CompileCheck.CompileResult> "domainCompile"
        check r.Compiles $"domain union with Plasm's case names should compile: %A{r.Errors}"

    [<StepDefinition("Plasm's error cases must be written with their type name")>]
    member _.UnqualifiedRejected() =
        let r = w.Get<CompileCheck.CompileResult> "unqualifiedCompile"
        check (not r.Compiles) "an unqualified DbError case should not compile"
        check (r.Errors |> List.exists (fun e -> e.Contains "FS0039")) $"expected FS0039 (not defined): %A{r.Errors}"

/// A domain error a transaction step fails with.
type StockError = OutOfStock of sku: string

/// Feature: Failures are values with qualified names (unions and typed transaction failures)
[<Binding>]
type QualifiedUnionSteps(w: World) =

    static let unions =
        dict
            [ "ChangeAction", ("Insert | Update | Delete", "ChangeAction.Insert", "let unqualified: ChangeAction = Insert")
              "SortDirection", ("Ascending | Descending", "SortDirection.Ascending", "let unqualified: SortDirection = Ascending")
              "FieldChange", ("Cleared | Assigned of int", "FieldChange.Cleared", "let unqualified: FieldChange = Cleared")
              "AssocKind", ("HasMany | HasOne | BelongsTo | ManyToMany", "AssocKind.HasMany", "let unqualified: AssocKind = HasMany")
              "LoadStrategy", ("BatchedByKeys | PerParentWindow", "LoadStrategy.BatchedByKeys 1", "let unqualified: LoadStrategy = BatchedByKeys 1")
              "TxFailure",
              ("InvalidChanges | Conflict | Failed | Rejected | Misused",
               "(TxFailure.Conflict(\"Order\", 1L): TxFailure<string>)",
               "let unqualified: TxFailure<string> = Conflict(\"Order\", 1L)") ]

    [<StepDefinition("^I compile a domain union reusing the case names of (\\w+) alongside Plasm$")>]
    member _.Compile(union: string) =
        task {
            let cases, qualified, unqualified = unions.[union]

            let domain =
                String.concat "\n" [ $"type Domain = {cases}"; $"let plasmValue = {qualified}"; "let domainValue: Domain = " + cases.Split(' ').[0] ]

            let! a = CompileCheck.check domain
            let! b = CompileCheck.check unqualified
            w.Set("domainCompile", a)
            w.Set("unqualifiedCompile", b)
        }
        |> unit

    [<StepDefinition("^Plasm's (ChangeAction|SortDirection|FieldChange|AssocKind|LoadStrategy|TxFailure) cases must be written with their type name$")>]
    member _.MustQualify(_union: string) =
        let r = w.Get<CompileCheck.CompileResult> "unqualifiedCompile"
        check (not r.Compiles) "an unqualified case should not compile"

    [<StepDefinition("a transaction step fails with a domain error value")>]
    member _.TypedRejection() =
        task {
            let! r = w.Repo.Transact(transaction { return! Tx.fail (OutOfStock "SKU-1") })
            w.Set("rejected", (err r).Failure)
        }
        |> unit

    [<StepDefinition("the failure gives back the domain error with its type, without casting")>]
    member _.ReasonTyped() =
        match w.Get<TxFailure<StockError>> "rejected" with
        | TxFailure.Rejected r -> equal (OutOfStock "SKU-1") r "reason"
        | other -> failwithf "expected a rejection, got %A" other

    [<StepDefinition("reading it as another type does not compile")>]
    member _.OtherTypeDoesNotCompile() =
        task {
            let! r =
                CompileCheck.check
                    """
type StockError = OutOfStock of string
let read (f: TxFailure<StockError>) =
    match f with
    | TxFailure.Rejected (r: string) -> r
    | _ -> ""
"""

            check (not r.Compiles) "reading a StockError rejection as a string compiled"
        }
        |> unit

    [<StepDefinition("a transaction step's changeset is invalid")>]
    member _.InvalidStep() =
        task {
            // orders_total_check refuses a negative total: the field's error.
            let! r = w.Repo.Transact(Tx.insert (New.order None Draft -5m): Tx<Order, string>)
            w.Set("invalidStep", (err r).Failure)
        }
        |> unit

    [<StepDefinition("the failure names the entity and lists the field errors")>]
    member _.InvalidNamed() =
        match w.Get<TxFailure<string>> "invalidStep" with
        | TxFailure.InvalidChanges(entity, errors) ->
            equal "Order" entity "entity"
            equal [ "Total" ] (errors |> List.map (fun e -> e.Field)) "fields"
        | other -> failwithf "expected invalid changes, got %A" other
