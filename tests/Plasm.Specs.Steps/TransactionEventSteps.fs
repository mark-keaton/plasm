namespace Plasm.Specs

open System.Data
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// transaction-events.feature: what OnCommand reports around a transaction's
/// statements, read from everything the recorder kept.
[<Binding>]
type TransactionEventSteps(w: World) =

    let customer email : Customer =
        { Id = CustomerId 0L
          Name = "Ada"
          Email = email }

    let customers = plasm { for c in tableOf<Customer> do select c }

    /// Remember where the scenario's events start.
    let mark () = w.Set("events-from", w.Db.Recorder.All.Length)

    let events () =
        w.Db.Recorder.All |> List.skip (w.Get<int> "events-from")

    let transact (tx: Tx<'T, string>) (isolation: IsolationLevel) =
        task {
            mark ()
            let! _ = w.Repo.Transact(tx, isolation)
            return ()
        }
        :> Task

    let one (kind: CommandKind) =
        match events () |> List.filter (fun e -> e.Kind = kind) with
        | [ e ] -> e
        | found -> failwithf "expected one %A event, found %A" kind found

    [<StepDefinition("^a transaction inserts a Customer$")>]
    member _.Inserts() =
        transact (Tx.insert (customer "ada@example.com")) IsolationLevel.ReadCommitted

    [<StepDefinition("^a transaction at (\\w+) inserts a Customer$")>]
    member _.InsertsAt(isolation: string) =
        transact (Tx.insert (customer "ada@example.com")) (System.Enum.Parse<IsolationLevel> isolation)

    [<StepDefinition("^a transaction inserts a Customer with the email \"(.*)\"$")>]
    member _.InsertsWith(email: string) =
        transact (Tx.insert (customer email)) IsolationLevel.ReadCommitted

    [<StepDefinition("^a transaction reads the Customers and then refuses$")>]
    member _.ReadsThenRefuses() =
        transact
            (transaction {
                let! _ = Tx.all customers
                return! Tx.fail "refused"
            })
            IsolationLevel.ReadCommitted

    [<StepDefinition("^a transaction refuses before it reads anything$")>]
    member _.RefusesFirst() =
        transact (Tx.fail "refused": Tx<unit, string>) IsolationLevel.ReadCommitted

    [<StepDefinition("^an Order with one Line$")>]
    member _.OrderWithLine() =
        task {
            let! o = Seed.order w.Db None "draft" 10m (Seed.at 1)
            let! p = Seed.product w.Db "SKU"
            let! _ = Seed.line w.Db o p 1 10m
            return ()
        }
        :> Task

    [<StepDefinition("^I read the Orders in one snapshot$")>]
    member _.Snapshot() =
        task {
            mark ()
            let! r = w.Repo.All(Query.from (tableOf<Order>), ReadConsistency.Snapshot)
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("^a sandbox reads the Customers$")>]
    member _.SandboxReads() =
        task {
            mark ()

            let! _ =
                w.Repo.Sandbox(fun sandbox ->
                    task {
                        let! r = sandbox.All customers
                        return ok r
                    })

            return ()
        }
        :> Task

    [<StepDefinition("^a sandbox's transaction inserts a Customer$")>]
    member _.SandboxInserts() =
        task {
            mark ()

            let! _ =
                w.Repo.Sandbox(fun sandbox ->
                    task {
                        let! r = sandbox.Transact(Tx.insert (customer "ada@example.com"): Tx<Customer, unit>)
                        return ok r
                    })

            return ()
        }
        :> Task

    [<StepDefinition("^OnCommand reported, in order:$")>]
    member _.Reported(table: DataTable) =
        let expected = [ for row in table.Rows -> row.["Kind"], row.["Sql"] ]
        let actual = events ()
        let shown = actual |> List.map (fun e -> $"{e.Kind} {e.Sql}") |> String.concat "\n"
        equal expected.Length actual.Length $"events reported:\n{shown}\n"

        for (kind, sql), e in List.zip expected actual do
            check (string e.Kind = kind && e.Sql.StartsWith sql) $"expected {kind} \"{sql}\", reported:\n{shown}"

    [<StepDefinition("^(\\d+) of them (?:is a statement|are statements), by its Kind$")>]
    member _.Statements(n: int) =
        equal n (events () |> List.filter (fun e -> e.Kind = CommandKind.Statement)).Length "statements"

    [<StepDefinition("^the first event is a Begin, \"(.*)\"$")>]
    member _.FirstBegin(sql: string) =
        let first = List.head (events ())
        equal CommandKind.Begin first.Kind "the first event's kind"
        equal sql first.Sql "the BEGIN"

    [<StepDefinition("^the Begin has the round trip of the INSERT$")>]
    member _.BeginWithInsert() =
        equal (one CommandKind.Statement).RoundTrip (one CommandKind.Begin).RoundTrip "the BEGIN's round trip"

    [<StepDefinition("^the Commit has a round trip of its own$")>]
    member _.CommitAlone() =
        let commit = one CommandKind.Commit
        let others = events () |> List.filter (fun e -> e.Kind <> CommandKind.Commit)
        check (others |> List.forall (fun e -> e.RoundTrip <> commit.RoundTrip)) "the COMMIT shares a round trip"

    [<StepDefinition("^the INSERT did not succeed$")>]
    member _.InsertFailed() =
        check (not (one CommandKind.Statement).Succeeded) "the INSERT is reported as failed"

    [<StepDefinition("^they share one round trip$")>]
    member _.OneTrip() =
        equal 1 (events () |> List.map (fun e -> e.RoundTrip) |> List.distinct).Length "round trips"
