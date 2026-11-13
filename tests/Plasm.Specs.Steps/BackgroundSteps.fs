namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

[<Binding>]
type BackgroundSteps(w: World) =

    let relationship (q: Query<'T>) (path: string) (kind: string) =
        let plan = w.Repo.Explain q |> ok
        let step = plan.Loads |> List.find (fun l -> l.Path = path)
        equal kind step.Relationship $"relationship kind of {path}"

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

    [<StepDefinition("an Order belongs to a Customer")>]
    member _.OrderBelongsToCustomer() =
        relationship (plasm { for o in Schemas.orders do preload o.Customer }) "Customer" "BelongsTo"

    [<StepDefinition("an Order has many OrderLines")>]
    member _.OrderHasManyLines() =
        relationship (plasm { for o in Schemas.orders do preload o.Lines }) "Lines" "HasMany"

    [<StepDefinition("an Order has many Payments")>]
    member _.OrderHasManyPayments() =
        relationship (plasm { for o in Schemas.orders do preload o.Payments }) "Payments" "HasMany"

    [<StepDefinition("an OrderLine belongs to a Product")>]
    member _.LineBelongsToProduct() =
        relationship (plasm { for l in Schemas.orderLines do preload l.Product }) "Product" "BelongsTo"

    // -- Shared steps --------------------------------------------------------

    [<StepDefinition("no database command is executed")>]
    member _.NoCommands() =
        equal [] (w.CommandsSinceMark |> List.map (fun e -> e.Sql)) "commands since mark"

    [<StepDefinition("I execute the query")>]
    member _.ExecuteQuery() = w.Pending()

    [<StepDefinition("I execute the load")>]
    member _.ExecuteLoad() = w.Pending()
