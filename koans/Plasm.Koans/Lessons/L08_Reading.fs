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
              fun k ->
                  task {
                      // k.Repo.All returns a Result: Ok with an F# list of the matching
                      // records, or Error saying what went wrong. ok takes the list out.
                      let! products = k.Repo.All(plasm { for p in Db.products do orderBy p.Id })
                      let products: Product list = ok products
                      products |> List.map (fun p -> p.Sku) |> shouldBe __
                  }
          )

          Koan.Task(
              "One returns an option",
              fun k ->
                  task {
                      // k.Repo.One reads at most one record. Its value is an option:
                      // Some record when a row matches, None when none does.
                      let! cy = k.Repo.One(plasm { for c in Db.customers do where (c.Email = "cy@example.com") })
                      ok cy |> Option.map (fun c -> c.Name) |> shouldBe __
                  }
          )

          Koan.Task(
              "Count returns an int64",
              fun k ->
                  task {
                      // k.Repo.Count returns how many rows match, as an int64
                      // (a 64-bit integer, written with an L: 7L).
                      let! n = k.Repo.Count(Query.from Db.products)
                      ok n |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting one field",
              fun k ->
                  task {
                      // select says what to read for each row: here only the Name,
                      // so the result is a list of strings, not of Customers.
                      let! names = k.Repo.All(plasm { for c in Db.customers do orderBy c.Id; select c.Name })
                      ok names |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading returns typed rows",
              fun k ->
                  task {
                      let! orders = k.Repo.All(plasm { for o in Db.orders do orderBy o.Id })
                      let first = (ok orders).Head
                      // Total is a decimal; Status is an OrderStatus, not a string.
                      first.Total |> shouldBe __
                      first.Status |> shouldBe __
                  }
          )

          Koan.Task(
              "Counting transfers no rows",
              fun k ->
                  task {
                      let! n = k.Repo.Count(plasm { for o in Db.orders do where (o.Status = Published) })
                      ok n |> shouldBe __
                      k.Commands.Head.Contains "COUNT(*)" |> shouldBe __
                  }
          )

          Koan.Task(
              "Asking whether anything exists",
              fun k ->
                  task {
                      let! any = k.Repo.Exists(plasm { for o in Db.orders do where (o.Total > 1000m) })
                      ok any |> shouldBe __
                  }
          )

          Koan.Task(
              "Selecting only what you need",
              fun k ->
                  task {
                      let totals = plasm { for o in Db.orders do orderBy o.Id; select (o.Id, o.Total) }
                      let! rows = k.Repo.All totals
                      (ok rows).Head |> shouldBe __
                      // Is the status column read?
                      k.Commands.Head.Contains "\"status\"" |> shouldBe __
                  }
          )

          Koan.Task(
              "Leaving the database is explicit",
              fun k ->
                  task {
                      let! published = k.Repo.All(plasm { for o in Db.orders do where (o.Status = Published) })
                      // From here on it is an ordinary F# list, in memory.
                      ok published |> List.sumBy (fun o -> o.Total) |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "One means at most one",
              fun k ->
                  task {
                      let! largest = k.Repo.One(plasm { for o in Db.orders do orderByDescending o.Total; take 1 })
                      (ok largest |> Option.map (fun o -> o.Total)) |> shouldBe __
                      let! none = k.Repo.One(plasm { for o in Db.orders do where (o.Total > 1000m) })
                      ok none |> shouldBe __
                  }
          ) ]
