namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// window-functions.feature: values over a window of rows.
[<Binding>]
type WindowSteps(w: World) =

    let name (id: CustomerId option) =
        if id = Some(w.Get<CustomerId> "ada") then "Ada" else "Bob"

    let opt (v: decimal option) =
        match v with
        | Some d -> string (int d)
        | None -> "none"

    let refused (r: Result<'a, DbError>) =
        match r with
        | Error(DbError.Translation e) -> w.Set("refusal", sprintf "%A" e)
        | Ok _ -> w.Set("refusal", "it was not refused")
        | Error e -> w.Set("refusal", sprintf "another error: %A" e)

    [<StepDefinition("Ada with Orders of 10, 40 and 40 and Bob with an Order of 20, on days 1 to 4")>]
    member _.Seed() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! _ = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! _ = Seed.order w.Db (Some bob) "published" 20m (Seed.at 2)
            let! _ = Seed.order w.Db (Some ada) "published" 40m (Seed.at 3)
            let! _ = Seed.order w.Db (Some ada) "published" 40m (Seed.at 4)
            w.Set("ada", ada)
            w.Set("bob", bob)
        }
        :> Task

    [<StepDefinition("I read each Order's total with its number, rank and dense rank by total, its running total, and the totals before and after it, within its customer by day")>]
    member _.Ranked() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            orderBy o.CustomerId
                            thenBy o.PlacedAt
                            select (
                                o.CustomerId,
                                o.Total,
                                Win.rowNumber (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt),
                                Win.rank (Win.partition o.CustomerId |> Win.orderBy o.Total),
                                Win.denseRank (Win.partition o.CustomerId |> Win.orderBy o.Total),
                                Win.runningSum o.Total (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt),
                                Win.lag o.Total (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt),
                                Win.lead o.Total (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt)
                            )
                    }
                )

            let rows =
                ok r
                |> List.map (fun (c, t, n, rk, d, run, before, after) ->
                    [ name c; string (int t); string n; string rk; string d; string (int run); opt before; opt after ])

            w.Set("rows", rows)
        }
        :> Task

    [<StepDefinition("the rows are")>]
    member _.Rows(table: DataTable) =
        let expected = [ for row in table.Rows -> [ for v in row.Values -> v ] ]
        equal expected (w.Get<string list list> "rows") "the rows"

    [<StepDefinition("I read each Order's total with the sum, average and count of all Orders, and of its customer's")>]
    member _.Whole() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            select (
                                o.CustomerId,
                                Win.sum o.Total Win.whole,
                                Win.avg o.Total Win.whole,
                                Win.count Win.whole,
                                Win.sum o.Total (Win.partition o.CustomerId),
                                Win.count (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt)
                            )
                    }
                )

            w.Set("whole", ok r)
        }
        :> Task

    [<StepDefinition("every row has the sum 110, the average 27.5 and the count 4")>]
    member _.WholeRows() =
        for _, s, a, n, _, _ in w.Get<(CustomerId option * decimal * decimal option * int64 * decimal * int64) list> "whole" do
            equal (110m, Some 27.5m, 4L) (s, a, n) "the whole window"

    [<StepDefinition("Ada's rows have the sum 90 and the count 3, and Bob's the sum 20 and the count 1")>]
    member _.PartRows() =
        let rows = w.Get<(CustomerId option * decimal * decimal option * int64 * decimal * int64) list> "whole"
        let seen = rows |> List.map (fun (c, _, _, _, s, n) -> name c, int s, n) |> List.distinct |> List.sort
        equal [ "Ada", 90, 3L; "Bob", 20, 1L ] seen "each customer's window"

    [<StepDefinition("I read the Orders over 15 numbered by day across all customers")>]
    member _.Kept() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Total > 15m)
                            orderBy o.PlacedAt
                            select (Win.rowNumber (Win.whole |> Win.orderBy o.PlacedAt))
                    }
                )

            w.Set("numbers", ok r)
        }
        :> Task

    [<StepDefinition("the numbers are 1, 2 and 3")>]
    member _.Numbers() = equal [ 1L; 2L; 3L ] (w.Get<int64 list> "numbers") "the numbers"

    [<StepDefinition("I read the Orders ranked within their customer")>]
    member _.NoOrderRank() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do select (o.Total, Win.rank (Win.partition o.CustomerId)) })
            refused r
        }
        :> Task

    [<StepDefinition("I read the Orders numbered over the whole")>]
    member _.NoOrderNumber() =
        task {
            let! r = w.Repo.All(plasm { for o in tableOf<Order> do select (o.Total, Win.rowNumber Win.whole) })
            refused r
        }
        :> Task

    [<StepDefinition("I read the Orders whose number by day is 1")>]
    member _.InWhere() =
        task {
            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            where (Win.rowNumber (Win.whole |> Win.orderBy o.PlacedAt) = 1L)
                    }
                )

            refused r
        }
        :> Task

    [<StepDefinition("^the windowed read is refused, saying \"(.*)\"$")>]
    member _.Refusal(message: string) =
        let got = w.Get<string> "refusal"
        check (got.Contains message) $"expected the refusal to say \"{message}\", got {got}"
