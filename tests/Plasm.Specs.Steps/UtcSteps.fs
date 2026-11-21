/// Feature: Only UTC times are stored, and a value that cannot be sent is a failure
namespace Plasm.Specs.Utc

open System
open System.Threading
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs

type AppointmentId = AppointmentId of int64

/// A value record holding times: two columns of the appointment's row.
type Period = { From: DateTime; Until: DateTime }

[<Table "appointments">]
type Appointment =
    { Id: AppointmentId
      Title: string
      When: DateTime
      At: DateTimeOffset
      Window: Period }

module Appointments =
    let model = Model.buildOrFail [] [ typeof<Appointment> ]

    let eight = DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc)
    let atEight = DateTimeOffset(eight)

    let utc title =
        { Id = AppointmentId 0L
          Title = title
          When = eight
          At = atEight
          Window = { From = eight; Until = eight.AddHours 1.0 } }

    /// "08:00 with Kind Local", "09:00 at the offset +01:00" and the like.
    let time (text: string) : obj =
        let hour = int (text.Substring(0, 2))

        if text.Contains "Kind" then
            let kind = Enum.Parse<DateTimeKind>(text.Substring(text.LastIndexOf ' ' + 1))
            box (DateTime(2026, 11, 21, hour, 0, 0, kind))
        else
            let offset = TimeSpan.Parse(text.Substring(text.LastIndexOf ' ' + 2))
            box (DateTimeOffset(2026, 11, 21, hour, 0, 0, offset))

