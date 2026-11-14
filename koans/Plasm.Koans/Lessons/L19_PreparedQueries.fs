/// Lesson 19: About prepared queries.
module Plasm.Koans.Lessons.L19_PreparedQueries

open System
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private ordersOf =
    Query.prepare (fun (customer: Arg<CustomerId>) ->
        plasm {
            for o in Db.orders do
                where (o.CustomerId = Some customer.Value)
                orderBy o.Id
        })

let lesson =
    Lesson.make
        "About prepared queries"
        [ Koan.Task(
              "Built once, run many times",
              fun k ->
                  task {
                      let! adas = k.Repo.Count(ordersOf (CustomerId 1L))
                      let! bobs = k.Repo.Count(ordersOf (CustomerId 2L))
                      (ok adas, ok bobs) |> shouldBe __
                      // Did the two counts send the same SQL text?
                      (k.Commands.[0] = k.Commands.[1]) |> shouldBe __
                  }
          )

          Koan.Sync(
              "Arguments are parameters",
              fun k ->
                  let sql = k.Sql(ordersOf (CustomerId 42L))
                  sql.Contains "42" |> shouldBe __
          )

          Koan.Task(
              "Paging by arguments",
              fun k ->
                  task {
                      let page =
                          Query.prepare2 (fun (size: Arg<int>) (offset: Arg<int>) ->
                              plasm {
                                  for o in Db.orders do
                                      orderBy o.Id
                                      skip offset.Value
                                      take size.Value
                              })

                      let! second = k.Repo.All(page 2 2)
                      ok second |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Prepared queries still compose",
              fun k ->
                  task {
                      let publishedOf c = plasm { for o in ordersOf c do where (o.Status = Published) }
                      let! n = k.Repo.Count(publishedOf (CustomerId 1L))
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
                              plasm { for o in Db.orders do where (o.CustomerId = Some id) })
                          |> ignore

                          false
                      with :? InvalidOperationException ->
                          true

                  refused |> shouldBe __
          )

          Koan.Task(
              "Prepared load queries",
              fun k ->
                  task {
                      let paymentsWith =
                          Query.prepare (fun (status: Arg<PaymentStatus>) -> plasm { for p in Db.payments do where (p.Status = status.Value) })

                      let! orders = k.Repo.All(plasm { for o in Db.orders do where (o.Id = OrderId 3L); preload o.Payments (paymentsWith Failed) })
                      (ok orders).Head.Payments |> Assoc.get |> List.map (fun p -> p.Status) |> shouldBe __
                  }
          ) ]
