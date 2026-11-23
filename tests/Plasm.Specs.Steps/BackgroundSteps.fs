namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

[<Binding>]
type BackgroundSteps(w: World) =

    let reference (model: string) (field: string) (target: Type) =
        check
            (w.Db.Model.References |> List.exists (fun r -> r.Record = model && r.Field = field && r.Target = target.Name))
            $"{model}.{field} references {target.Name}: %A{w.Db.Model.References}"

    /// A scenario tagged for one database never runs on the other: if a run
    /// starts one (its filter left out), it fails at once, naming the run it
    /// belongs to.
    [<BeforeScenario(Order = -100000)>]
    member _.OnItsProvider(scenario: ScenarioContext, feature: FeatureContext) =
        let tags = Set.ofSeq scenario.ScenarioInfo.CombinedTags

        match providerKind with
        | PostgreSQL when tags.Contains "sqlite" ->
            failwith "this scenario is SQLite's: run it with PLASM_TEST_PROVIDER=sqlite dotnet test tests/Plasm.Specs"
        | SQLite when tags.Contains "postgres" ->
            failwith "this scenario is PostgreSQL's: run it with dotnet test tests/Plasm.Specs (PLASM_TEST_PROVIDER unset)"
        | _ -> ()

    /// Each scenario has its own database and connection pool: both go when
    /// the scenario ends, after every other hook (one rolls back a prepared
    /// transaction, which would keep the database from being dropped).
    [<AfterScenario(Order = 100000)>]
    member _.DropDatabase() =
        if obj.ReferenceEquals(w.Db, null) then
            System.Threading.Tasks.Task.CompletedTask
        else
            (w.Db :> IAsyncDisposable).DisposeAsync().AsTask()

    [<StepDefinition("a configured relational database")>]
    member _.Database() =
        task {
            let! db = freshDefault ()
            w.Db <- db
        }
        |> unit

    [<StepDefinition("mappings for Customers, Orders, OrderLines, Products, and Payments")>]
    member _.Mappings() =
        for t in [ typeof<Customer>; typeof<Order>; typeof<OrderLine>; typeof<Product>; typeof<Payment> ] do
            check (List.contains t w.Db.Model.Types) $"{t.Name} is mapped"

    [<StepDefinition("an Order refers to its Customer by id")>]
    member _.OrderRefersToCustomer() = reference "Order" "CustomerId" typeof<Customer>

    [<StepDefinition("an Order owns its OrderLines")>]
    member _.OrderOwnsLines() =
        let plan = w.Repo.Explain(Query.from (tableOf<Order>)) |> ok
        check (plan.Loads |> List.exists (fun l -> l.Path = "Lines")) "an Order's OrderLines are loaded with it"

    [<StepDefinition("a Payment refers to its Order by id")>]
    member _.PaymentRefersToOrder() = reference "Payment" "OrderId" typeof<Order>

    [<StepDefinition("an OrderLine refers to its Product by id")>]
    member _.LineRefersToProduct() = reference "OrderLine" "ProductId" typeof<Product>

    // -- Shared steps --------------------------------------------------------

    [<StepDefinition("no database command is executed")>]
    member _.NoCommands() =
        equal [] (w.CommandsSinceMark |> List.map (fun e -> e.Sql)) "commands since mark"

    [<StepDefinition("I execute the query")>]
    member _.ExecuteQuery() = w.Pending()

    [<StepDefinition("I execute the load")>]
    member _.ExecuteLoad() = w.Pending()
