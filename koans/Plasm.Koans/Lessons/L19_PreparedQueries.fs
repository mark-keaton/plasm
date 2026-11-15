/// Lesson 19: About prepared queries.
module Plasm.Koans.Lessons.L19_PreparedQueries

open System
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private ordersOf =
    Query.prepare (fun (customer: Arg<CustomerId>) ->
        plasm {
            for o in tableOf<Order> do
                where (o.CustomerId = Some customer.Value)
                orderBy o.Id
        })

/// A prepared query: built once, then called with a SKU to give the query for that SKU.
/// An Arg<string> stands for the value given at each call; `.Value` reads it inside the query.
let private productsWithSku =
    Query.prepare (fun (sku: Arg<string>) -> plasm { for p in tableOf<Product> do where (p.Sku = sku.Value) })

let private names (r: Result<Product list, DbError>) = ok r |> List.map (fun p -> p.Name)

let lesson =
    Lesson.make
        "About prepared queries"
        [ Koan.Task(
              "A prepared query takes an argument",
              fun repo ->
                  task {
                      // productsWithSku (top of this file) is a function: give it a SKU, get a query.
                      let! gizmos = repo.All(productsWithSku "GIZMO")
                      names gizmos |> shouldBe __
                  }
          )

          Koan.Task(
              "One prepared query, two values",
              fun repo ->
                  task {
                      let! widgets = repo.All(productsWithSku "WIDGET")
                      let! gadgets = repo.All(productsWithSku "GADGET")
                      names widgets |> shouldBe __
                      names gadgets |> shouldBe __
                  }
          )

          Koan.Task(
              "A prepared query can be counted",
              fun repo ->
                  task {
                      // ordersOf (top of this file) is prepared with a CustomerId. Customer 3 is Cy.
                      let! cys = repo.Count(ordersOf (CustomerId 3L))
                      ok cys |> shouldBe __
                  }
          )

          Koan.Task(
              "Built once, run many times",
              fun repo ->
                  task {
                      let! adas = repo.Count(ordersOf (CustomerId 1L))
                      let! bobs = repo.Count(ordersOf (CustomerId 2L))
                      (ok adas, ok bobs) |> shouldBe __
                      // Did the two counts send the same SQL text?
                      ((commands ()).[0] = (commands ()).[1]) |> shouldBe __
                  }
          )

          Koan.Sync(
              "Arguments are parameters",
              fun repo ->
                  let sql = (ok (repo.Explain(ordersOf (CustomerId 42L)))).Sql
                  sql.Contains "42" |> shouldBe __
          )

          Koan.Task(
              "Paging by arguments",
              fun repo ->
                  task {
                      let page =
                          Query.prepare2 (fun (size: Arg<int>) (offset: Arg<int>) ->
                              plasm {
                                  for o in tableOf<Order> do
                                      orderBy o.Id
                                      skip offset.Value
                                      take size.Value
                              })

                      let! second = repo.All(page 2 2)
                      ok second |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Prepared queries still compose",
              fun repo ->
                  task {
                      let publishedOf c = plasm { for o in ordersOf c do where (o.Status = Published) }
                      let! n = repo.Count(publishedOf (CustomerId 1L))
                      ok n |> shouldBe __
                  }
          )

          Koan.Sync(
              "Arguments are read only inside the query",
              fun _ ->
                  let refused =
                      try
                          Query.prepare (fun (customer: Arg<CustomerId>) ->
                              let id = customer.Value // read while the query is being built
                              plasm { for o in tableOf<Order> do where (o.CustomerId = Some id) })
                          |> ignore

                          false
                      with :? InvalidOperationException ->
                          true

                  refused |> shouldBe __
          )

          Koan.Task(
              "An exists takes an argument too",
              fun repo ->
                  task {
                      // An inner query inside exists can read the prepared query's
                      // arguments: the orders with a payment of at least an amount.
                      let paidAtLeast =
                          Query.prepare (fun (least: Arg<decimal>) ->
                              plasm {
                                  for o in tableOf<Order> do
                                      where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Amount >= least.Value) }))
                                      orderBy o.Id
                              })

                      let! big = repo.All(paidAtLeast 50m)
                      ok big |> List.map (fun o -> o.Id) |> shouldBe __
                      let! any = repo.All(paidAtLeast 0m)
                      ok any |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          ) ]
