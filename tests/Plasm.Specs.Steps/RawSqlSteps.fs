namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A record that is not part of the model, read from hand-written SQL.
type CustomerSummary =
    { Name: string
      OrderCount: int
      Total: decimal }

/// Feature: Typed SQL escape hatches
/// (Data from "Orders for several Customers": Ada 10 + 20, Bob 30, one order of 40 without a customer.)
[<Binding>]
type RawSqlSteps(w: World) =

    let totalSum () = w.Db.Scalar<decimal>("select sum(total) from orders")

    // Rule: Hand-written queries return typed values

    [<StepDefinition("I read Customers with hand-written SQL")>]
    member _.ReadCustomers() =
        task {
            let excluded = "Nobody"
            let! r = w.Repo.Sql<Customer>($"SELECT * FROM customers WHERE name <> {excluded} ORDER BY id")
            w.Set("rawCustomers", ok r)
        }
        |> unit

    [<StepDefinition("the results are Customer records built through the model")>]
    member _.CustomersBuilt() =
        let customers: Customer list = w.Get "rawCustomers"
        equal [ w.Get<CustomerId> "ada"; w.Get<CustomerId> "bob" ] (customers |> List.map (fun c -> c.Id)) "typed keys"
        equal [ "Ada"; "Bob" ] (customers |> List.map (fun c -> c.Name)) "names"

    [<StepDefinition("Orders for several Customers, each with an OrderLine")>]
    member _.OrdersWithLines() =
        task {
            let! ada = Seed.customer w.Db "Ada" "ada@example.com"
            let! p = Seed.product w.Db "SKU"
            let! o1 = Seed.order w.Db (Some ada) "published" 10m (Seed.at 1)
            let! o2 = Seed.order w.Db None "draft" 20m (Seed.at 2)
            let! _ = Seed.line w.Db o1 p 1 10m
            let! _ = Seed.line w.Db o2 p 2 10m
            ()
        }
        |> unit

    [<StepDefinition("I read Orders with hand-written SQL")>]
    member _.ReadOrders() =
        task {
            let minimum = 5m
            w.MarkCommands()
            let! r = w.Repo.Sql<Order>($"SELECT * FROM orders WHERE total >= {minimum} ORDER BY id")
            w.Set("rawOrders", ok r)
        }
        |> unit

    [<StepDefinition("each Order holds its OrderLines, read in one more command")>]
    member _.OrdersHoldLines() =
        let orders: Order list = w.Get "rawOrders"
        equal [ [ 1 ]; [ 2 ] ] (orders |> List.map (fun o -> o.Lines |> List.map (fun l -> l.Quantity.Value))) "each Order's lines"
        check (orders |> List.forall (fun o -> o.Lines |> List.forall (fun l -> l.OrderId = o.Id))) "lines of their Order"
        equal 2 w.CommandsSinceMark.Length "the SQL, and the lines its Orders own"

    [<StepDefinition("I read Customer names with their Order counts using hand-written SQL")>]
    member _.ReadTuples() =
        task {
            let! r =
                w.Repo.Sql<string * int>(
                    $"SELECT c.name, count(o.id) FROM customers c JOIN orders o ON o.customer_id = c.id GROUP BY c.name ORDER BY c.name"
                )

            w.Set("rawTuples", ok r)
        }
        |> unit

    [<StepDefinition("the results are typed tuples in column order")>]
    member _.Tuples() =
        equal [ "Ada", 2; "Bob", 1 ] (w.Get<(string * int) list> "rawTuples") "rows"

    [<StepDefinition("I read a summary record that is not part of the model")>]
    member _.ReadSummary() =
        task {
            let! r =
                w.Repo.Sql<CustomerSummary>(
                    $"""SELECT c.name, count(o.id) AS order_count, sum(o.total) AS total
                        FROM customers c JOIN orders o ON o.customer_id = c.id
                        GROUP BY c.name ORDER BY c.name"""
                )

            w.Set("summaries", ok r)
        }
        |> unit

    [<StepDefinition("each field is read from the column of the same name")>]
    member _.Summaries() =
        equal
            [ { Name = "Ada"; OrderCount = 2; Total = 30m }
              { Name = "Bob"; OrderCount = 1; Total = 30m } ]
            (w.Get<CustomerSummary list> "summaries")
            "summaries"

    [<StepDefinition("I interpolate a value containing SQL into hand-written SQL")>]
    member _.Injection() =
        task {
            w.MarkCommands()
            let name = "x'; DELETE FROM orders; --"
            let! r = w.Repo.Sql<Customer>($"SELECT * FROM customers WHERE name = {name}")
            equal [] (ok r) "no customer has that name"
        }
        |> unit

    [<StepDefinition("the value is sent as a command parameter")>]
    member _.AsParameter() =
        task {
            let cmd = w.CommandsSinceMark |> List.exactlyOne
            equal "SELECT * FROM customers WHERE name = @p0" cmd.Sql "SQL"
            equal [ "@p0" ] (cmd.Parameters |> List.map fst) "parameters"
            let! n = w.Db.Scalar<int64>("select count(*) from orders")
            equal 4L n "orders are untouched"
        }
        |> unit

    [<StepDefinition("the SQL text does not contain the value")>]
    member _.NotInText() =
        check (not ((w.CommandsSinceMark |> List.exactlyOne).Sql.Contains "DELETE")) "value not spliced"

    [<StepDefinition("hand-written SQL omits a column the record requires")>]
    member _.OmitsColumn() =
        task {
            let! _ = Seed.order w.Db None "draft" 1m (Seed.at 1)
            let! r = w.Repo.Sql<Order>($"SELECT id, status, total FROM orders")
            w.Set("rawError", err r)
        }
        |> unit

    [<StepDefinition("execution fails with the missing column identified")>]
    member _.MissingColumn() =
        match w.Get<DbError> "rawError" with
        | DbError.Materialization m ->
            equal "CustomerId" m.Property "property"
            check (m.Message.Contains "\"customer_id\" is missing") m.Message
        | other -> failwithf "expected a materialization error, got %A" other

    // Rule: SQL fragments extend typed queries

    [<StepDefinition("I filter Customers with a SQL fragment over a column")>]
    member _.Fragment() =
        task {
            w.MarkCommands()
            let pattern = "a%"

            let! r =
                w.Repo.All(
                    plasm {
                        for c in tableOf<Customer> do
                            where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, pattern))
                    }
                )

            w.Set("fragmentCustomers", ok r)
        }
        |> unit

    [<StepDefinition("the fragment is part of the executed SQL with its arguments as columns and parameters")>]
    member _.FragmentSql() =
        let cmd = w.CommandsSinceMark |> List.exactlyOne
        check (cmd.Sql.Contains "WHERE (\"t0\".\"name\" ILIKE @p0)") cmd.Sql

    [<StepDefinition("the results are typed records")>]
    member _.FragmentResults() =
        equal [ "Ada" ] (w.Get<Customer list> "fragmentCustomers" |> List.map (fun c -> c.Name)) "customers"

    [<StepDefinition("I attempt to build a fragment from a runtime string")>]
    member _.RuntimeFragment() =
        w.Set("fragmentSnippet", "let sql = \"{0} ILIKE 'a%%'\"\nlet q = plasm { for c in tableOf<Customer> do where (Raw.fragment<bool> sql c.Name) }")

    [<StepDefinition("the fragment is rejected at compile time")>]
    member _.FragmentRejected() =
        task {
            let control = "let q = plasm { for c in tableOf<Customer> do where (Raw.fragment<bool> \"{0} ILIKE {1}\" (c.Name, \"a%\")) }"
            let! r = CompileCheck.check control
            check r.Compiles ("expected to compile:\n" + control + "\n" + String.concat "\n" r.Errors)
            let code: string = w.Get "fragmentSnippet"
            let! r = CompileCheck.check code
            check (r.Errors |> List.exists (fun e -> e.StartsWith "FS0001")) $"expected FS0001 for:\n{code}\ngot: %A{r.Errors}"
        }
        |> unit

    // Rule: Hand-written commands and transactions

    [<StepDefinition("I execute a hand-written update")>]
    member _.HandUpdate() =
        task {
            let ada = w.Get<CustomerId> "ada"
            let! r = w.Repo.Transact(Tx.executeSql ($"UPDATE orders SET total = total + 1 WHERE customer_id = {ada}"): Tx<int, unit>)
            w.Set("affected", ok r)
        }
        |> unit

    [<StepDefinition("the number of affected rows is returned")>]
    member _.AffectedRows() =
        task {
            equal 2 (w.Get<int> "affected") "affected rows"
            let! sum = totalSum ()
            equal 102m sum "Ada's two orders were each increased by 1"
        }
        |> unit

    [<StepDefinition("a transaction runs hand-written SQL and then fails")>]
    member _.TxRaw() =
        task {
            let! r =
                w.Repo.Transact(
                    transaction {
                        let! n = Tx.executeSql $"UPDATE orders SET total = total + 100"
                        let! rows = Tx.sql<decimal, _> $"SELECT total FROM orders ORDER BY id"
                        equal 4 n "updated inside the transaction"
                        equal [ 110m; 120m; 130m; 140m ] rows "visible inside the transaction"
                        return! Tx.fail "stop"
                    }
                )

            equal "fail" (err r).Step.Action "failed step"
        }
        |> unit

    [<StepDefinition("the hand-written change is rolled back")>]
    member _.RawRolledBack() =
        task {
            let! sum = totalSum ()
            equal 100m sum "totals"
        }
        |> unit
