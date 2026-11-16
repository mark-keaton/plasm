/// Queries for distinct.feature checked by `repo.CheckQueries`.
namespace Plasm.Specs.Distinct

open Plasm
open Plasm.TestDomain.Domain

module DistinctGood =
    let statuses =
        plasm {
            for o in tableOf<Order> do
                orderBy o.Status
                selectDistinct o.Status
        }

    let statusesOfAtLeast =
        Query.prepare (fun (least: Arg<decimal>) ->
            plasm {
                for o in tableOf<Order> do
                    where (o.Total >= least.Value)
                    orderBy o.Status
                    selectDistinct o.Status
            })

namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database
open Plasm.Specs.Distinct

/// Feature: A query can return each selected value once
[<Binding>]
type DistinctSteps(w: World) =

    let read (q: Projection<'R>) =
        task {
            w.MarkCommands()
            let! r = w.Repo.All q
            w.Set("distinct", r |> Result.map (List.map box))
        }
        |> unit

    let values () =
        ok (w.Get<Result<obj list, DbError>> "distinct")

    let refusal () =
        match w.Get<Result<obj list, DbError>> "distinct" with
        | Error(DbError.Translation e) -> string e
        | other -> failwithf "expected the read to be refused, got %A" other

    let customers () =
        w.Get<Map<string, CustomerId>> "distinctCustomers"

    let customerName (c: obj) =
        match c :?> CustomerId option with
        | None -> "no customer"
        | Some id -> customers () |> Map.findKey (fun _ v -> v = id)

    [<StepDefinition("the Customers \"Ada\" and \"Bob\" for distinct values")>]
    member _.Customers() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            w.Set("distinctCustomers", Map [ "Ada", ada; "Bob", bob ])
        }
        |> unit

    [<StepDefinition("the Orders, by customer, status and total:")>]
    member _.Orders(table: DataTable) =
        task {
            for row in table.Rows do
                let customer =
                    match row.["customer"].Trim() with
                    | "" -> None
                    | name -> Some (customers ()).[name]

                let! _ = Seed.order w.Db customer (row.["status"].ToLowerInvariant()) (decimal row.["total"]) (Seed.at 0)
                ()
        }
        |> unit

    [<StepDefinition("I select the distinct statuses of the Orders, ordered by status")>]
    member _.Statuses() = read DistinctGood.statuses

    [<StepDefinition("I select the distinct customers of the Orders")>]
    member _.CustomersOfOrders() =
        read (plasm { for o in tableOf<Order> do selectDistinct o.CustomerId })

    [<StepDefinition("I select the distinct pairs of customer and status of the Orders")>]
    member _.Pairs() =
        read (plasm { for o in tableOf<Order> do selectDistinct (o.CustomerId, o.Status) })

    [<StepDefinition("I select the distinct names of the Customers of published Orders, ordered by name")>]
    member _.Joined() =
        read (
            plasm {
                for o in tableOf<Order> do
                    join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                    where (o.Status = Published)
                    orderBy c.Name
                    selectDistinct c.Name
            }
        )

    [<StepDefinition("I select the distinct statuses of the Orders of at least 8, through a prepared query")>]
    member _.Prepared() = read (DistinctGood.statusesOfAtLeast 8m)

    [<StepDefinition("I check the queries of a module whose distinct queries translate")>]
    member _.Check() =
        w.Set("problems", w.Repo.CheckQueries(typeof<DistinctSteps>.Assembly, "Plasm.Specs.Distinct.DistinctGood"))

    [<StepDefinition("I select the distinct statuses of the Orders, ordered by total")>]
    member _.ByTotal() =
        read (plasm { for o in tableOf<Order> do orderBy o.Total; selectDistinct o.Status })

    [<StepDefinition("I select the distinct statuses of the first two Orders")>]
    member _.Paged() =
        read (plasm { for o in tableOf<Order> do orderBy o.Id; take 2; selectDistinct o.Status })

    [<StepDefinition("I select the distinct Orders")>]
    member _.Whole() =
        read (plasm { for o in tableOf<Order> do selectDistinct o })

    [<StepDefinition("I count the distinct customers of the Orders")>]
    member _.Count() =
        task {
            let! n = w.Repo.One(plasm { for o in tableOf<Order> do select (Agg.countDistinct o.CustomerId) })
            w.Set("count", (ok n).Value)
        }
        |> unit

    [<StepDefinition("I count the distinct customers of the Orders by status")>]
    member _.CountByStatus() =
        read (
            plasm {
                for o in tableOf<Order> do
                    groupBy o.Status
                    orderBy o.Status
                    select (o.Status, Agg.countDistinct o.CustomerId)
            }
        )

    // -- Results ---------------------------------------------------------------

    [<StepDefinition("the values are Draft and Published")>]
    member _.DraftPublished() =
        equal [ box Draft; box Published ] (values ()) "statuses"

    [<StepDefinition("the values are Published")>]
    member _.OnlyPublished() = equal [ box Published ] (values ()) "statuses"

    [<StepDefinition("the values are Ada and Bob")>]
    member _.AdaBob() = equal [ box "Ada"; box "Bob" ] (values ()) "names"

    [<StepDefinition("the values are Ada, Bob and no customer, in any order")>]
    member _.AnyOrder() =
        equal [ "Ada"; "Bob"; "no customer" ] (values () |> List.map customerName |> List.sort) "customers"

    [<StepDefinition("there are 4 pairs")>]
    member _.FourPairs() = equal 4 (values ()).Length $"pairs: %A{values ()}"

    [<StepDefinition("one command is sent, selecting distinct rows")>]
    member _.OneDistinctCommand() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 1 sql.Length $"commands: %A{sql}"
        check (sql.[0].Contains "SELECT DISTINCT ") sql.[0]

    [<StepDefinition("the read is refused, naming the Total it is ordered by")>]
    member _.NamesTotal() =
        let e = refusal ()
        check (e.Contains "Total" && e.Contains "not selected") e

    [<StepDefinition("the read is refused, saying take and skip cannot be used with selectDistinct")>]
    member _.NoPaging() =
        let e = refusal ()
        check (e.Contains "take and skip cannot be used with selectDistinct") e

    [<StepDefinition("the read is refused, saying a whole record is unique by its key")>]
    member _.WholeRecord() =
        let e = refusal ()
        check (e.Contains "unique by its key") e

    [<StepDefinition("the counts are Draft 1 and Published 2")>]
    member _.CountsPerStatus() =
        equal [ box (Draft, 1L); box (Published, 2L) ] (values ()) "counts per status"
