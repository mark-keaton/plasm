namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: A program builds a query at run time from declared fields
[<Binding>]
type BuildQuerySteps(w: World) =


    let status (text: string) : Result<OrderStatus, string> =
        match text with
        | "draft" -> Ok Draft
        | "published" -> Ok Published
        | "cancelled" -> Ok Cancelled
        | other -> Error $"no OrderStatus is \"{other}\""


    let sqlOf (plan: Result<QueryPlan, TranslationError>) =
        match plan with
        | Ok p -> p.Sql
        | Error e -> failwithf "the query was refused: %A" e

    let build (f: unit -> Query<'T>) =
        try
            w.Set("built", f ())
        with :? ArgumentException as e ->
            w.Set("refused", e.Message)

    member private _.Field<'T>(key: string) = w.Get<QueryField<'T>> key
    member private _.Built<'T>() = w.Get<Query<'T>> "built"

    // -- Given ------------------------------------------------------------------

    [<StepDefinition("the Customer's name declared as a field compared for equality")>]
    member this.CustomerName() = w.Set("customerName", QueryField.equal "name" <@ fun (c: Customer) -> c.Name @>)

    [<StepDefinition("the Order's total declared as a field compared and ordered")>]
    member this.OrderTotal() = w.Set("orderTotal", QueryField.ordered "total" <@ fun (o: Order) -> o.Total @>)

    [<StepDefinition("the Order's status declared as a field given as text")>]
    member this.OrderStatus() = w.Set("orderStatus", QueryField.equalVia "status" <@ fun (o: Order) -> o.Status @> status "draft")

    [<StepDefinition("the Customer's name declared as a field read through its hash code, compared and ordered")>]
    member this.CustomerNameHash() =
        w.Set("customerNameHash", QueryField.ordered "nameHash" <@ fun (c: Customer) -> c.Name.GetHashCode() @>)

    // -- When -------------------------------------------------------------------

    [<StepDefinition("I build the Customers whose name equals \"Ada\"")>]
    member this.BuildAda() =
        build (fun () ->
            let c = (this.Field<Customer> "customerName").Condition(QueryOperator.Equal, box "Ada")
            Query.from tableOf<Customer> |> BuildQuery.where (c.ToExpr()))

    [<StepDefinition("I build the Orders whose total is over 10 or whose status equals \"draft\", and whose total is not 99")>]
    member this.BuildOrders() =
        build (fun () ->
            let total, st = this.Field<Order> "orderTotal", this.Field<Order> "orderStatus"

            let c =
                QueryCondition.And(
                    QueryCondition.Or(total.Condition(QueryOperator.GreaterThan, box 10m), st.Condition(QueryOperator.Equal, box "draft")),
                    QueryCondition.Not(total.Condition(QueryOperator.Equal, box 99m))
                )

            Query.from tableOf<Order> |> BuildQuery.where (c.ToExpr()))

    [<StepDefinition("I build the Orders by total descending, skipping 5 and taking 10")>]
    member this.BuildPage() =
        build (fun () -> (this.Field<Order> "orderTotal").OrderBy(Query.from tableOf<Order>, true) |> BuildQuery.skip 5 |> BuildQuery.take 10)

    [<StepDefinition("I read the Customers whose name equals \"Grace\", built")>]
    member this.ReadGrace() =
        task {
            let c = (this.Field<Customer> "customerName").Condition(QueryOperator.Equal, box "Grace")
            let! rows = w.Repo.All(Query.from tableOf<Customer> |> BuildQuery.where (c.ToExpr()))
            w.Set("rows", ok rows)
        }
        |> unit

    [<StepDefinition("I build the Orders whose status equals \"shipped\"")>]
    member this.BuildShipped() =
        build (fun () ->
            let c = (this.Field<Order> "orderStatus").Condition(QueryOperator.Equal, box "shipped")
            Query.from tableOf<Order> |> BuildQuery.where (c.ToExpr()))

    [<StepDefinition("I build the Orders by total, taking 10 and then taking 5")>]
    member this.BuildTwice() =
        build (fun () -> (this.Field<Order> "orderTotal").OrderBy(Query.from tableOf<Order>, false) |> BuildQuery.take 10 |> BuildQuery.take 5)

    [<StepDefinition("I check the declared Order fields")>]
    member this.CheckOrders() =
        try
            w.Repo.CheckFields [ this.Field<Order> "orderTotal"; this.Field<Order> "orderStatus" ]
            w.Set<Result<unit, PlasmStartupException>>("checked", Ok())
        with :? PlasmStartupException as e ->
            w.Set<Result<unit, PlasmStartupException>>("checked", Error e)

    [<StepDefinition("I check the declared Customer fields")>]
    member this.CheckCustomers() =
        try
            w.Repo.CheckFields [ this.Field<Customer> "customerNameHash" ]
            w.Set<Result<unit, PlasmStartupException>>("checked", Ok())
        with :? PlasmStartupException as e ->
            w.Set<Result<unit, PlasmStartupException>>("checked", Error e)

    // -- Then -------------------------------------------------------------------

    [<StepDefinition("the built query's SQL is the SQL of the Customers whose name is \"Ada\", written")>]
    member this.SameAsAda() =
        equal (sqlOf (w.Repo.Explain(plasm { for c in tableOf<Customer> do where (c.Name = "Ada") }))) (sqlOf (w.Repo.Explain(this.Built<Customer>()))) "SQL"

    [<StepDefinition("the built query's SQL is the SQL of the same Orders, written")>]
    member this.SameAsOrders() =
        let written = plasm { for o in tableOf<Order> do where ((o.Total > 10m || o.Status = Draft) && not (o.Total = 99m)) }
        equal (sqlOf (w.Repo.Explain written)) (sqlOf (w.Repo.Explain(this.Built<Order>()))) "SQL"

    [<StepDefinition("the built query's SQL is the SQL of the Orders by total descending, skipping 5 and taking 10, written")>]
    member this.SameAsPage() =
        let written =
            plasm {
                for o in tableOf<Order> do
                    orderByDescending o.Total
                    skip 5
                    take 10
            }

        equal (sqlOf (w.Repo.Explain written)) (sqlOf (w.Repo.Explain(this.Built<Order>()))) "SQL"

    [<StepDefinition("the built read finds exactly the Customer \"Grace\"")>]
    member this.FoundGrace() = equal [ "Grace" ] (w.Get<Customer list> "rows" |> List.map (fun c -> c.Name)) "Customers read"

    [<StepDefinition("building is refused with {string}")>]
    member this.BuildingRefused(message: string) =
        check (w.Has "refused") "building was not refused"
        equal message (w.Get<string> "refused") "refusal"

    [<StepDefinition("the built query is refused with {string}")>]
    member this.QueryRefused(message: string) =
        match w.Repo.Explain(this.Built<Order>()) with
        | Ok _ -> failwith "the built query was translated"
        | Error e -> check ((string e).Contains message) $"refusal: expected it to contain {message} but was {e}"

    [<StepDefinition("the check passes")>]
    member this.CheckPasses() =
        match w.Get<Result<unit, PlasmStartupException>> "checked" with
        | Ok() -> ()
        | Error e -> failwithf "the check failed: %s" e.Message

    [<StepDefinition("the check fails start-up, naming each operator and the ordering:")>]
    member this.CheckFails(table: DataTable) =
        match w.Get<Result<unit, PlasmStartupException>> "checked" with
        | Ok() -> failwith "the check passed"
        | Error e ->
            let named = e.Problems |> List.map (function StartupProblem.Query q -> q.Query | other -> string other)
            equal [ for row in table.Rows -> row.["refused"] ] named "fields named"
            check (e.Message.StartsWith "Plasm did not start: 7 declared fields Plasm cannot translate") $"message: {e.Message}"
