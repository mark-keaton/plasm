namespace Plasm.Specs

open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A provider whose dialect differs from PostgreSQL's in every way the
/// feature checks. Commands are only explained or refused, never run.
type TestDialectProvider(inner: IProvider) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "TestSQL"
            member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
            member _.ParameterName i = inner.Dialect.ParameterName i
            member _.ColumnTypes t = inner.Dialect.ColumnTypes t
            member _.NullSafeComparison negated = if negated then "IS NOT" else "IS"

            member _.FunctionName name =
                match name with
                | "char_length" -> "length"
                | n -> n

            member _.Capabilities =
                { inner.Dialect.Capabilities with
                    ArrayParameters = false
                    Upserts = false
                    Returning = false
                    RowLocking = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(_, _) = Task.FromResult None
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// Feature: Database-specific SQL comes from the provider
[<Binding>]
type ProviderSteps(w: World) =

    let repo () = w.Get<Repo> "testRepo"

    let explain (q: Query<'T>) = (repo().Explain q |> ok)

    let stored = { New.order None Published 10m with Id = OrderId 1L; Version = 1 }

    [<StepDefinition("a test dialect without array parameters, upserts, RETURNING or row locking, with its own null-safe comparison and string length")>]
    member _.TestDialect() =
        w.Set("testRepo", Repo(Schemas.model, TestDialectProvider(w.Db.Provider)))

    [<StepDefinition("I explain a query for Orders, which own their Lines, with the test dialect")>]
    member _.ExplainLoads() =
        let plan = explain (Query.from Schemas.orders)
        w.Set("sql", (plan.Loads |> List.head).Sql)

    [<StepDefinition("the load command matches keys with an IN list of parameters")>]
    member _.InList() =
        let sql = w.Get<string> "sql"
        check (sql.Contains " IN (" && not (sql.Contains "ANY(")) $"expected an IN list: {sql}"

    [<StepDefinition("I explain a query comparing an optional field with <> with the test dialect")>]
    member _.ExplainNullSafe() =
        let reason = Some "late"
        w.Set("sql", (explain (plasm { for o in Schemas.orders do where (o.CancellationReason <> reason) })).Sql)

    [<StepDefinition("the comparison uses the dialect's null-safe operator")>]
    member _.NullSafe() =
        let sql = w.Get<string> "sql"
        check (sql.Contains " IS NOT @p0" && not (sql.Contains "DISTINCT")) $"expected the dialect's operator: {sql}"

    [<StepDefinition("I explain a query filtering on the length of a name with the test dialect")>]
    member _.ExplainLength() =
        w.Set("sql", (explain (plasm { for c in Schemas.customers do where (c.Name.Length > 3) })).Sql)

    [<StepDefinition("the length uses the dialect's function name")>]
    member _.Length() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "length(" && not (sql.Contains "char_length")) $"expected length(): {sql}"

    [<StepDefinition("I attempt (.*) with the test dialect")>]
    member _.Attempt(operation: string) =
        task {
            w.MarkCommands()
            let r = repo ()

            let! outcome =
                task {
                    match operation with
                    | "an upsert" ->
                        let! x = r.Transact(Tx.execute (Bulk.upsert ([ New.product "SKU" "Widget" ], (fun p -> p.Sku), (fun _ incoming -> incoming))): Tx<_, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported upsert, got %A" other
                    | "an insert" ->
                        let! x = r.Transact(Tx.insert (New.customer "Ada" "ada@example.com"): Tx<_, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported insert, got %A" other
                    | "deleting an Order and its Lines" ->
                        // Deleting an owner deletes every line it has, read FOR UPDATE.
                        let! x = r.Transact(Tx.delete stored: Tx<Order, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported delete, got %A" other
                    | "changing an Article's links" ->
                        let article = { Id = ArticleId 1L; Title = "Intro"; Tags = [] }

                        let! x = r.Transact(Tx.update (article, { article with Tags = [ { Id = TagId 1L; Name = "fsharp" } ] }): Tx<Article, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected unsupported links, got %A" other
                    | op -> return failwithf "unknown operation %s" op
                }

            w.Set("refused", outcome.Value)
        }
        |> unit

    [<StepDefinition("it fails before execution as unsupported by the provider, naming (.*)")>]
    member _.Refused(capability: string) =
        let op = w.Get<string> "refused"
        check (op.Contains capability) $"expected the refusal to name {capability}: {op}"
        equal [] (w.CommandsSinceMark |> List.map (fun c -> c.Sql)) "commands sent"

    [<StepDefinition("^I attempt an operation the test dialect cannot perform through (an insert step|a bulk write step)$")>]
    member _.AttemptThrough(call: string) =
        task {
            let r = repo ()

            let! x =
                match call with
                | "an insert step" -> r.Transact(Tx.insert (New.customer "Ada" "ada@example.com") |> Tx.map ignore: Tx<unit, unit>)
                | _ -> r.Transact(Tx.execute (Bulk.upsert ([ New.product "SKU" "Widget" ], (fun p -> p.Sku), (fun _ incoming -> incoming))) |> Tx.map ignore: Tx<unit, unit>)

            match x with
            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> w.Set("refused", op)
            | other -> failwithf "got %A" other
        }
        |> unit

    [<StepDefinition("the failure contains TranslationError.UnsupportedByProvider naming the missing capability")>]
    member _.NamesCapability() =
        let op = w.Get<string> "refused"
        check (op.Contains "RETURNING" || op.Contains "upserts") op
