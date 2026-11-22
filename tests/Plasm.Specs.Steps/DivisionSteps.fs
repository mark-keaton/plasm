namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Database

/// A row whose integer, double and decimal each have a divisor of zero.
module Division =
    type DivRowId = DivRowId of int64

    [<Table "div_rows">]
    type DivRow =
        { Id: DivRowId
          N: int
          Z: int
          X: float
          ZX: float
          D: decimal
          ZD: decimal }

    let model = Model.buildOrFail [] [ typeof<DivRow> ]

    let tables =
        "drop table if exists div_rows;
         create table div_rows (
           id bigint generated always as identity primary key,
           n int not null, z int not null,
           x double precision not null, zx double precision not null,
           d numeric(12,2) not null, zd numeric(12,2) not null);"

open Division

/// Feature: A division by zero fails, on every database
[<Binding>]
type DivisionSteps(w: World) =
    let repo () = w.Get<Repo> "divRepo"

    let read (r: Task<Result<'T, DbError>>) =
        task {
            let! r = r

            match r with
            | Ok _ -> w.Set("divFailure", (None: DbFailure option))
            | Error(DbError.Database f) -> w.Set("divFailure", Some f)
            | Error other -> failwithf "expected a database failure, got %A" other
        }
        :> Task

    let update (u: Tx<int, unit>) =
        task {
            let! r = repo().Transact u

            match r with
            | Ok _ -> w.Set("divFailure", (None: DbFailure option))
            | Error { Failure = TxFailure.Failed(DbError.Database f) } -> w.Set("divFailure", Some f)
            | Error other -> failwithf "expected a database failure, got %A" other
        }
        :> Task

    let rows = Query.from (tableOf<DivRow>)

    [<StepDefinition("^a row whose integer, double and decimal are divided by divisors of zero$")>]
    member _.Row() =
        task {
            do! w.Db.MakeTables(Division.tables, Division.model)
            let r = w.Db.RepoWith(model = Division.model)
            w.Set("divRepo", r)

            let! inserted =
                r.Transact(Tx.insert { Id = DivRowId 0L; N = 7; Z = 0; X = 7.5; ZX = 0.0; D = 7.50m; ZD = 0.00m }: Tx<_, unit>)

            ok inserted |> ignore
        }
        :> Task

    [<StepDefinition("^I (divide the integer|take the integer's remainder|divide the double|divide the decimal|take the decimal's remainder) by zero in (what the query selects|the query's condition|an aggregate's argument|a bulk update's set)$")>]
    member _.ByZero(operation: string, place: string) =
        let r = repo ()

        match operation, place with
        | "divide the integer", "what the query selects" -> read (r.All(plasm { for d in tableOf<DivRow> do select (d.N / d.Z) }))
        | "divide the integer", "the query's condition" -> read (r.All(plasm { for d in tableOf<DivRow> do where (d.N / d.Z > 1) }))
        | "divide the integer", "an aggregate's argument" -> read (r.All(plasm { for d in tableOf<DivRow> do select (Agg.sum (d.N / d.Z)) }))
        | "divide the integer", _ -> update (Tx.execute (Bulk.update (rows, fun d -> { d with N = d.N / d.Z })))
        | "take the integer's remainder", "what the query selects" -> read (r.All(plasm { for d in tableOf<DivRow> do select (d.N % d.Z) }))
        | "take the integer's remainder", "the query's condition" -> read (r.All(plasm { for d in tableOf<DivRow> do where (d.N % d.Z > 1) }))
        | "take the integer's remainder", "an aggregate's argument" -> read (r.All(plasm { for d in tableOf<DivRow> do select (Agg.sum (d.N % d.Z)) }))
        | "take the integer's remainder", _ -> update (Tx.execute (Bulk.update (rows, fun d -> { d with N = d.N % d.Z })))
        | "divide the double", "what the query selects" -> read (r.All(plasm { for d in tableOf<DivRow> do select (d.X / d.ZX) }))
        | "divide the double", "the query's condition" -> read (r.All(plasm { for d in tableOf<DivRow> do where (d.X / d.ZX > 1.0) }))
        | "divide the double", "an aggregate's argument" -> read (r.All(plasm { for d in tableOf<DivRow> do select (Agg.sum (d.X / d.ZX)) }))
        | "divide the double", _ -> update (Tx.execute (Bulk.update (rows, fun d -> { d with X = d.X / d.ZX })))
        | "divide the decimal", "what the query selects" -> read (r.All(plasm { for d in tableOf<DivRow> do select (d.D / d.ZD) }))
        | "divide the decimal", "the query's condition" -> read (r.All(plasm { for d in tableOf<DivRow> do where (d.D / d.ZD > 1m) }))
        | "divide the decimal", "an aggregate's argument" -> read (r.All(plasm { for d in tableOf<DivRow> do select (Agg.sum (d.D / d.ZD)) }))
        | "divide the decimal", _ -> update (Tx.execute (Bulk.update (rows, fun d -> { d with D = d.D / d.ZD })))
        | _, "what the query selects" -> read (r.All(plasm { for d in tableOf<DivRow> do select (d.D % d.ZD) }))
        | _, "the query's condition" -> read (r.All(plasm { for d in tableOf<DivRow> do where (d.D % d.ZD > 1m) }))
        | _, "an aggregate's argument" -> read (r.All(plasm { for d in tableOf<DivRow> do select (Agg.sum (d.D % d.ZD)) }))
        | _ -> update (Tx.execute (Bulk.update (rows, fun d -> { d with D = d.D % d.ZD })))

    [<StepDefinition("^I divide the integer by a captured zero in what the query selects$")>]
    member _.ByCapturedZero() =
        let zero = 0
        read (repo().All(plasm { for d in tableOf<DivRow> do select (d.N / zero) }))

    [<StepDefinition("^it fails as a database failure naming a division by zero$")>]
    member _.Failed() =
        match w.Get<DbFailure option> "divFailure" with
        | Some f ->
            equal DbFailureKind.OtherDatabaseError f.Kind "kind"
            check (f.Message.ToLowerInvariant().Contains "division by zero") f.Message

            if w.Db.Kind = PostgreSQL then
                equal (Some "22012") f.SqlState "SQLSTATE"
        | None -> failwith "the division by zero did not fail"

    [<StepDefinition("^I divide the integer, the double and the decimal by their divisors plus one$")>]
    member _.ByOne() =
        task {
            let! r = repo().All(plasm { for d in tableOf<DivRow> do select (d.N / (d.Z + 1), d.X / (d.ZX + 1.0), d.D / (d.ZD + 1m)) })
            w.Set("quotients", ok r)
        }
        :> Task

    [<StepDefinition("^the quotients are 7, 7.5 and 7.50$")>]
    member _.Quotients() =
        equal [ 7, 7.5, 7.50m ] (w.Get<(int * float * decimal) list> "quotients") "quotients"

    // A literal divisor: a constant SQLite must not evaluate ahead of its CASE.
    [<StepDefinition("^I divide the integer by the literal 2 in what the query selects$")>]
    member _.ByTwo() =
        task {
            let! r = repo().All(plasm { for d in tableOf<DivRow> do select (d.N / 2) })
            w.Set("quotient", ok r)
        }
        :> Task

    [<StepDefinition("^the quotient is 3$")>]
    member _.Three() = equal [ 3 ] (w.Get<int list> "quotient") "quotient"

