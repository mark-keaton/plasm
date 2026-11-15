/// Lesson 22: About unions with fields.
module Plasm.Koans.Lessons.L22_UnionsWithFields

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private deliveryOf (order: int64) =
    plasm { for d in Db.deliveries do where (d.OrderId = OrderId order) }

let private orderIds (q: Query<Delivery>) (repo: Repo) =
    task {
        let! rows = repo.All q
        return ok rows |> List.map (fun d -> d.OrderId) |> List.sort
    }

let lesson =
    Lesson.make
        "About unions with fields"
        [ Koan.Task(
              "A union with fields is read back whole",
              fun repo ->
                  task {
                      // A Delivery's Stage is a union whose cases carry facts. Plasm
                      // stores it across columns: the case's name in stage, and each
                      // case's field in stage_<field>. Reading gives the union back.
                      let! d = repo.One(deliveryOf 3L)
                      (ok d).Value.Stage |> shouldBe __
                  }
          )

          Koan.Task(
              "A case test is a query",
              fun repo ->
                  task {
                      // Matching on a case becomes `stage = 'delivered'`.
                      let delivered =
                          plasm {
                              for d in Db.deliveries do
                                  where (
                                      match d.Stage with
                                      | Delivered _ -> true
                                      | _ -> false
                                  )
                          }

                      let! ids = orderIds delivered repo
                      ids |> shouldBe __
                  }
          )

          Koan.Task(
              "Matching on a case's field",
              fun repo ->
                  task {
                      // A case's field is its own column, so a match can test it.
                      let bySwift =
                          plasm {
                              for d in Db.deliveries do
                                  where (
                                      match d.Stage with
                                      | InTransit carrier -> carrier = "Swift"
                                      | _ -> false
                                  )
                          }

                      let! ids = orderIds bySwift repo
                      ids |> shouldBe __
                  }
          )

          Koan.Task(
              "The domain's own member in a query",
              fun repo ->
                  task {
                      // Delivery.Carrier is written in the domain, marked
                      // [<ReflectedDefinition>]. Plasm reads its definition and
                      // translates it, so the query says what the domain says.
                      let carried = plasm { for d in Db.deliveries do where (d.Carrier = Some "Swift") }
                      let! ids = orderIds carried repo
                      ids |> shouldBe __
                  }
          )

          Koan.Task(
              "Comparing with a whole value",
              fun repo ->
                  task {
                      // Comparing with a case compares the case and each of its fields.
                      let signedByAda = plasm { for d in Db.deliveries do where (d.Stage = Delivered "Ada") }
                      let! ids = orderIds signedByAda repo
                      ids |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting by stage",
              fun repo ->
                  task {
                      // StageName is the domain's member too; grouping by it groups by case.
                      let byStage =
                          plasm {
                              for d in Db.deliveries do
                                  groupBy d.StageName
                                  select (d.StageName, Agg.count ())
                          }

                      let! rows = repo.All byStage
                      ok rows |> List.sort |> shouldBe __
                  }
          )

          Koan.Task(
              "Changing the stage clears the old stage's columns",
              fun repo ->
                  task {
                      let! read = repo.One(deliveryOf 3L)
                      let d = (ok read).Value
                      let! r = repo.Transact(Tx.update (d, { d with Stage = Delivered "Bob" }): Tx<Delivery, unit>)
                      ok r |> ignore
                      // The carrier belonged to InTransit; Delivered has no carrier.
                      let! carrier = repo.Sql<string option>($"select stage_carrier from deliveries where order_id = {3L}")
                      ok carrier |> shouldBe __
                  }
          )

          Koan.Task(
              "A row that is no case is refused",
              fun repo ->
                  task {
                      // A stored stage that is none of the cases cannot become a
                      // DeliveryStage, so reading it is an error naming the field.
                      let lose: Tx<int, unit> = Tx.executeSql ($"update deliveries set stage = 'lost' where order_id = {2L}")
                      let! _ = repo.Transact lose
                      let! rows = repo.All(Query.from Db.deliveries)

                      let refused =
                          match rows with
                          | Error e -> (string e).Contains "Stage"
                          | Ok _ -> false

                      refused |> shouldBe __
                  }
          ) ]
