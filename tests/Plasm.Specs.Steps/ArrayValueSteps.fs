/// Every scalar a list column holds, at the values that test its form: each
/// stored as a column of its type holds it, read back as it was, and found by
/// List.contains (array-columns.feature).
namespace Plasm.Specs.ArrayValues

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs
open Plasm.TestDomain.Database

type SampleId = SampleId of int64

[<Table "list_samples">]
type Sample =
    { Id: SampleId
      Name: string
      Guids: Guid list
      Times: DateTime list
      Moments: DateTimeOffset list
      Days: DateOnly list
      Hours: TimeOnly list
      Flags: bool list
      Bigs: int64 list
      Doubles: float list
      Amounts: decimal list }

module Samples =
    let model = Model.buildOrFail [] [ typeof<Sample> ]

    let guid = Guid.Parse "0f8fad5b-d9cb-469f-a165-70867728950e"
    // Microseconds: PostgreSQL keeps no finer, so both databases read these back.
    let time = DateTime(2026, 11, 21, 9, 5, 7, DateTimeKind.Utc).AddTicks 1234560L
    let moment = DateTimeOffset(2026, 11, 21, 9, 5, 7, TimeSpan.Zero).AddTicks 1234560L
    let day = DateOnly(2026, 11, 21)
    let hour = TimeOnly(9, 5, 7).Add(TimeSpan.FromTicks 1234560L)
    let big = 9007199254740993L // 2^53 + 1: past what a double holds

    let sample =
        { Id = SampleId 0L
          Name = "edges"
          Guids = [ guid; Guid.Empty ]
          Times = [ time ]
          Moments = [ moment ]
          Days = [ day; DateOnly.MinValue ]
          Hours = [ hour; TimeOnly.MinValue ]
          Flags = [ true; false ]
          Bigs = [ big; Int64.MaxValue; Int64.MinValue ]
          Doubles = [ 0.1; 1e308; -2.5e-300 ]
          Amounts = [ 2.50m; 0.000001m; -79228162514264337593543950335m ] }

[<Binding>]
type ArrayValueSteps(w: World) =

    let repo () = w.Get<Repo> "samplesRepo"

    let found (q: Query<Sample>) =
        task {
            let! r = repo().All q
            return ok r |> List.map (fun s -> s.Name)
        }

    [<StepDefinition("a sample holding every scalar a list can hold, at the values that test its form")>]
    member _.Stored() =
        task {
            let repo = w.Db.RepoWith(model = Samples.model)
            let! _ = w.Db.Exec("drop table if exists list_samples; " + repo.Ddl())
            let! r = repo.Transact(Tx.insert Samples.sample: Tx<_, unit>)
            ok r |> ignore
            w.Set("samplesRepo", repo)
        }
        :> Task

    [<StepDefinition("the sample is read back as it was written")>]
    member _.ReadBack() =
        task {
            let! r = repo().All(Query.from (tableOf<Sample>))
            let read = (ok r).Head
            equal { Samples.sample with Id = read.Id } read "the sample read"
            check (read.Amounts.Head.Scale = 2uy) $"2.50 keeps its scale: {read.Amounts.Head}"
        }
        :> Task

    [<StepDefinition("each of its values is found in its list by List.contains, the decimal by value")>]
    member _.Contains() =
        task {
            let guid, time, moment, day, hour, big = Samples.guid, Samples.time, Samples.moment, Samples.day, Samples.hour, Samples.big

            let queries =
                [ "Guid", plasm { for s in tableOf<Sample> do where (List.contains guid s.Guids) }
                  "DateTime", plasm { for s in tableOf<Sample> do where (List.contains time s.Times) }
                  "DateTimeOffset", plasm { for s in tableOf<Sample> do where (List.contains moment s.Moments) }
                  "DateOnly", plasm { for s in tableOf<Sample> do where (List.contains day s.Days) }
                  "TimeOnly", plasm { for s in tableOf<Sample> do where (List.contains hour s.Hours) }
                  "bool", plasm { for s in tableOf<Sample> do where (List.contains false s.Flags) }
                  "int64 past 2^53", plasm { for s in tableOf<Sample> do where (List.contains big s.Bigs) }
                  "int64 max", plasm { for s in tableOf<Sample> do where (List.contains Int64.MaxValue s.Bigs) }
                  "double", plasm { for s in tableOf<Sample> do where (List.contains 0.1 s.Doubles) }
                  "decimal 2.5 for 2.50", plasm { for s in tableOf<Sample> do where (List.contains 2.5m s.Amounts) } ]

            for what, q in queries do
                let! names = found q
                equal [ "edges" ] names $"the sample found by its {what}"

            // And a value not there is not found.
            let! none = found (plasm { for s in tableOf<Sample> do where (List.contains (big + 1L) s.Bigs) })
            equal [] none "a sample found by 2^53 + 2"
        }
        :> Task

    [<StepDefinition("a sample with NaN among its doubles is written")>]
    member _.NaN() =
        task {
            let repo = w.Db.RepoWith(model = Samples.model)
            let! _ = w.Db.Exec("drop table if exists list_samples; " + repo.Ddl())

            let! outcome =
                task {
                    try
                        let! r = repo.Transact(Tx.insert { Samples.sample with Doubles = [ nan ] }: Tx<_, unit>)
                        return (match r with Ok _ -> "stored" | Error e -> e.Display)
                    with e ->
                        return e.GetBaseException().Message
                }

            w.Set("nanOutcome", outcome)
        }
        :> Task

    [<StepDefinition("^the write is refused, saying \"(.*)\"$")>]
    member _.NaNRefused(text: string) =
        let outcome = w.Get<string> "nanOutcome"
        check (outcome.Contains text) outcome

    [<StepDefinition("^the sample's (\\w+) column is set behind Plasm's back to (.*)$")>]
    member _.Behind(column: string, text: string) =
        task {
            // Past the column's check, as a file another tool wrote could be.
            let! _ = w.Db.Exec($"PRAGMA ignore_check_constraints = ON; update list_samples set {column} = @v; PRAGMA ignore_check_constraints = OFF;", [ "v", box text ])
            ()
        }
        :> Task

    [<StepDefinition("I read the samples")>]
    member _.ReadSamples() =
        task {
            let! r = repo().All(Query.from (tableOf<Sample>))
            w.Set("samplesRead", r |> Result.map ignore)
        }
        :> Task

    [<StepDefinition("^the read of the samples fails, saying \"(.*)\" and \"(.*)\"$")>]
    member _.ReadFails(a: string, b: string) =
        // A materialization error, as a value Plasm cannot read is.
        match w.Get<Result<unit, DbError>> "samplesRead" with
        | Error(DbError.Materialization m) -> check (m.Message.Contains a && m.Message.Contains b && m.Property = "Bigs") $"%A{m}"
        | other -> failwithf "expected a materialization error, got %A" other
