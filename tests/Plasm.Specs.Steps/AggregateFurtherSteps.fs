namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// aggregates.feature: filtered, boolean, list and text aggregates.
[<Binding>]
type AggregateFurtherSteps(w: World) =

    let summary (q: Query<Order>) =
        plasm {
            for o in q do
                groupBy o.CustomerId
                orderBy o.CustomerId
                select (
                    o.CustomerId,
                    Agg.countWhere (o.Status = Published),
                    Agg.sumWhere (o.Status = Published) o.Total,
                    Agg.any (o.Total > 30m),
                    Agg.all (o.Status = Published),
                    Agg.listBy o.Total o.PlacedAt,
                    Agg.stringConcatBy ", " (Raw.fragment<string> "{0}::text" o.Status) o.PlacedAt
                )
        }

    let show (count: int64, sum: decimal, any: bool, all: bool, totals: decimal list, statuses: string) =
        let totals = totals |> List.map (fun t -> string (int t)) |> String.concat "; "
        $"{count}, {int sum}, {(string any).ToLower()}, {(string all).ToLower()}, [{totals}] and \"{statuses}\""

    [<StepDefinition("Ada with published Orders of 10 and 40, and Bob with a draft Order of 20")>]
    member _.Seed() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! _ = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! _ = Seed.order w.Db (Some bob) "draft" 20m (Seed.at 2)
            let! _ = Seed.order w.Db (Some ada) "published" 40m (Seed.at 3)
            w.Set("ada", ada)
            w.Set("bob", bob)
        }
        :> Task

    [<StepDefinition("I read, for each customer, the published count and total, whether any total is over 30 and whether all are published, the totals by day, and the statuses by day joined by \", \"")>]
    member _.Summary() =
        task {
            let! r = w.Repo.All(summary (Query.from (tableOf<Order>)))
            w.Set("summary", ok r)
        }
        :> Task

    [<StepDefinition("^(Ada|Bob)'s row is (.*)$")>]
    member _.Row(who: string, text: string) =
        let id = Some(w.Get<CustomerId>(who.ToLower()))
        let rows = w.Get<(CustomerId option * int64 * decimal * bool * bool * decimal list * string) list> "summary"
        let _, c, s, a, l, t, st = rows |> List.find (fun (k, _, _, _, _, _, _) -> k = id)
        equal text (show (c, s, a, l, t, st)) $"{who}'s row"

    [<StepDefinition("I read Ada's totals by day, latest first")>]
    member _.Latest() =
        task {
            let ada = Some(w.Get<CustomerId> "ada")

            let! r =
                w.Repo.One(
                    plasm {
                        for o in tableOf<Order> do
                            where (o.CustomerId = ada)
                            select (Agg.listByDescending o.Total o.PlacedAt)
                    }
                )

            w.Set("latest", (ok r).Value)
        }
        :> Task

    [<StepDefinition("^Ada's totals are \\[(.*)\\]$")>]
    member _.Totals(text: string) =
        let expected = text.Split("; ") |> Array.map decimal |> List.ofArray
        equal expected (w.Get<decimal list> "latest") "totals"

    [<StepDefinition("I read the same of the Orders over 1000")>]
    member _.None() =
        task {
            let! r =
                w.Repo.One(
                    plasm {
                        for o in tableOf<Order> do
                            where (o.Total > 1000m)
                            select (
                                Agg.countWhere (o.Status = Published),
                                Agg.sumWhere (o.Status = Published) o.Total,
                                Agg.any (o.Total > 30m),
                                Agg.all (o.Status = Published),
                                Agg.listBy o.Total o.PlacedAt,
                                Agg.stringConcatBy ", " (Raw.fragment<string> "{0}::text" o.Status) o.PlacedAt
                            )
                    }
                )

            w.Set("empty", (ok r).Value)
        }
        :> Task

    [<StepDefinition("^the row is (.*)$")>]
    member _.EmptyRow(text: string) =
        let c, s, a, l, t, st = w.Get<int64 * decimal * bool * bool * decimal list * string> "empty"
        equal text (show (c, s, a, l, t, st)) "the row over no orders"
