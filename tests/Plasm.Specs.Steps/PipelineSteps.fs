namespace Plasm.Specs

open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// One way of writing a query: what it explains to, and what it returns.
type private Written =
    { Plan: unit -> Result<string list, TranslationError>
      Rows: unit -> Task<string> }

/// Feature: Every query operation has a pipeline form
[<Binding>]
type PipelineSteps(w: World) =

    let ofQuery (q: Query<'T>) =
        { Plan =
            fun () ->
                w.Repo.Explain q
                |> Result.map (fun p ->
                    let rec loads (steps: LoadStep list) =
                        steps |> List.collect (fun l -> $"{l.Path}: {l.Sql}" :: loads l.Children)

                    p.Sql :: loads p.Loads)
          Rows =
            fun () ->
                task {
                    let! r = w.Repo.All q
                    return sprintf "%A" r
                } }

    let ofProjection (p: Projection<'R>) =
        { Plan = fun () -> w.Repo.Explain p |> Result.map (fun p -> [ p.Sql ])
          Rows =
            fun () ->
                task {
                    let! r = w.Repo.All p
                    return sprintf "%A" r
                } }

    let lines = plasm { for l in Schemas.orderLines do orderBy l.Id; preload l.Product }

    let pageCe =
        Query.prepare2 (fun (size: Arg<int>) (offset: Arg<int>) ->
            plasm {
                for o in Schemas.orders do
                    orderBy o.Id
                    skip offset.Value
                    take size.Value
            })

    let pagePipeline =
        Query.prepare2 (fun (size: Arg<int>) (offset: Arg<int>) ->
            Query.from Schemas.orders
            |> Query.orderBy (fun o -> o.Id)
            |> Query.skip offset.Value
            |> Query.take size.Value)

    let pair (operation: string) : Written * Written =
        match operation with
        | "thenBy" ->
            ofQuery (plasm { for o in Schemas.orders do orderBy o.Status; thenBy o.Id }),
            ofQuery (Query.from Schemas.orders |> Query.orderBy (fun o -> o.Status) |> Query.thenBy (fun o -> o.Id))
        | "thenByDescending" ->
            ofQuery (plasm { for o in Schemas.orders do orderBy o.Status; thenByDescending o.Total }),
            ofQuery (
                Query.from Schemas.orders
                |> Query.orderBy (fun o -> o.Status)
                |> Query.thenByDescending (fun o -> o.Total)
            )
        | "preload with a child query" ->
            ofQuery (plasm { for o in Schemas.orders do orderBy o.Id; preload o.Lines lines }),
            ofQuery (Query.from Schemas.orders |> Query.orderBy (fun o -> o.Id) |> Query.preload ((fun o -> o.Lines), lines))
        | "replacePreload" ->
            ofQuery (plasm { for o in Schemas.orders do orderBy o.Id; preload o.Lines; replacePreload o.Lines lines }),
            ofQuery (
                Query.from Schemas.orders
                |> Query.orderBy (fun o -> o.Id)
                |> Query.preload (fun o -> o.Lines)
                |> Query.replacePreload ((fun o -> o.Lines), lines)
            )
        | "limitPerParent" ->
            let latest = plasm { for p in Schemas.payments do orderByDescending p.CreatedAt; limitPerParent 1 }

            let latestPipeline =
                Query.from Schemas.payments |> Query.orderByDescending (fun p -> p.CreatedAt) |> Query.limitPerParent 1

            ofQuery (plasm { for o in Schemas.orders do orderBy o.Id; preload o.Payments latest }),
            ofQuery (Query.from Schemas.orders |> Query.orderBy (fun o -> o.Id) |> Query.preload ((fun o -> o.Payments), latestPipeline))
        | "take and skip from arguments" -> ofQuery (pageCe 2 1), ofQuery (pagePipeline 2 1)
        | "select" ->
            ofProjection (plasm { for o in Schemas.orders do orderBy o.Id; select (o.Id, o.Total) }),
            ofProjection (Query.from Schemas.orders |> Query.orderBy (fun o -> o.Id) |> Query.select (fun o -> (o.Id, o.Total)))
        | "join" ->
            ofQuery (
                plasm {
                    for o in Schemas.orders do
                        join c in Schemas.customers on (o.CustomerId = Some c.Id)
                        where (c.Name = "Ada")
                        orderBy o.Id
                }
            ),
            ofQuery (
                Query.from Schemas.orders
                |> Query.join (Schemas.customers, (fun o -> o.CustomerId), (fun c -> Some c.Id))
                |> Query.where (fun (o, c) -> c.Name = "Ada")
                |> Query.orderBy (fun (o, c) -> o.Id)
            )
        | "leftJoin" ->
            ofQuery (
                plasm {
                    for o in Schemas.orders do
                        leftJoin c in Schemas.customers on (o.CustomerId = Some c.Id)
                        orderBy o.Id
                }
            ),
            ofQuery (
                Query.from Schemas.orders
                |> Query.leftJoin (Schemas.customers, (fun o -> o.CustomerId), (fun c -> Some c.Id))
                |> Query.orderBy (fun (o, c) -> o.Id)
            )
        | "groupBy and having" ->
            ofProjection (
                plasm {
                    for o in Schemas.orders do
                        where o.CustomerId.IsSome
                        groupBy o.CustomerId
                        having (Agg.count () > 1L)
                        orderBy o.CustomerId
                        select (o.CustomerId, Agg.count ())
                }
            ),
            ofProjection (
                Query.from Schemas.orders
                |> Query.where (fun o -> o.CustomerId.IsSome)
                |> Query.groupBy (fun o -> o.CustomerId)
                |> Query.having (fun o -> Agg.count () > 1L)
                |> Query.orderBy (fun o -> o.CustomerId)
                |> Query.select (fun o -> (o.CustomerId, Agg.count ()))
            )
        | op -> failwithf "unknown operation %s" op

    [<StepDefinition("I write a query using (.*) both as plasm \\{ \\} and as a Query pipeline")>]
    member _.Write(operation: string) =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! o1 = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db (Some ada) "draft" 20m (Seed.at 2)
            let! _ = Seed.order w.Db (Some bob) "published" 30m (Seed.at 3)
            let! _ = Seed.order w.Db None "draft" 40m (Seed.at 4)
            let! p1 = Seed.product w.Db "A-1"
            let! p2 = Seed.product w.Db "B-2"
            let! _ = Seed.line w.Db o1 p1 1 5m
            let! _ = Seed.line w.Db o1 p2 2 2.5m
            let! _ = Seed.line w.Db o2 p1 3 5m
            let! _ = Seed.payment w.Db o1 5m "succeeded" (Seed.at 1)
            let! _ = Seed.payment w.Db o1 5m "failed" (Seed.at 2)
            w.Set("pair", pair operation)
        }
        |> unit

    [<StepDefinition("both produce the same SQL and the same load plan")>]
    member _.SameSql() =
        let ce, pipeline = w.Get<Written * Written> "pair"
        let a = ce.Plan() |> ok
        let b = pipeline.Plan() |> ok
        equal a b "SQL and loads of the pipeline compared with plasm { }"

    [<StepDefinition("both return the same rows")>]
    member _.SameRows() =
        task {
            let ce, pipeline = w.Get<Written * Written> "pair"
            let! a = ce.Rows()
            let! b = pipeline.Rows()
            check (a.StartsWith "Ok") $"the query failed: {a}"
            equal a b "rows of the pipeline compared with plasm { }"
        }
        |> unit

    [<StepDefinition("I join Orders to OrderLines and OrderLines to Products in a pipeline and filter on the Product")>]
    member this.ChainedJoins() =
        task {
            do! this.Write "thenBy"

            let pipeline =
                Query.from Schemas.orders
                |> Query.join (Schemas.orderLines, (fun o -> o.Id), (fun l -> l.OrderId))
                |> Query.join (Schemas.products, (fun (o, l) -> l.ProductId), (fun p -> p.Id))
                |> Query.where (fun ((o, l), p) -> p.Sku = "A-1")
                |> Query.orderBy (fun ((o, l), p) -> l.Id)

            let expression =
                plasm {
                    for o in Schemas.orders do
                        join l in Schemas.orderLines on (o.Id = l.OrderId)
                        join p in Schemas.products on (l.ProductId = p.Id)
                        where (p.Sku = "A-1")
                        orderBy l.Id
                }

            w.Set("chained", (pipeline, expression))
        }
        |> unit

    [<StepDefinition("the SQL equals the same plasm \\{ \\} query with two joins")>]
    member _.ChainedSql() =
        let pipeline, expression = w.Get<Query<(Order * OrderLine) * Product> * Query<Order * OrderLine * Product>> "chained"
        equal (w.Repo.Explain expression |> ok).Sql (w.Repo.Explain pipeline |> ok).Sql "SQL of the chained pipeline"

    [<StepDefinition("the rows are the same Order, OrderLine and Product triples")>]
    member _.ChainedRows() =
        task {
            let pipeline, expression = w.Get<Query<(Order * OrderLine) * Product> * Query<Order * OrderLine * Product>> "chained"
            let! a = w.Repo.All pipeline
            let! b = w.Repo.All expression
            let flat = ok a |> List.map (fun ((o, l), p) -> o, l, p)
            check (not flat.IsEmpty) "the chained join returned no rows"
            equal (ok b) flat "rows of the chained pipeline"
        }
        |> unit
