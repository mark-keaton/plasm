/// Lesson 08: About reading.
module Plasm.Koans.Lessons.L08_Reading

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About reading"
        [ Koan.Task(
              "All returns a list",
              fun repo ->
                  task {
                      // repo.All returns a Result: Ok with an F# list of the matching
                      // records, or Error saying what went wrong. ok takes the list out.
                      let! products = repo.All(plasm { for p in Db.products do orderBy p.Id })
                      let products: Product list = ok products
                      products |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "One returns an option",
              fun repo ->
                  task {
                      // repo.One reads at most one record. Its value is an option:
                      // Some record when a row matches, None when none does.
                      let! cy = repo.One(plasm { for c in Db.customers do where (c.Email = "cy@example.com") })
                      ok cy |> Option.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Count returns an int64",
              fun repo ->
                  task {
                      // repo.Count returns how many rows match, as an int64
                      // (a 64-bit integer, written with an L: 7L).
                      let! n = repo.Count(Query.from Db.products)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting one field",
              fun repo ->
                  task {
                      // select says what to read for each row: here only the Name,
                      // so the result is a list of strings, not of Customers.
                      let! names = repo.All(plasm { for c in Db.customers do orderBy c.Id; select c.Name })
                      ok names |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading returns typed rows",
              fun repo ->
                  task {
                      let! orders = repo.All(plasm { for o in Db.orders do orderBy o.Id })
                      let first = (ok orders).Head
                      // Total is a decimal; Status is an OrderStatus, not a string.
                      first.Total |> shouldBe __
                      first.Status |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting transfers no rows",
              fun repo ->
                  task {
                      let! n = repo.Count(plasm { for o in Db.orders do where (o.Status = Published) })
                      ok n |> shouldBe __
                      (commands ()).Head.Contains "COUNT(*)" |> shouldBe __
                  }
          )

          Koan.Task(
              "Asking whether anything exists",
              fun repo ->
                  task {
                      let! any = repo.Exists(plasm { for o in Db.orders do where (o.Total > 1000m) })
                      ok any |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting only what you need",
              fun repo ->
                  task {
                      let totals = plasm { for o in Db.orders do orderBy o.Id; select (o.Id, o.Total) }
                      let! rows = repo.All totals
                      (ok rows).Head |> shouldBe __
                      // Is the status column read?
                      (commands ()).Head.Contains "\"status\"" |> shouldBe __
                  }
          )

          Koan.Task(
              "Leaving the database is explicit",
              fun repo ->
                  task {
                      let! published = repo.All(plasm { for o in Db.orders do where (o.Status = Published) })
                      // From here on it is an ordinary F# list, in memory.
                      ok published |> List.sumBy (fun o -> o.Total) |> shouldBe __
                      (commands ()).Length |> shouldBe __
                  }
          )

          Koan.Task(
              "One means at most one",
              fun repo ->
                  task {
                      let! largest = repo.One(plasm { for o in Db.orders do orderByDescending o.Total; take 1 })
                      (ok largest |> Option.map (fun o -> o.Total)) |> shouldBe __
                      let! none = repo.One(plasm { for o in Db.orders do where (o.Total > 1000m) })
                      ok none |> shouldBe __
                  }
          ) ]
