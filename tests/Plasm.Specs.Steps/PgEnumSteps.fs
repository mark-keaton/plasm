namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain.Domain

/// A domain of its own: its record labels would otherwise shadow Order's.
module PgEnums =

    type ShipmentId = ShipmentId of int64

    /// Stored as the PostgreSQL enum type shipment_status (pg-enums.feature).
    type ShipmentStatus =
        | Packed
        | Shipped
        | Delivered

    [<Table "shipments">]
    type Shipment =
        { Id: ShipmentId
          Tracking: string
          Status: ShipmentStatus
          PreviousStatus: ShipmentStatus option }

    let parse =
        function
        | "Packed" -> Packed
        | "Shipped" -> Shipped
        | "Delivered" -> Delivered
        | other -> failwith $"not a ShipmentStatus: {other}"

    let labels = [ Packed, "packed"; Shipped, "shipped"; Delivered, "delivered" ]

    let model =
        Model.buildOrFail
            [ Scalar.wrap ShipmentId (fun (ShipmentId v) -> v)
              Scalar.enum labels |> Scalar.asPgEnum "shipment_status" ]
            [ typeof<Shipment> ]

    /// The enum type and the table using it, with the given labels in order.
    let create (typeLabels: string list) =
        let quoted = typeLabels |> List.map (fun l -> $"'{l}'") |> String.concat ", "

        $"create type shipment_status as enum ({quoted});
          create table shipments (
            id bigint generated always as identity primary key,
            tracking text not null unique,
            status shipment_status not null,
            previous_status shipment_status);"

    let drop = "drop table if exists shipments; drop type if exists shipment_status;"

    let unsaved tracking status previous =
        { Id = ShipmentId 0L
          Tracking = tracking
          Status = status
          PreviousStatus = previous }

open PgEnums

