/// `bench` mode: BenchmarkDotNet timing and allocations (in-process toolchain).
module Plasm.Benchmarks.Bench

open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Toolchains.InProcess.Emit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.Benchmarks.Workloads

/// A repository for measuring reads: nothing to check, so both checks are
/// written as Ignore.
let private unchecked (db: Plasm.TestDomain.Database.TestDb) =
    Repo
        .Start(db.Model, db.Provider, queriesIn = typeof<Order>.Assembly, options = { RepoOptions.Default with Queries = StartupCheck.Ignore; Schema = StartupCheck.Ignore })
        .GetAwaiter()
        .GetResult()

[<MemoryDiagnoser>]
type GraphLoad() =
    let mutable repo: Repo = Unchecked.defaultof<_>
    let mutable cs = ""
    let mutable page: Projection<Order * Payment list> = Unchecked.defaultof<_>

    [<Params(20, 100, 1000)>]
    member val Parents = 20 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        let db = database.Force().GetAwaiter().GetResult()
        repo <- unchecked db
        cs <- db.ConnectionString
        page <- plasmPageWithPayments this.Parents

    [<Benchmark(Baseline = true)>]
    member _.Plasm() : Task<Order list * Payment list> = runPage repo page

    [<Benchmark>]
    member this.EfSingleQuery() = efPage cs null this.Parents false

    [<Benchmark>]
    member this.EfSplitQuery() = efPage cs null this.Parents true

[<MemoryDiagnoser>]
type Siblings() =
    let mutable repo: Repo = Unchecked.defaultof<_>
    let mutable cs = ""

    [<GlobalSetup>]
    member _.Setup() =
        let db = database.Force().GetAwaiter().GetResult()
        repo <- unchecked db
        cs <- db.ConnectionString

    [<Benchmark(Baseline = true)>]
    member _.Plasm() = runSiblings repo (OrderId siblingOrderId)

    [<Benchmark>]
    member _.PlasmPrepared() = runSiblingsPrepared repo (OrderId siblingOrderId)

    [<Benchmark>]
    member _.EfSingleQuery() = efSiblings cs null false

    [<Benchmark>]
    member _.EfSplitQuery() = efSiblings cs null true

let config () =
    ManualConfig
        .CreateMinimumViable()
        .AddJob(
            Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithLaunchCount(1)
        )
