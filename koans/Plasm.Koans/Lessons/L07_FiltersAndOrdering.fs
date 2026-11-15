/// Lesson 07: About filters and ordering.
module Plasm.Koans.Lessons.L07_FiltersAndOrdering

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private count (repo: Repo) (q: Query<Order>) =
    task {
        let! n = repo.Count q
        return ok n
    }

let lesson =
    Lesson.make
        "About filters and ordering"
        [ Koan.Task(
              "Comparing numbers",
              fun repo ->
                  task {
                      // A filter compares a field as F# would: Total is a decimal, so it is
                      // compared with a decimal (20m). count, defined at the top of this
                      // lesson, runs repo.Count: how many rows the query matches.
                      let! n = count repo (plasm { for o in tableOf<Order> do where (o.Total > 20m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Contains on text becomes LIKE",
              fun repo ->
                  task {
                      // s.Contains "o" is true when the text s has an "o" in it. In a filter it
                      // becomes SQL's LIKE, which matches text against a pattern.
                      let withO = plasm { for c in tableOf<Customer> do where (c.Name.Contains "o") }
                      (ok (repo.Explain withO)).Sql.Contains "LIKE" |> shouldBe __
                      let! customers = repo.All withO
                      ok customers |> List.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "And needs both",
              fun repo ->
                  task {
                      // && keeps a row only when both sides are true.
                      let! n = count repo (plasm { for o in tableOf<Order> do where (o.Status = Published && o.Total < 50m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Or needs either",
              fun repo ->
                  task {
                      // || keeps a row when either side is true, or both.
                      let! n = count repo (plasm { for o in tableOf<Order> do where (o.Status = Draft || o.Total > 50m) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Ordering largest first",
              fun repo ->
                  task {
                      // orderByDescending sorts the rows largest first.
                      let! orders = repo.All(plasm { for o in tableOf<Order> do orderByDescending o.Total })
                      ok orders |> List.map (fun o -> o.Total) |> shouldBe __
                  }
          )

          Koan.Task(
              "Independent filters combine",
              fun repo ->
                  task {
                      let! n =
                          count repo (plasm {
                              for o in tableOf<Order> do
                                  where (o.Status = Published)
                                  where (o.Total > 50m)
                          })

                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Comparing with None",
              fun repo ->
                  task {
                      let withoutCustomer = plasm { for o in tableOf<Order> do where (o.CustomerId = None) }
                      (ok (repo.Explain withoutCustomer)).Sql.Contains "IS NULL" |> shouldBe __
                      let! n = count repo withoutCustomer
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "not keeps F#'s meaning",
              fun repo ->
                  task {
                      // Orders that are not Ada's: in F#, an Order with no customer is not Ada's.
                      let notAdas = plasm { for o in tableOf<Order> do where (not (o.CustomerId = Some(CustomerId 1L))) }
                      let! n = count repo notAdas
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Membership in an empty list",
              fun repo ->
                  task {
                      let wanted: OrderStatus list = []
                      let! n = count repo (plasm { for o in tableOf<Order> do where (List.contains o.Status wanted) })
                      n |> shouldBe __
                  }
          )

          Koan.Task(
              "Some code cannot be translated",
              fun repo ->
                  task {
                      let shout (s: string) = s.ToUpperInvariant() + "!"
                      let! r = repo.All(plasm { for c in tableOf<Customer> do where (shout c.Name = "ADA!") })

                      let refused =
                          match r with
                          | Error(DbError.Translation(TranslationError.UnsupportedExpression _)) -> true
                          | _ -> false

                      refused |> shouldBe __
                  }
          )

          Koan.Task(
              "Pages need an order",
              fun repo ->
                  task {
                      let! unordered = repo.All(plasm { for o in tableOf<Order> do take 2 })
                      Result.isError unordered |> shouldBe __

                      let! page = repo.All(plasm { for o in tableOf<Order> do orderBy o.Total; take 2 })
                      ok page |> List.map (fun o -> o.Total) |> shouldBe __
                  }
          )

          Koan.Task(
              "Captured arrays are read each run",
              fun repo ->
                  task {
                      let statuses = [| Published |]
                      let matching = plasm { for o in tableOf<Order> do where (Array.contains o.Status statuses) }
                      let! before = count repo matching
                      statuses.[0] <- Draft
                      let! after = count repo matching
                      before |> shouldBe __
                      after |> shouldBe __
                  }
          ) ]
