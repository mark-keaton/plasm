/// Lesson 04: About reading.
module Plasm.Koans.Lessons.L04_Reading

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "About reading"
        [ Koan.Task(
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
