namespace Plasm.Specs.Statements

open System
open Plasm
open Plasm.TestDomain.Domain

/// statement-check.feature: statements the database refuses to describe.
module Refused =
    let misspelledFragment =
        plasm {
            for c in tableOf<Customer> do
                where (Raw.fragment<bool> "{0} ILIKEE {1}" (c.Name, "a%"))
        }

    let lateOrders: FormattableString = $"select id from orders where totl > {10}"

    let purgeQuoted =
        Bulk.delete (
            plasm {
                for c in tableOf<Customer> do
                    where (Raw.fragment<bool> "{0} ILIKEE {1}" (c.Name, "x%"))
            }
        )

/// Statements the database describes: none of them may run.
module Accepted =
    let startingWithA =
        plasm {
            for c in tableOf<Customer> do
                where (Raw.fragment<bool> "{0} ILIKE {1}" (c.Name, "a%"))
        }

    let everyCustomerGone: FormattableString = $"delete from customers"

    let purgeEveryone = Bulk.delete (Query.from (tableOf<Customer>))

    let ordersWithLines = Query.from (tableOf<Order>)

    let customerById =
        Query.prepare (fun (id: Arg<CustomerId>) -> plasm { for c in tableOf<Customer> do where (c.Id = id.Value) })

/// A statement whose parameter's type the database cannot tell from the
/// statement alone, when it is sent as an untyped NULL.
module Untyped =
    let whenAbsent =
        Query.prepare (fun (n: Arg<int64>) -> plasm { for c in tableOf<Customer> do where (Raw.fragment<bool> "{0} IS NULL" n.Value) })

namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm

