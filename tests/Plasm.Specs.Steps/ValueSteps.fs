namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Rule: Persistence descriptions are values and execution is explicit
[<Binding>]
type ValueSteps(w: World) =

    // Scenario: Constructing a query performs no database operations

    [<StepDefinition("I construct a query for Orders")>]
    member _.ConstructQuery() =
        w.MarkCommands()
        w.Set("query", plasm { for o in Schemas.orders do where (o.Total > 0m) })

    [<StepDefinition("add filters, sorting, projections, and relationship loads")>]
    member _.AddEverything() =
        let q: Query<Order> = w.Get "query"

        let extended =
            plasm {
                for o in q do
                    where (o.Status = Published)
                    orderByDescending o.PlacedAt
                    take 10
            }

        let projected =
            plasm {
                for o in q do
                    orderBy o.Id
                    select (o.Id, o.Total)
            }

        w.Set("query", extended)
        w.Set("projection", projected)

    [<StepDefinition("the query can be passed to another function")>]
    member _.PassToFunction() =
        let describe (q: Query<Order>) = q.FilterCount
        let q: Query<Order> = w.Get "query"
        equal 2 (describe q) "described query"
        // Inspection is a further function over the value; still no database
        // access. The relationship load is the OrderLines the Order owns.
        let plan = w.Repo.Explain q |> ok
        equal [ "Lines" ] plan.LoadPaths "plan paths"
        equal 0 w.CommandsSinceMark.Length "commands"

    // Scenario: Executing a query does not mutate its description

    [<StepDefinition("a query for published Orders")>]
    member _.PublishedQuery() =
        task {
            let! c = Seed.customer w.Db "Ada" "ada@example.com"
            let! _ = Seed.order w.Db (Some c) "published" 10m (Seed.at 1)
            let! _ = Seed.order w.Db (Some c) "published" 20m (Seed.at 2)
            let! _ = Seed.order w.Db (Some c) "draft" 30m (Seed.at 3)
            let q = plasm { for o in published do orderBy o.PlacedAt }
            w.Set("query", q)
            w.Set("before", (string q, (w.Repo.Explain q |> ok).Sql))

            w.Pending <-
                fun () ->
                    task {
                        let! r = w.Repo.All q
                        w.Set("first", ok r)
                    }
        }
        |> unit

    [<StepDefinition("the query description remains unchanged")>]
    member _.Unchanged() =
        let q: Query<Order> = w.Get "query"
        equal (w.Get<string * string> "before") (string q, (w.Repo.Explain q |> ok).Sql) "query description"

    [<StepDefinition("it can be executed again")>]
    member _.ExecuteAgain() =
        task {
            let q: Query<Order> = w.Get "query"
            let! again = w.Repo.All q
            let first: Order list = w.Get "first"
            equal 2 first.Length "first result"
            equal first (ok again) "second result"
        }
        |> unit

    // Scenario: Extending a query preserves the original query

    [<StepDefinition("a base query for published Orders")>]
    member _.BaseQuery() =
        task {
            let! c1 = Seed.customer w.Db "Ada" "ada@example.com"
            let! c2 = Seed.customer w.Db "Bob" "bob@example.com"
            let! _ = Seed.order w.Db (Some c1) "published" 10m (Seed.at 1)
            let! _ = Seed.order w.Db (Some c2) "published" 20m (Seed.at 2)
            let! _ = Seed.order w.Db (Some c2) "draft" 30m (Seed.at 3)
            w.Set("c1", c1)
            w.Set("c2", c2)
            w.Set("base", published)
            w.Set("baseSql", (w.Repo.Explain published |> ok).Sql)
        }
        |> unit

    [<StepDefinition("I derive one query for a Customer")>]
    member _.DeriveOne() =
        w.Set("q1", forCustomer (w.Get "c1") (w.Get "base"))

    [<StepDefinition("derive another query for a different Customer")>]
    member _.DeriveAnother() =
        w.Set("q2", forCustomer (w.Get "c2") (w.Get "base"))

    [<StepDefinition("neither derived query changes the base query")>]
    member _.BaseUnchanged() =
        task {
            let b: Query<Order> = w.Get "base"
            equal 1 b.FilterCount "base filters"
            equal (w.Get<string> "baseSql") (w.Repo.Explain b |> ok).Sql "base SQL"
            let! all = w.Repo.All b
            equal 2 (ok all).Length "published orders through the base query"
        }
        |> unit

    [<StepDefinition("neither derived query changes the other")>]
    member _.DerivedIndependent() =
        task {
            let q1: Query<Order> = w.Get "q1"
            let q2: Query<Order> = w.Get "q2"
            equal 2 q1.FilterCount "q1 filters"
            equal 2 q2.FilterCount "q2 filters"
            let! r1 = w.Repo.All q1
            let! r2 = w.Repo.All q2
            equal [ Some(w.Get<CustomerId> "c1") ] (ok r1 |> List.map (fun o -> o.CustomerId)) "q1 customers"
            equal [ Some(w.Get<CustomerId> "c2") ] (ok r2 |> List.map (fun o -> o.CustomerId)) "q2 customers"
        }
        |> unit

    // Scenario: Constructing a change performs no database operations
    // (Given an existing Order is shared with the changeset rule.)

    [<StepDefinition("an existing Order")>]
    member _.ExistingOrder() =
        task {
            let! c = Seed.customer w.Db "Ada" "ada@example.com"
            let! id = Seed.order w.Db (Some c) "draft" 10m (Seed.at 1)
            let! o = w.Repo.One(plasm { for o in Schemas.orders do where (o.Id = id) })
            w.Set("order", (ok o).Value)
        }
        |> unit

    [<StepDefinition("I describe a change to its status")>]
    member _.DescribeChange() =
        w.MarkCommands()
        let o: Order = w.Get "order"
        w.Set("changeset", o |> Changeset.edit (fun o -> { o with Status = Published }))

    [<StepDefinition("the Order remains unchanged")>]
    member _.OrderUnchanged() =
        let o: Order = w.Get "order"
        equal Draft o.Status "original status"
        let cs: Changeset<Order> = w.Get "changeset"
        equal (Some o) cs.Data "changeset data"
