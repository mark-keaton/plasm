/// Lesson 07: About filters and ordering.
module Plasm.Koans.Lessons.L07_FiltersAndOrdering

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private count (k: KoanContext) (q: Query<Order>) =
    task {
        let! n = k.Repo.Count q
        return ok n
    }

let lesson =
    Lesson.make
        "About filters and ordering"
        [ Koan.Task(
              "Comparing numbers",
              fun k ->
                  task {
                      // A filter compares a field as F# would: Total is a decimal, so it is
                      // compared with a decimal (20m). count, defined at the top of this
                      // lesson, runs k.Repo.Count: how many rows the query matches.
                      let! n = count k (plasm { for o in Db.orders do where (o.Total > 20m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Contains on text becomes LIKE",
              fun k ->
                  task {
                      // s.Contains "o" is true when the text s has an "o" in it. In a filter it
                      // becomes SQL's LIKE, which matches text against a pattern.
                      let withO = plasm { for c in Db.customers do where (c.Name.Contains "o") }
                      (k.Sql withO).Contains "LIKE" |> shouldBe __
                      let! customers = k.Repo.All withO
                      ok customers |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "And needs both",
              fun k ->
                  task {
                      // && keeps a row only when both sides are true.
                      let! n = count k (plasm { for o in Db.orders do where (o.Status = Published && o.Total < 50m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Or needs either",
              fun k ->
                  task {
                      // || keeps a row when either side is true, or both.
                      let! n = count k (plasm { for o in Db.orders do where (o.Status = Draft || o.Total > 50m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Ordering largest first",
              fun k ->
                  task {
                      // orderByDescending sorts the rows largest first.
                      let! orders = k.Repo.All(plasm { for o in Db.orders do orderByDescending o.Total })
                      ok orders |> List.map (fun o -> o.Total) |> shouldBe __
                  }
          )

          Koan.Task(
              "Independent filters combine",
              fun k ->
                  task {
                      let! n =
                          count k (plasm {
                              for o in Db.orders do
                                  where (o.Status = Published)
                                  where (o.Total > 50m)
                          })

                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Comparing with None",
              fun k ->
                  task {
                      let withoutCustomer = plasm { for o in Db.orders do where (o.CustomerId = None) }
                      (k.Sql withoutCustomer).Contains "IS NULL" |> shouldBe __
                      let! n = count k withoutCustomer
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "not keeps F#'s meaning",
              fun k ->
                  task {
                      // Orders that are not Ada's: in F#, an Order with no customer is not Ada's.
                      let notAdas = plasm { for o in Db.orders do where (not (o.CustomerId = Some(CustomerId 1L))) }
                      let! n = count k notAdas
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Membership in an empty list",
              fun k ->
                  task {
                      let wanted: OrderStatus list = []
                      let! n = count k (plasm { for o in Db.orders do where (List.contains o.Status wanted) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Some code cannot be translated",
              fun k ->
                  task {
                      let shout (s: string) = s.ToUpperInvariant() + "!"
                      let! r = k.Repo.All(plasm { for c in Db.customers do where (shout c.Name = "ADA!") })

                      let refused =
                          match r with
                          | Error(DbError.Translation(TranslationError.UnsupportedExpression _)) -> true
                          | _ -> false

                      refused |> shouldBe __
                  }
          )

          Koan.Task(
              "Pages need an order",
              fun k ->
                  task {
                      let! unordered = k.Repo.All(plasm { for o in Db.orders do take 2 })
                      Result.isError unordered |> shouldBe __

                      let! page = k.Repo.All(plasm { for o in Db.orders do orderBy o.Total; take 2 })
                      ok page |> List.map (fun o -> o.Total) |> shouldBe __
                  }
          )

          Koan.Task(
              "Captured arrays are read each run",
              fun k ->
                  task {
                      let statuses = [| Published |]
                      let matching = plasm { for o in Db.orders do where (Array.contains o.Status statuses) }
                      let! before = count k matching
                      statuses.[0] <- Draft
                      let! after = count k matching
                      before |> shouldBe __
                      after |> shouldBe __
                  }
          ) ]