/// A provider like the database's that cannot describe a statement.
type NoDescribe(inner: IProvider) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "NoDescribeSQL"
            member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
            member _.ParameterName i = inner.Dialect.ParameterName i
            member _.ColumnType t = inner.Dialect.ColumnType t
            member _.CatalogueTypes t = inner.Dialect.CatalogueTypes t
            member _.CastType t = inner.Dialect.CastType t
            member _.StoreCast(t, array) = inner.Dialect.StoreCast(t, array)
            member _.TextCast = inner.Dialect.TextCast
            member _.UnboundedLimit = inner.Dialect.UnboundedLimit
            member _.NullSafeComparison negated = inner.Dialect.NullSafeComparison negated
            member _.LikeEscape = inner.Dialect.LikeEscape
            member _.FunctionName name = inner.Dialect.FunctionName name
            member _.Json = inner.Dialect.Json
            member _.Begin isolation = inner.Dialect.Begin isolation
            member _.Capabilities = { inner.Dialect.Capabilities with Describe = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// A provider like the database's whose description of a statement is its
/// own: it refuses every one, saying `saying`.
type RefusingDescribe(inner: IProvider, saying: string) =
    interface IProvider with
        member _.Dialect = inner.Dialect
        member _.Describe(_, _) = Task.FromResult(DescribeOutcome.Refused { Kind = DbFailureKind.OtherDatabaseError; SqlState = None; Constraint = None; Message = saying; Exception = exn saying })
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

[<Binding>]
type StatementSteps(w: World) =

    let assembly = typeof<StatementSteps>.Assembly
    let refused = "Plasm.Specs.Statements.Refused"
    let accepted = "Plasm.Specs.Statements.Accepted"

    let customers () = w.Db.Scalar<int64>("select count(*) from customers")

    [<StepDefinition("the database describes the statements of the module of refused ones")>]
    member _.DescribeRefused() =
        task {
            let! problems = w.Repo.Describe(assembly, refused)
            w.Set("described", problems)
        }
        :> Task

    [<StepDefinition("^a provider like the database's that refuses every statement it is asked to describe, saying \"(.*)\"$")>]
    member _.Refusing(saying: string) =
        w.Set("ownRepo", Repo(w.Db.Model, RefusingDescribe(w.Db.Provider, saying)))

    [<StepDefinition("it describes the statements of the module of accepted ones")>]
    member _.OwnDescribes() =
        task {
            let! problems = w.Get<Repo>("ownRepo").Describe(assembly, accepted)
            w.Set("described", problems)
        }
        :> Task

    [<StepDefinition("^each of them is reported as refused, saying \"(.*)\"$")>]
    member _.EachRefused(saying: string) =
        let problems = w.Get<StartupProblem list> "described"
        let refused = problems |> List.choose (function StartupProblem.Refused r -> Some r | _ -> None)
        check (not refused.IsEmpty && refused.Length = problems.Length && refused |> List.forall (fun r -> r.Failure.Message = saying)) $"%A{problems |> List.map string}"

    [<StepDefinition("the database describes the statements of the module of accepted ones")>]
    member _.DescribeAccepted() =
        task {
            let! before = customers ()
            w.Set("customersBefore", before)
            let! problems = w.Repo.Describe(assembly, accepted)
            w.Set("described", problems)
        }
        :> Task

    [<StepDefinition("^\"(.*)\" is reported as refused by the database, saying \"(.*)\"$")>]
    member _.RefusedOne(name: string, reason: string) =
        let problems = w.Get<StartupProblem list> "described"

        let found =
            problems
            |> List.tryPick (function
                | StartupProblem.Refused r when r.Statement.EndsWith("." + name) -> Some r
                | _ -> None)

        match found with
        | Some r -> check (r.Failure.Message.Contains reason) $"{name} was refused for: {r.Failure.Message}"
        | None -> failwithf "%s is not reported as refused in %A" name problems

    [<StepDefinition("the database describes the statements of the module whose parameter has no type of its own")>]
    member _.DescribeUntyped() =
        task {
            let! problems = w.Repo.Describe(assembly, "Plasm.Specs.Statements.Untyped")
            w.Set("described", problems)
        }
        :> Task

    [<StepDefinition("no statement is reported")>]
    member _.NoneReported() = equal [] (w.Get<StartupProblem list> "described") "problems"

    [<StepDefinition("the customers are as they were")>]
    member _.Unchanged() =
        task {
            let! after = customers ()
            equal (w.Get<int64> "customersBefore") after "customers"
        }
        :> Task

    [<StepDefinition("I start a repository over the steps' modules, warning")>]
    member _.StartWarning() =
        task {
            let found = ResizeArray<StartupProblem>()

            let! _ =
                Repo.Start(
                    w.Db.Model,
                    w.Db.Provider,
                    assembly,
                    { RepoOptions.Default with
                        Queries = StartupCheck.Warn
                        Schema = StartupCheck.Warn
                        OnProblem = Some found.Add }
                )

            w.Set("startProblems", List.ofSeq found)
        }
        :> Task

    [<StepDefinition("^the problems include \"(.*)\", refused by the database$")>]
    member _.Includes(name: string) =
        let problems = w.Get<StartupProblem list> "startProblems"

        check
            (problems |> List.exists (function StartupProblem.Refused r -> r.Statement.EndsWith("." + name) | _ -> false))
            $"%A{problems |> List.map string}"

    [<StepDefinition("the problems include the missing column customers.email, and none refused by the database")>]
    member _.DriftOnly() =
        let problems = w.Get<StartupProblem list> "startProblems"

        check
            (problems |> List.exists (function StartupProblem.Schema i -> i.Table = "customers" && i.Column = Some "email" | _ -> false))
            $"%A{problems |> List.map string}"

        check (problems |> List.forall (function StartupProblem.Refused _ -> false | _ -> true)) $"%A{problems |> List.map string}"

    [<StepDefinition("a provider that cannot describe describes the statements of the module of refused ones")>]
    member _.NoDescribe() =
        task {
            let repo = Repo(w.Db.Model, NoDescribe(w.Db.Provider), RepoOptions.Default)
            let! problems = repo.Describe(assembly, refused)
            w.Set("described", problems)
        }
        :> Task

    [<StepDefinition("^it notes that (\\d+) statements? (?:were|was) not described, and reports none as refused$")>]
    member _.Noted(n: int) =
        match w.Get<StartupProblem list> "described" with
        | [ StartupProblem.NotDescribed(_, statements) ] -> equal n statements.Length "statements not described"
        | other -> failwithf "expected one note, got %A" other