/// Feature: Fieldless unions stored as PostgreSQL enum types
[<Binding>]
type PgEnumSteps(w: World) =

    let repo () = w.Get<Repo> "shipmentRepo"

    let all () =
        task {
            let! r = repo().All(plasm { for s in tableOf<Shipment> do orderBy s.Id })
            return ok r
        }

    let recreate (typeLabels: string list) =
        task {
            let! _ = w.Db.Exec(PgEnums.drop + PgEnums.create typeLabels)
            return ()
        }

    let statuses (rows: Shipment list) = rows |> List.map (fun s -> s.Tracking, s.Status)

    [<StepDefinition("a shipment_status enum type with the labels packed, shipped and delivered, in that order")>]
    member _.EnumType() = recreate [ "packed"; "shipped"; "delivered" ] |> unit

    [<StepDefinition("Shipments whose Status is mapped to the shipment_status type")>]
    member _.Mapped() =
        w.Set("shipmentRepo", w.Db.RepoWith(model = PgEnums.model, includeParameterValues = true))

    // Rule: Values are written and read without driver configuration

    [<StepDefinition("^I insert a Shipment with status (Packed|Shipped|Delivered)$")>]
    member _.Insert(status: string) =
        task {
            let s = PgEnums.unsaved "T-1" (PgEnums.parse status) None
            let! r = repo().Transact(Tx.insert s: Tx<_, unit>)
            w.Set("inserted", ok r)
        }
        |> unit

    [<StepDefinition("^reading it back gives status (Packed|Shipped|Delivered)$")>]
    member _.ReadBack(status: string) =
        task {
            let id = (w.Get<Shipment> "inserted").Id
            let! r = repo().All(plasm { for s in tableOf<Shipment> do where (s.Id = id) })
            equal [ PgEnums.parse status ] (ok r |> List.map (fun s -> s.Status)) "status read back"
        }
        |> unit

    [<StepDefinition("the data source connected before the shipment_status type was created")>]
    member _.ConnectedBefore() =
        task {
            // The repo's data source loads the database's types on its first
            // connection; the type is then replaced, so its loaded types are stale.
            let! r = repo().Count(plasm { for s in tableOf<Shipment> do where (s.Tracking = "none") })
            ok r |> ignore
            do! recreate [ "packed"; "shipped"; "delivered" ]
        }
        |> unit

    [<StepDefinition("a Shipment with status Packed")>]
    member _.APacked() =
        task {
            let! r = repo().Transact(Tx.insert (PgEnums.unsaved "T-1" Packed None): Tx<_, unit>)
            w.Set("inserted", ok r)
        }
        |> unit

    [<StepDefinition("I update its status to Delivered")>]
    member _.UpdateToDelivered() =
        task {
            let! r = repo().Transact(w.Get<Shipment> "inserted" |> edit (fun s -> { s with Status = Delivered }))
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the database holds the label delivered")>]
    member _.HoldsDelivered() =
        task {
            let (ShipmentId id) = (w.Get<Shipment> "inserted").Id
            let! label = w.Db.Scalar<string>("select status::text from shipments where id = @id", [ "id", box id ])
            equal "delivered" label "stored label"
        }
        |> unit

    [<StepDefinition("I insert Shipments with and without a previous status")>]
    member _.InsertPrevious() =
        task {
            let! a = repo().Transact(Tx.insert (PgEnums.unsaved "T-1" Shipped (Some Packed)): Tx<_, unit>)
            let! b = repo().Transact(Tx.insert (PgEnums.unsaved "T-2" Packed None): Tx<_, unit>)
            ok a |> ignore
            ok b |> ignore
        }
        |> unit

    [<StepDefinition("each reads back with the previous status it was given")>]
    member _.PreviousBack() =
        task {
            let! rows = all ()
            equal [ "T-1", Some Packed; "T-2", None ] (rows |> List.map (fun s -> s.Tracking, s.PreviousStatus)) "previous statuses"
            let! r = repo().All(plasm { for s in tableOf<Shipment> do where (s.PreviousStatus = Some Packed) })
            equal [ "T-1" ] (ok r |> List.map (fun s -> s.Tracking)) "filtered on the optional status"
        }
        |> unit

    [<StepDefinition("I bulk insert Shipments with every status")>]
    member _.BulkInsert() =
        task {
            let! r =
                repo().Transact(Tx.execute (Bulk.insert
                        [ PgEnums.unsaved "T-1" Packed None
                          PgEnums.unsaved "T-2" Shipped (Some Packed)
                          PgEnums.unsaved "T-3" Delivered (Some Shipped)
                          PgEnums.unsaved "T-4" Packed None ]): Tx<_, unit>)

            equal [ Packed; Shipped; Delivered; Packed ] (ok r |> List.map (fun s -> s.Status)) "statuses returned by the insert"
        }
        |> unit

    [<StepDefinition("I upsert one of them with status Delivered")>]
    member _.Upsert() =
        task {
            let! r =
                repo().Transact(Tx.execute (Bulk.upsert (
                        [ PgEnums.unsaved "T-2" Delivered None ],
                        (fun s -> s.Tracking),
                        fun current incoming -> { current with Status = incoming.Status }
                    )): Tx<_, unit>)

            equal [ Delivered ] (ok r |> List.map (fun s -> s.Status)) "status returned by the upsert"
        }
        |> unit

    [<StepDefinition("I bulk update the Packed Shipments to Shipped")>]
    member _.BulkUpdate() =
        task {
            let packed = plasm { for s in tableOf<Shipment> do where (s.Status = Packed) }
            let! n = repo().Transact(Tx.execute (Bulk.update (packed, fun s -> { s with Status = Shipped; PreviousStatus = Some Packed })): Tx<_, unit>)
            equal 2 (ok n) "Shipments updated"
        }
        |> unit

    [<StepDefinition("every Shipment holds the status F# expects")>]
    member _.AllExpected() =
        task {
            let! rows = all ()

            equal
                [ "T-1", Shipped, Some Packed
                  "T-2", Delivered, Some Packed
                  "T-3", Delivered, Some Shipped
                  "T-4", Shipped, Some Packed ]
                (rows |> List.map (fun s -> s.Tracking, s.Status, s.PreviousStatus))
                "Shipments"
        }
        |> unit

    // Rule: Queries keep their F# meaning

    [<StepDefinition("Shipments with every status")>]
    member _.EveryStatus() =
        task {
            // Neither insertion order nor alphabetical order agrees with the cases.
            let! _ =
                w.Db.Exec
                    "insert into shipments (tracking, status) values
                       ('T-1', 'delivered'), ('T-2', 'packed'), ('T-3', 'shipped'), ('T-4', 'packed'), ('T-5', 'delivered')"

            return ()
        }
        |> unit

    [<StepDefinition("I keep Shipments whose status is Shipped")>]
    member _.KeepShipped() =
        task {
            let! r = repo().All(plasm { for s in tableOf<Shipment> do where (s.Status = Shipped) })
            w.Set("kept", ok r)
        }
        |> unit

    [<StepDefinition("^only the (Shipped|Delivered) Shipments are returned$")>]
    member _.OnlyThose(status: string) =
        task {
            let expected = PgEnums.parse status
            let! everyone = all ()
            let wanted = everyone |> List.filter (fun s -> s.Status = expected) |> statuses
            check (not wanted.IsEmpty) "the seed has Shipments with that status"
            equal wanted (w.Get<Shipment list> "kept" |> List.sortBy (fun s -> s.Id) |> statuses) "Shipments returned"
        }
        |> unit

    [<StepDefinition("^I keep Shipments whose status is in a captured (list|array|set) of Packed and Delivered$")>]
    member _.KeepCaptured(container: string) =
        task {
            let query, predicate =
                match container with
                | "list" ->
                    let xs = [ Packed; Delivered ]
                    plasm { for s in tableOf<Shipment> do where (List.contains s.Status xs) }, (fun (s: Shipment) -> List.contains s.Status xs)
                | "array" ->
                    let xs = [| Packed; Delivered |]
                    plasm { for s in tableOf<Shipment> do where (Array.contains s.Status xs) }, (fun s -> Array.contains s.Status xs)
                | _ ->
                    let xs = set [ Packed; Delivered ]
                    plasm { for s in tableOf<Shipment> do where (xs.Contains s.Status) }, (fun s -> xs.Contains s.Status)

            let! r = repo().All query
            let! everyone = all ()
            w.Set("compared", (ok r |> List.sortBy (fun s -> s.Id) |> statuses, everyone |> List.filter predicate |> statuses))
        }
        |> unit

    [<StepDefinition("the result equals evaluating the predicate over the same Shipments in F#")>]
    member _.AsFSharp() =
        let rows, expected = w.Get<(string * ShipmentStatus) list * (string * ShipmentStatus) list> "compared"
        equal expected rows "Shipments compared with F#"

    [<StepDefinition("I sort Shipments by status, and keep those whose status is after Packed")>]
    member _.SortAndCompare() =
        task {
            let! sorted =
                repo().All(
                    plasm {
                        for s in tableOf<Shipment> do
                            orderBy s.Status
                            thenBy s.Id
                    }
                )

            let! after = repo().All(plasm { for s in tableOf<Shipment> do where (s.Status > Packed) })
            w.Set("sorted", ok sorted |> statuses)
            w.Set("after", ok after |> List.sortBy (fun s -> s.Id) |> statuses)
        }
        |> unit

    [<StepDefinition("both equal doing the same over the Shipments in F#")>]
    member _.BothAsFSharp() =
        task {
            let! everyone = all ()
            equal (everyone |> List.sortBy (fun s -> s.Status, s.Id) |> statuses) (w.Get "sorted") "Shipments sorted by status"
            equal (everyone |> List.filter (fun s -> s.Status > Packed) |> statuses) (w.Get "after") "Shipments after Packed"
        }
        |> unit

    [<StepDefinition("I select the minimum and maximum status of the Shipments")>]
    member _.MinMax() =
        task {
            let! r = repo().All(plasm { for s in tableOf<Shipment> do select (Agg.min s.Status, Agg.max s.Status) })
            w.Set("minMax", ok r)
        }
        |> unit

    [<StepDefinition("they are Packed and Delivered")>]
    member _.PackedAndDelivered() =
        equal [ Some Packed, Some Delivered ] (w.Get<(ShipmentStatus option * ShipmentStatus option) list> "minMax") "minimum and maximum"

    [<StepDefinition("I run a prepared query for Shipments with a status argument of Delivered")>]
    member _.Prepared() =
        task {
            let withStatus =
                Query.prepare (fun (status: Arg<ShipmentStatus>) -> plasm { for s in tableOf<Shipment> do where (s.Status = status.Value) })

            let! r = repo().All(withStatus Delivered)
            w.Set("kept", ok r)
        }
        |> unit

    [<StepDefinition("I read Shipments with raw SQL that filters on the status Shipped")>]
    member _.RawSql() =
        task {
            let status = Shipped
            let! r = repo().Sql<Shipment>($"SELECT * FROM shipments WHERE status = {status} ORDER BY id")
            w.Set("kept", ok r)
        }
        |> unit

    [<StepDefinition("I explain a query that filters on a status")>]
    member _.Explain() =
        w.Set("sql", (repo().Explain(plasm { for s in tableOf<Shipment> do where (s.Status = Shipped) }) |> ok).Sql)

    [<StepDefinition("the parameter is cast to the enum type and the column is not")>]
    member _.CastOnParameter() =
        let sql = w.Get<string> "sql"
        let where = sql.Substring(sql.IndexOf " WHERE ")
        check (where.Contains "\"t0\".\"status\" = CAST(@p0 AS \"shipment_status\")") $"the parameter is not cast: {sql}"
        check (not (where.Contains "CAST(\"t0\"")) $"the column is cast: {sql}"

    // Rule: The schema check finds mismatches before a request fails

    [<StepDefinition("I check the Shipments model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("issues", ok r)
        }
        |> unit

    [<StepDefinition("no problems are reported for the Shipments")>]
    member _.NoProblems() =
        equal [] (w.Get<SchemaIssue list> "issues" |> List.map string) "schema issues"

    [<StepDefinition("the status column is text")>]
    member _.TextColumn() =
        task {
            let! _ = w.Db.Exec "alter table shipments alter column status type text"
            return ()
        }
        |> unit

    [<StepDefinition("the status column has another enum type, and shipment_status does not exist")>]
    member _.OtherEnum() =
        task {
            let! _ = w.Db.Exec(PgEnums.drop + (PgEnums.create [ "packed"; "shipped"; "delivered" ]).Replace("shipment_status", "parcel_state"))
            return ()
        }
        |> unit

    [<StepDefinition("the shipment_status type also has the label lost")>]
    member _.ExtraLabel() =
        task {
            let! _ = w.Db.Exec "alter type shipment_status add value 'lost'"
            return ()
        }
        |> unit

    [<StepDefinition("the shipment_status type has only the labels packed and shipped")>]
    member _.MissingLabel() = recreate [ "packed"; "shipped" ] |> unit

    [<StepDefinition("the shipment_status type declares delivered before shipped")>]
    member _.Reordered() = recreate [ "packed"; "delivered"; "shipped" ] |> unit

    [<StepDefinition("^an? (\\w+) issue for Shipment\\.(\\w+) says \"(.*)\"$")>]
    member _.Issue(kind: string, field: string, message: string) =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i -> string i.Kind = kind && i.Model = "Shipment" && i.Field = Some field && i.Message = message))
            $"""no {kind} issue for Shipment.{field} saying "{message}"; found: {issues |> List.map string}"""

    // Rule: Mistakes are reported where they are made

    [<StepDefinition("the shipment_status type also has the label lost, and a Shipment is stored with it")>]
    member _.StoredLost() =
        task {
            let! _ = w.Db.Exec "alter type shipment_status add value 'lost'"
            let! _ = w.Db.Exec "insert into shipments (tracking, status) values ('T-1', 'lost')"
            return ()
        }
        |> unit

    [<StepDefinition("I read the Shipments")>]
    member _.ReadAll() =
        task {
            let! r = repo().All(plasm { for s in tableOf<Shipment> do orderBy s.Id })
            w.Set("read", r)
        }
        |> unit

    [<StepDefinition("the read fails with a materialization error saying \"(.*)\"")>]
    member _.MaterializationFails(message: string) =
        match w.Get<Result<Shipment list, DbError>> "read" with
        | Error(DbError.Materialization m) -> check (m.Message.Contains message) $"materialization error: {m.Message}"
        | other -> failwithf "expected a materialization error, got %A" other

    [<StepDefinition("I map the status to the enum type \"(.*)\"")>]
    member _.MapInvalid(name: string) =
        try
            Scalar.enum PgEnums.labels |> Scalar.asPgEnum name |> ignore
            w.Set("mappingError", "")
        with :? PlasmConfigurationException as e ->
            w.Set("mappingError", e.Message)

    [<StepDefinition("the mapping is rejected with a configuration error naming \"(.*)\"")>]
    member _.MappingRejected(name: string) =
        let message = w.Get<string> "mappingError"
        check (message.Contains name) $"expected a configuration error naming {name}, got: '{message}'"
