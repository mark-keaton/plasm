namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// set-operations.feature: Customers Ada, Bob and Cy; Orders for Ada and Bob.
[<Binding>]
type SetOpSteps(w: World) =

    let named (names: string list) =
        plasm {
            for c in tableOf<Customer> do
                where (List.contains c.Name names)
        }

    let withOrder =
        plasm {
            for c in tableOf<Customer> do
                where (exists (plasm { for o in tableOf<Order> do where (o.CustomerId = Some c.Id) }))
        }

    let read (q: Query<Customer>) =
        task {
            w.MarkCommands()
            let! r = w.Repo.All q
            w.Set("setRead", r)
        }
        :> Task

    let names () =
        match w.Get<Result<Customer list, DbError>> "setRead" with
        | Ok cs -> cs |> List.map (fun c -> c.Name)
        | Error e -> failwithf "the read failed: %A" e

    [<StepDefinition("the Customers Ada, Bob and Cy, with Orders for Ada and Bob")>]
    member _.Seed() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! bob = Seed.customer w.Db "Bob" "bob@example.com"
            let! _ = Seed.customer w.Db "Cy" "cy@example.com"
            let! a = Seed.order w.Db (Some ada) "draft" 10m (Seed.at 1)
            let! b = Seed.order w.Db (Some bob) "draft" 20m (Seed.at 2)
            w.Set("adaOrder", a)
            w.Set("bobOrder", b)
            w.Set("ada", ada)
            w.Set("bob", bob)
        }
        :> Task

    [<StepDefinition("^I read the (union|unionAll|intersect|except) of the Customers named Ada or Bob and the Customers with an Order, ordered by name$")>]
    member _.Combined(operation: string) =
        let a = named [ "Ada"; "Bob" ]

        let combined =
            match operation with
            | "union" -> Query.union (a, withOrder)
            | "unionAll" -> Query.unionAll (a, withOrder)
            | "intersect" -> Query.intersect (a, withOrder)
            | _ -> Query.except (a, withOrder)

        read (plasm { for c in combined do orderBy c.Name })

    [<StepDefinition("^the combined Customers read are (.*)$")>]
    member _.Names(text: string) =
        let expected = if text = "(none)" then [] else text.Split(", ") |> List.ofArray
        equal expected (names ()) "customers"

    [<StepDefinition("one command read them, its source the set operation")>]
    member _.OneCommand() =
        let commands = w.CommandsSinceMark
        equal 1 commands.Length $"commands: %A{commands |> List.map (fun c -> c.Sql)}"
        let sql = commands.Head.Sql
        check (sql.Contains "FROM (" && (sql.Contains "UNION" || sql.Contains "INTERSECT" || sql.Contains "EXCEPT")) sql

    [<StepDefinition("I read the first Customer by name of the union of those named Bob or Cy and those with an Order")>]
    member _.FirstOfUnion() =
        read (
            plasm {
                for c in Query.union (named [ "Bob"; "Cy" ], withOrder) do
                    orderBy c.Name
                    take 1
            }
        )

    [<StepDefinition("its ORDER BY and LIMIT come after the union")>]
    member _.After() =
        let sql = w.CommandsSinceMark.Head.Sql
        let closed = sql.LastIndexOf ") AS"
        check (closed > 0 && sql.IndexOf "ORDER BY" > closed && sql.IndexOf "LIMIT" > closed) sql

    [<StepDefinition("Ada's Order has a Line")>]
    member _.Line() =
        task {
            let! p = Seed.product w.Db "SET-1"
            let! _ = Seed.line w.Db (w.Get<OrderId> "adaOrder") p 1 1m
            ()
        }
        :> Task

    [<StepDefinition("I read the union of Ada's Orders and Bob's Orders")>]
    member _.Orders() =
        task {
            let ada = Some(w.Get<CustomerId> "ada")
            let bob = Some(w.Get<CustomerId> "bob")
            let mine (who: CustomerId option) = plasm { for o in tableOf<Order> do where (o.CustomerId = who) }
            let! r = w.Repo.All(Query.union (mine ada, mine bob))
            w.Set("unionOrders", ok r)
        }
        :> Task

    [<StepDefinition("Cy has an Order too")>]
    member _.CyOrder() =
        task {
            let! cy = w.Db.Scalar<int64>("select id from customers where name = 'Cy'")
            let! _ = Seed.order w.Db (Some(CustomerId cy)) "draft" 30m (Seed.at 3)
            ()
        }
        :> Task

    [<StepDefinition("Ada's and Bob's Orders are read, not Cy's")>]
    member _.NotCy() =
        let orders = w.Get<Order list> "unionOrders"
        equal (set [ w.Get<OrderId> "adaOrder"; w.Get<OrderId> "bobOrder" ]) (orders |> List.map (fun o -> o.Id) |> set) "orders"

    [<StepDefinition("Ada's Order is read with its Line")>]
    member _.WithLine() =
        let orders = w.Get<Order list> "unionOrders"
        let ada = orders |> List.find (fun o -> o.Id = w.Get<OrderId> "adaOrder")
        equal 1 ada.Lines.Length "Ada's order's lines"

    [<StepDefinition("^I read the union of the Customers ordered by name and the rest$")>]
    member _.OrderedOperand() =
        read (Query.union (plasm { for c in tableOf<Customer> do orderBy c.Name }, withOrder))

    [<StepDefinition("^I read the union of the first Customer and the rest$")>]
    member _.PagedOperand() =
        read (Query.union (plasm { for c in tableOf<Customer> do orderBy c.Id; take 1 }, withOrder))

    [<StepDefinition("^I read the union of two Customer queries, locked for update$")>]
    member _.Locked() =
        task {
            let locked = plasm { for c in Query.union (named [ "Ada" ], withOrder) do locking RowLock.ForUpdate }
            let! r = w.Repo.Transact(Tx.all locked: Tx<Customer list, unit>)

            let asRead =
                match r with
                | Ok cs -> Ok cs
                | Error { Failure = TxFailure.Failed e } -> Error e
                | Error other -> failwithf "unexpected %A" other

            w.Set("setRead", asRead)
        }
        :> Task

    [<StepDefinition("^the combined read is refused, saying \"(.*)\"$")>]
    member _.Refused(saying: string) =
        match w.Get<Result<Customer list, DbError>> "setRead" with
        | Error(DbError.Translation e) -> check ((string e).Contains saying) (string e)
        | other -> failwithf "expected a refusal, got %A" other

    [<StepDefinition("I read the union of the names of the Customers with an Order and the names of those named Ada")>]
    member _.Projections() =
        task {
            let a = plasm { for c in withOrder do select c.Name }
            let b = plasm { for c in named [ "Ada" ] do select c.Name }
            let! r = w.Repo.All(Query.union (a, b))
            w.Set("projected", ok r)
        }
        :> Task

    [<StepDefinition("^the names read, in any order, are (.*)$")>]
    member _.AnyOrder(text: string) =
        let expected = text.Replace(" and ", ", ").Split(", ") |> Array.sort |> List.ofArray
        equal expected (w.Get<string list> "projected" |> List.sort) "names"

    [<StepDefinition("I read the Orders joined with the Customers named Ada, as a query")>]
    member _.JoinQuery() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for o in tableOf<Order> do
                            join c in (named [ "Ada" ]) on (o.CustomerId = Some c.Id)
                            select (o.Id, c.Name)
                    }
                )

            w.Set("joined", ok r)
        }
        :> Task

    [<StepDefinition("only Ada's Orders are read, with her name")>]
    member _.OnlyAda() =
        equal [ w.Get<OrderId> "adaOrder", "Ada" ] (w.Get<(OrderId * string) list> "joined") "orders joined"

    [<StepDefinition("the join's source is the query, named by its variable")>]
    member _.JoinSource() =
        let sql = w.CommandsSinceMark.Head.Sql
        check (sql.Contains "INNER JOIN (" && sql.Contains ") AS \"c\"") sql
