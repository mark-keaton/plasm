module Plasm.Benchmarks.Program

open BenchmarkDotNet.Running

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | "bench" :: rest ->
        BenchmarkSwitcher
            .FromTypes([| typeof<Bench.GraphLoad>; typeof<Bench.Siblings> |])
            .Run(List.toArray rest, Bench.config ())
        |> ignore

        0
    | [ "profile" ] ->
        (Counts.planning ()).GetAwaiter().GetResult()
        (Counts.floor ()).GetAwaiter().GetResult()
        (Counts.profile ()).GetAwaiter().GetResult()
        0
    | [ "spread" ] ->
        (Counts.spread ()).GetAwaiter().GetResult()
        0
    | [ "counts" ]
    | [] ->
        let results = (Counts.all ()).GetAwaiter().GetResult()
        Counts.print results
        0
    | _ ->
        eprintfn "usage: dotnet run -c Release -- [counts | bench [BenchmarkDotNet args]]"
        2
