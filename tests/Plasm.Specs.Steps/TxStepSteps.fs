namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Transaction steps do what they say
[<Binding>]
type TxStepSteps(w: World) =

    let stored = { New.customer "Ada" "ada@example.com" with Id = CustomerId 1L }

    let changesetFor (action: string) =
        match action with
        | "insert" -> Changeset.insert (New.customer "Ada" "ada@example.com")
        | "update" -> stored |> Changeset.edit (fun c -> { c with Name = "Ada L." })
        | "delete" -> Changeset.delete stored
        | a -> failwithf "unknown action %s" a

    let attempt (f: unit -> unit) =
        try
            f ()
            w.Set("rejection", "")
        with :? ArgumentException as e ->
            w.Set("rejection", e.Message)

    let rejection () =
        let m = w.Get<string> "rejection"
        check (m <> "") "expected the call to be rejected"
        m

    [<StepDefinition("^I build a transaction whose Tx\\.(insert|update|delete) step is given a changeset for (insert|update|delete)$")>]
    member _.BuildMismatched(step: string, action: string) =
        w.MarkCommands()
        let cs = changesetFor action

        attempt (fun () ->
            let tx =
                match step with
                | "insert" -> Tx.insert cs
                | "update" -> Tx.update cs
                | _ -> Tx.delete cs

            w.Repo.Transact(tx).Wait())

        w.Set("expected", (step, action))

    [<StepDefinition("building the transaction fails, naming where the step is written and both actions")>]
    member _.NamesStepAndActions() =
        let m = rejection ()
        let step, action = w.Get<string * string> "expected"
        check (m.Contains "TxStepSteps.fs:") $"where the step is written is missing: {m}"
        check (m.Contains $"Tx.{step}") $"step kind missing: {m}"
        check (m.Contains $"for {action}") $"changeset action missing: {m}"

    [<StepDefinition("^I call repo\\.(Insert|Update|Delete) with a changeset for (insert|update|delete)$")>]
    member _.RepoMismatched(memberName: string, action: string) =
        let cs = changesetFor action

        attempt (fun () ->
            match memberName with
            | "Insert" -> w.Repo.Insert(cs) |> ignore
            | "Update" -> w.Repo.Update(cs) |> ignore
            | _ -> w.Repo.Delete(cs) |> ignore)

        w.Set("expected", (memberName, action))

    [<StepDefinition("the call fails before execution, naming both actions")>]
    member _.CallNamesActions() =
        let m = rejection ()
        let memberName, action = w.Get<string * string> "expected"
        check (m.Contains $"repo.{memberName}") $"member missing: {m}"
        check (m.Contains $"for {action}") $"changeset action missing: {m}"

    [<StepDefinition("a transaction runs a custom step that inserts a row with a command and a later step fails")>]
    member _.CustomStepThenFail() =
        task {
            let! r =
                w.Repo.Transact(
                    transaction {
                        let! _ =
                            Tx.run (fun ctx ->
                                task {
                                    use cmd = ctx.CreateCommand()
                                    cmd.CommandText <- "insert into products(sku, name) values ('AUDIT', 'audit')"
                                    let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                    return Ok()
                                })

                        return! Tx.fail "stop"
                    }
                )

            equal "fail" (err r).Step.Action "failed step"
        }
        |> unit

    [<StepDefinition("the custom step's row was rolled back")>]
    member _.RolledBack() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from products where sku = 'AUDIT'")
            equal 0L n "rows written by the custom step"
        }
        |> unit

    [<StepDefinition("I write a custom step that commits the transaction it was given")>]
    member _.CommitStep() =
        task {
            let code =
                String.concat
                    "\n"
                    [ "let step ="
                      "    Tx.run (fun ctx ->"
                      "        task {"
                      "            ctx.Transaction.Commit()"
                      "            return Ok()"
                      "        })" ]

            let! r = CompileCheck.check code
            w.Set("commitCompile", r)
        }
        |> unit

    [<StepDefinition("the code does not compile, because the step context has no transaction to commit")>]
    member _.DoesNotCompile() =
        let r = w.Get<CompileCheck.CompileResult> "commitCompile"
        check (not r.Compiles) "a step that commits its transaction should not compile"
        check (r.Errors |> List.exists (fun e -> e.Contains "Transaction")) $"expected an error about Transaction: %A{r.Errors}"

    [<StepDefinition("a transaction inserts a Customer and then runs a custom step whose command is \"(.*)\"")>]
    member _.ControlSql(sql: string) =
        task {
            try
                let! r =
                    w.Repo.Transact(
                        transaction {
                            let! _ = Tx.insert (Changeset.insert (New.customer "Ada" "ada@example.com"))

                            let! _ =
                                Tx.run (fun ctx ->
                                    task {
                                        use cmd = ctx.CreateCommand()
                                        cmd.CommandText <- sql
                                        let! _ = cmd.ExecuteNonQueryAsync(ctx.CancellationToken)
                                        return (Ok(): Result<unit, string>)
                                    })

                            return ()
                        }
                    )

                w.Set("controlled", (Choice1Of2 r: Choice<Result<unit, TransactionError<string>>, exn>))
            with ex ->
                w.Set("controlled", (Choice2Of2 ex: Choice<Result<unit, TransactionError<string>>, exn>))
        }
        |> unit

    [<StepDefinition("the transaction fails at the custom step, saying a step cannot control the transaction")>]
    member _.FailsAtCustom() =
        match w.Get<Choice<Result<unit, TransactionError<string>>, exn>> "controlled" with
        | Choice1Of2(Error e) ->
            equal "run" e.Step.Action "failed step"

            match e.Failure with
            | TxFailure.Misused m -> check (m.Contains "cannot control the transaction") m
            | other -> failwithf "expected a misused step, got %A" other
        | other -> failwithf "expected the transaction to fail, got %A" other

    [<StepDefinition("the Customer was rolled back")>]
    member _.CustomerRolledBack() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from customers")
            equal 0L n "customers"
        }
        |> unit