[<Binding>]
type UtcSteps(w: World) =

    let repo () = w.Get<Repo> "utcRepo"

    /// A transaction's failure by its case, each field error with its field.
    let failed (e: TransactionError<unit>) =
        match e.Failure with
        | TxFailure.InvalidChanges(entity, errors) ->
            "InvalidChanges " + entity + " " + String.concat "; " [ for er in errors -> $"Field = \"{er.Field}\" {er.Kind}: {er.Message}" ]
        | TxFailure.Failed d -> "Failed " + (match d with DbError.Database f -> $"Database {f.Kind}: {f.Message}" | d -> string d)
        | other -> sprintf "%A" other

    /// A read's failure by its case.
    let readFailed (e: DbError) =
        match e with
        | DbError.Translation t -> "Translation " + string t
        | DbError.Database f -> $"Database {f.Kind}: {f.Message}"
        | other -> string other

    let attempt (f: unit -> Task<string>) =
        task {
            try
                return! f ()
            with e ->
                return "THROWN " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message
        }

    let appointments () =
        task {
            let! r = repo().All(plasm { for a in tableOf<Appointment> do orderBy a.Id })
            return ok r
        }

    [<StepDefinition("a table of appointments")>]
    member _.Table() =
        task {
            let repo = w.Db.RepoWith(model = Appointments.model)
            let! _ = w.Db.Exec("drop table if exists appointments; " + repo.Ddl())
            w.Set("utcRepo", repo)
        }
        :> Task

    [<StepDefinition("^I insert an appointment whose (When|At) is (.*)$")>]
    member _.InsertOne(field: string, value: string) =
        task {
            let a = Appointments.utc "one"
            let v = Appointments.time value
            let a = if field = "When" then { a with When = unbox v } else { a with At = unbox v }
            let! outcome = attempt (fun () -> task { let! r = repo().Transact(Tx.insert a: Tx<_, unit>) in return (match r with Ok _ -> "stored" | Error e -> failed e) })
            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("^the (write|transaction) is refused as an invalid change of Appointment.(\\w+), saying \"(.*)\"$")>]
    member _.Refused(_what: string, field: string, text: string) =
        let outcome = w.Get<string> "utcOutcome"
        check (outcome.Contains "InvalidChanges" && outcome.Contains $"Field = \"{field}\"" && outcome.Contains text) outcome

    [<StepDefinition("no appointment is stored")>]
    member _.NoneStored() =
        task {
            let! all = appointments ()
            equal [] all "the appointments stored"
        }
        :> Task

    [<StepDefinition("I insert an appointment at 08:00 UTC, as a DateTime and as a DateTimeOffset")>]
    member _.InsertUtc() =
        task {
            let! r = repo().Transact(Tx.insert (Appointments.utc "one"): Tx<_, unit>)
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("it is read back with When 08:00 with Kind Utc and At 08:00 at the offset +00:00")>]
    member _.ReadUtc() =
        task {
            let! all = appointments ()
            let a = List.exactlyOne all
            check (a.When = Appointments.eight && a.When.Kind = DateTimeKind.Utc) $"When {a.When:o} {a.When.Kind}"
            check (a.At = Appointments.atEight && a.At.Offset = TimeSpan.Zero) $"At {a.At:o}"
        }
        :> Task

    [<StepDefinition("an appointment at 08:00 UTC")>]
    member _.OneAtEight() =
        task {
            let! r = repo().Transact(Tx.insert (Appointments.utc "first"): Tx<_, unit>)
            w.Set("utcFirst", ok r)
        }
        :> Task

    [<StepDefinition("a transaction inserts a second and then moves the first to 09:00 with Kind Unspecified")>]
    member _.MoveInTx() =
        task {
            let first = w.Get<Appointment> "utcFirst"
            let nine = DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Unspecified)

            let! outcome =
                attempt (fun () ->
                    task {
                        let! r =
                            repo().Transact(
                                transaction {
                                    let! _ = Tx.insert (Appointments.utc "second")
                                    let! _ = Tx.update (first, { first with When = nine })
                                    return ()
                                }: Tx<unit, unit>
                            )

                        return (match r with Ok _ -> "stored" | Error e -> failed e)
                    })

            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("there is one appointment, at 08:00 UTC")>]
    member _.OneLeft() =
        task {
            let! all = appointments ()
            equal 1 all.Length "appointments"
            check (all.Head.When = Appointments.eight) $"{all.Head.When:o}"
        }
        :> Task

    [<StepDefinition("I move every appointment to 09:00 with Kind Local in one bulk update")>]
    member _.BulkMove() =
        task {
            let nine = DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Local)

            let! outcome =
                attempt (fun () ->
                    task {
                        let! r = repo().Transact(Tx.execute (Bulk.update (Query.from (tableOf<Appointment>), fun a -> { a with When = nine })): Tx<_, unit>)
                        return (match r with Ok _ -> "stored" | Error e -> failed e)
                    })

            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("^the transaction fails, saying \"(.*)\"$")>]
    member _.TxFails(text: string) =
        let outcome = w.Get<string> "utcOutcome"
        check (not (outcome.StartsWith "THROWN") && outcome <> "stored" && outcome.Contains text) outcome

    [<StepDefinition("^I read the appointments (in a query|in a stream|through a prepared query|among a prepared list of times|by their window) by 08:00 with Kind Unspecified$")>]
    member _.ReadBy(how: string) =
        task {
            let eight = DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Unspecified)

            let! outcome =
                attempt (fun () ->
                    task {
                        match how with
                        | "in a query" ->
                            let! r = repo().All(plasm { for a in tableOf<Appointment> do where (a.When = eight) })
                            return (match r with Ok xs -> $"read {xs.Length}" | Error e -> readFailed e)
                        | "in a stream" ->
                            let rows = repo().Stream(plasm { for a in tableOf<Appointment> do where (a.When = eight) }).GetAsyncEnumerator(CancellationToken.None)
                            let! first = rows.MoveNextAsync()
                            let result = if first then (match rows.Current with Ok _ -> "read a row" | Error e -> readFailed e) else "no rows"
                            do! rows.DisposeAsync()
                            return result
                        | "through a prepared query" ->
                            let byTime = Query.prepare (fun (t: Arg<DateTime>) -> plasm { for a in tableOf<Appointment> do where (a.When = t.Value) })
                            let! r = repo().All(byTime eight)
                            return (match r with Ok xs -> $"read {xs.Length}" | Error e -> readFailed e)
                        | "among a prepared list of times" ->
                            let among = Query.prepare (fun (ts: Arg<DateTime list>) -> plasm { for a in tableOf<Appointment> do where (List.contains a.When ts.Value) })
                            let! r = repo().All(among [ Appointments.eight; eight ])
                            return (match r with Ok xs -> $"read {xs.Length}" | Error e -> readFailed e)
                        | _ ->
                            let window = { From = eight; Until = eight.AddHours 1.0 }
                            let! r = repo().All(plasm { for a in tableOf<Appointment> do where (a.Window = window) })
                            return (match r with Ok xs -> $"read {xs.Length}" | Error e -> readFailed e)
                    })

            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("^the read is refused as a query that cannot be translated, naming the value and saying \"(.*)\"$")>]
    member _.QueryRefused(text: string) =
        let outcome = w.Get<string> "utcOutcome"
        check (outcome.Contains "Translation" && outcome.Contains "2026-11-21T08:00:00.0000000 has Kind Unspecified" && outcome.Contains text) outcome

    [<StepDefinition("a transaction's own command sends a Uri, which no provider can send, as a parameter")>]
    member _.Unsendable() =
        task {
            let sendUri =
                Tx.run (fun ctx ->
                    task {
                        use cmd = ctx.CreateCommand()
                        cmd.CommandText <- "select count(*) from appointments where title = @t"
                        let p = cmd.CreateParameter()
                        p.ParameterName <- "t"
                        p.Value <- Uri "https://example.com/"
                        cmd.Parameters.Add p |> ignore
                        let! _ = cmd.ExecuteScalarAsync(ctx.CancellationToken)
                        return Ok()
                    })

            let! outcome =
                attempt (fun () ->
                    task {
                        let! r = repo().Transact(sendUri: Tx<unit, unit>)
                        return (match r with Ok _ -> "sent" | Error e -> failed e)
                    })

            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("I read appointments with hand-written SQL comparing the title with a Uri")>]
    member _.SqlWithUri() =
        task {
            let uri = Uri "https://example.com/"

            let! outcome =
                attempt (fun () ->
                    task {
                        let! r = repo().Sql<Appointment>($"SELECT * FROM appointments WHERE title = {uri}")
                        return (match r with Ok xs -> $"read {xs.Length}" | Error(DbError.Translation(TranslationError.UnmappedType t)) -> $"UnmappedType {t}" | Error e -> readFailed e)
                    })

            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("the read is refused as a query naming the type Uri, which Plasm does not map")>]
    member _.UnmappedUri() =
        let outcome = w.Get<string> "utcOutcome"
        check (outcome.StartsWith "UnmappedType" && outcome.Contains "Uri") outcome

    [<StepDefinition("the transaction fails as a database failure, not an exception")>]
    member _.DatabaseFailure() =
        let outcome = w.Get<string> "utcOutcome"
        check (not (outcome.StartsWith "THROWN") && outcome.Contains "Database") outcome

    // ---- kept to the microsecond, ordered, and held to one form (SQLite) ----

    static member private Utc(text: string) =
        DateTime.ParseExact(text, [| "yyyy-MM-dd HH:mm:ss"; "yyyy-MM-dd HH:mm:ss.FFFFFFF" |], Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal ||| Globalization.DateTimeStyles.AssumeUniversal)

    [<StepDefinition("^I insert an appointment whose When and At are (.*) UTC$")>]
    member _.InsertAt(text: string) =
        task {
            let t = UtcSteps.Utc text
            let! r = repo().Transact(Tx.insert { Appointments.utc "one" with When = t; At = DateTimeOffset t }: Tx<_, unit>)
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("^it is read back with When and At (.*) UTC$")>]
    member _.ReadKept(text: string) =
        task {
            let kept = UtcSteps.Utc text
            let! all = appointments ()
            let a = List.exactlyOne all
            check (a.When = kept && a.When.Kind = DateTimeKind.Utc) $"When {a.When:o} {a.When.Kind}, not {kept:o}"
            check (a.At = DateTimeOffset kept && a.At.Offset = TimeSpan.Zero) $"At {a.At:o}, not {kept:o}"
        }
        :> Task

    [<StepDefinition("^it is found by When and by At (.*) UTC$")>]
    member _.FoundBy(text: string) =
        task {
            let t = UtcSteps.Utc text
            let at = DateTimeOffset t
            let! byWhen = repo().All(plasm { for a in tableOf<Appointment> do where (a.When = t) })
            let! byAt = repo().All(plasm { for a in tableOf<Appointment> do where (a.At = at) })
            equal (1, 1) ((ok byWhen).Length, (ok byAt).Length) "found by When, by At"
        }
        :> Task

    [<StepDefinition("^appointments at (.*) UTC$")>]
    member _.AtTimes(times: string) =
        task {
            for text in times.Replace(" and ", ", ").Split(", ") do
                let t = UtcSteps.Utc("2026-11-21 " + text)
                let! r = repo().Transact(Tx.insert { Appointments.utc text with When = t; At = DateTimeOffset t }: Tx<_, unit>)
                ok r |> ignore
        }
        :> Task

    [<StepDefinition("I read the appointments ordered by When and by At")>]
    member _.Ordered() =
        task {
            let! byWhen = repo().All(plasm { for a in tableOf<Appointment> do orderBy a.When })
            let! byAt = repo().All(plasm { for a in tableOf<Appointment> do orderBy a.At })
            w.Set("utcOrders", ((ok byWhen) |> List.map _.Title, (ok byAt) |> List.map _.Title))
        }
        :> Task

    [<StepDefinition("^both are in the order (.*)$")>]
    member _.InOrder(order: string) =
        let expected = order.Split(", ") |> List.ofArray
        let byWhen, byAt = w.Get<string list * string list> "utcOrders"
        equal (expected, expected) (byWhen, byAt) "by When, by At"

    [<StepDefinition("^the appointment's (\\w+) column is set to \"(.*)\", as another tool might write it$")>]
    member _.SetByTool(column: string, text: string) =
        task {
            let! outcome = attempt (fun () -> task { let! _ = w.Db.Exec($"update appointments set \"{column}\" = @v", [ "v", box text ]) in return "set" })
            w.Set("utcOutcome", outcome)
        }
        :> Task

    [<StepDefinition("the database refuses it, by the column's check")>]
    member _.RefusedByCheck() =
        let outcome = w.Get<string> "utcOutcome"
        check (outcome.StartsWith "THROWN" && outcome.Contains "CHECK constraint failed") outcome

    [<StepDefinition("^the appointment's (\\w+) column is set past its check to \"(.*)\"$")>]
    member _.SetPastCheck(column: string, text: string) =
        task {
            let! _ = w.Db.Exec($"PRAGMA ignore_check_constraints = ON; update appointments set \"{column}\" = @v; PRAGMA ignore_check_constraints = OFF;", [ "v", box text ])
            ()
        }
        :> Task

    [<StepDefinition("I read the appointments")>]
    member _.ReadAppointments() =
        task {
            let! outcome = attempt (fun () -> task { let! r = repo().All(Query.from (tableOf<Appointment>)) in return (match r with Ok xs -> $"read {xs.Length}" | Error e -> readFailed e) })
            w.Set("utcRead", outcome)
        }
        :> Task

    [<StepDefinition("^the read of the appointments fails, saying \"(.*)\" and \"(.*)\"$")>]
    member _.ReadFails(column: string, text: string) =
        let outcome = w.Get<string> "utcRead"
        check (not (outcome.StartsWith "read") && not (outcome.StartsWith "THROWN") && outcome.Contains column && outcome.Contains text) outcome
