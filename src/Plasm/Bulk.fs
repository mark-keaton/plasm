namespace Plasm

open System
open System.Threading.Tasks
open Microsoft.FSharp.Quotations

type internal ConflictDef =
    | IgnoreConflict of target: Expr
    | UpdateOnConflict of target: Expr * change: Expr

type internal BulkDef =
    /// `returning`: the rows written, not their count (bulk-writes.feature).
    | UpdateAll of QueryDef * change: Expr * returning: bool
    | DeleteAll of QueryDef * returning: bool
    | InsertAll of Type * rows: obj list * conflict: ConflictDef option

/// A bulk write of any result, for the start-up check (statement-check.feature).
type internal IBulkWrite =
    abstract Def: BulkDef

/// A set-based write: one statement over every row a query matches, or many
/// records inserted at once. Like a query, it is a value: building it touches
/// nothing; run it with `Tx.execute`, inspect it with `repo.Explain`.
///
/// Database constraints apply to bulk writes as to any write, and versioned
/// rows have their version incremented.
[<Sealed>]
type BulkWrite<'R> internal (def: BulkDef, finish: obj -> 'R) =
    member internal _.Def = def

    interface IBulkWrite with
        member _.Def = def
    member internal _.Finish(v: obj) = finish v

type Bulk =
    /// Updates every row the query matches: `Bulk.update(q, fun o -> { o with Status = Cancelled })`.
    /// Assigned values may be computed from the current row (`o.Total + 10m`).
    /// Returns the number of rows updated.
    static member update(query: Query<'T>, [<ReflectedDefinition>] change: Expr<'T -> 'T>) : BulkWrite<int> =
        BulkWrite<int>(UpdateAll(query.Def, change, false), unbox<int>)

    /// Deletes every row the query matches. Returns the number of rows deleted.
    static member delete(query: Query<'T>) : BulkWrite<int> =
        BulkWrite<int>(DeleteAll(query.Def, false), unbox<int>)

    /// As Bulk.update, returning the rows it changed as they are after it,
    /// each read whole with what it owns and links (bulk-writes.feature).
    static member updateReturning(query: Query<'T>, [<ReflectedDefinition>] change: Expr<'T -> 'T>) : BulkWrite<'T list> =
        BulkWrite<'T list>(UpdateAll(query.Def, change, true), (fun v -> v :?> obj list |> List.map unbox<'T>))

    /// As Bulk.delete, returning the rows it deleted, as they were. Only for
    /// records that own or link nothing: what a deleted row owned cannot be
    /// read back (bulk-writes.feature).
    static member deleteReturning(query: Query<'T>) : BulkWrite<'T list> =
        BulkWrite<'T list>(DeleteAll(query.Def, true), (fun v -> v :?> obj list |> List.map unbox<'T>))

    /// Inserts records with as few commands as possible; returns them with
    /// their generated values, in order.
    static member insert(records: 'T list) : BulkWrite<'T list> =
        BulkWrite<'T list>(InsertAll(typeof<'T>, List.map box records, None), (fun v -> v :?> obj list |> List.map unbox<'T>))

    /// Inserts records, skipping those that conflict on a unique field (or a
    /// tuple of fields). Returns only the records inserted.
    static member insertMissing(records: 'T list, [<ReflectedDefinition>] on: Expr<'T -> 'K>) : BulkWrite<'T list> =
        BulkWrite<'T list>(
            InsertAll(typeof<'T>, List.map box records, Some(IgnoreConflict on)),
            (fun v -> v :?> obj list |> List.map unbox<'T>)
        )

    /// Inserts records; where one conflicts on a unique field, updates the
    /// stored row instead: `fun current incoming -> { current with Name = incoming.Name }`.
    /// Returns every inserted or updated record.
    static member upsert
        (records: 'T list, [<ReflectedDefinition>] on: Expr<'T -> 'K>, [<ReflectedDefinition>] update: Expr<'T -> 'T -> 'T>)
        : BulkWrite<'T list> =
        BulkWrite<'T list>(
            InsertAll(typeof<'T>, List.map box records, Some(UpdateOnConflict(on, update))),
            (fun v -> v :?> obj list |> List.map unbox<'T>)
        )

module internal BulkExec =

    /// PostgreSQL accepts at most 65,535 parameters in one command.
    let private maxParameters = 65000

    type Command =
        | Rows of Sql.Command * SchemaInfo
        | Count of Sql.Command

    let private raiseT e = raise (Translate.TranslationFailure e)

    let private ok r =
        match r with
        | Ok v -> v
        | Error e -> raiseT e

    /// The commands a bulk write sends, in order.
    let commands (model: Model) (dialect: ISqlDialect) (def: BulkDef) : Result<Command list, TranslationError> =
        Translate.wrap (fun () ->
            let vol = ref false

            match def with
            | UpdateAll(q, change, false) -> [ Count(Sql.renderUpdate dialect (ok (Translate.bulkUpdate model vol q change))) ]
            | DeleteAll(q, false) -> [ Count(Sql.renderDelete dialect (ok (Translate.bulkDelete model vol q))) ]
            | UpdateAll(q, _, true)
            | DeleteAll(q, true) ->
                if not dialect.Capabilities.Returning then
                    raiseT (TranslationError.UnsupportedByProvider("RETURNING (reading back the rows a bulk write wrote)", dialect.Name))

                let schema =
                    match model.Schema q.Root with
                    | Ok s -> s
                    | Error e -> raiseT e

                let returning = Sql.returningFields schema.Table schema.Fields

                match def with
                | UpdateAll(_, change, _) ->
                    let update = ok (Translate.bulkUpdate model vol q change)
                    [ Rows(Sql.renderUpdate dialect { update with Returning = returning }, schema) ]
                | _ ->
                    if schema.Assocs.Length > 0 then
                        let names = schema.Assocs |> Array.map (fun a -> a.Name) |> String.concat ", "

                        raiseT (
                            TranslationError.InvalidQuery
                                $"a bulk delete returns only records that own or link nothing, and {schema.Name} has {names}, which a delete cannot read back; delete it with Bulk.delete, or read the rows before deleting them"
                        )

                    let delete = ok (Translate.bulkDelete model vol q)
                    [ Rows(Sql.renderDelete dialect { delete with Returning = returning }, schema) ]
            | InsertAll(t, rows, conflict) ->
                let caps = dialect.Capabilities

                if conflict.IsSome && not caps.Upserts then
                    raiseT (TranslationError.UnsupportedByProvider("upserts (INSERT ... ON CONFLICT)", dialect.Name))

                if not caps.Returning then
                    raiseT (TranslationError.UnsupportedByProvider("RETURNING (reading back inserted rows)", dialect.Name))

                let schema =
                    match model.Schema t with
                    | Ok s -> s
                    | Error e -> raiseT e

                let cols = schema.Fields |> Array.filter (fun f -> not f.Generated) |> Array.toList

                let onConflict =
                    conflict
                    |> Option.map (fun c ->
                        match c with
                        | IgnoreConflict on -> Sql.DoNothing(ok (Translate.conflictTarget model t on))
                        | UpdateOnConflict(on, change) ->
                            Sql.DoUpdate(ok (Translate.conflictTarget model t on), ok (Translate.conflictUpdate model vol t change)))

                let returning = Sql.returningFields schema.Table schema.Fields
                let perCommand = max 1 (maxParameters / max 1 (cols |> List.sumBy (fun f -> f.Parts.Length)))

                rows
                |> List.chunkBySize perCommand
                |> List.map (fun chunk ->
                    let values =
                        chunk
                        |> List.map (fun r ->
                            let vs = schema.Shape.Deconstruct r
                            cols |> List.collect (fun f -> Sql.fieldColumns f vs.[f.Index] |> List.map snd))

                    Rows(
                        Sql.renderInsertMany
                            dialect
                            { Table = schema.Table
                              Columns = cols |> List.collect (fun f -> f.Parts |> List.map (fun p -> p.Name))
                              Rows = values
                              OnConflict = onConflict
                              Returning = returning },
                        schema
                    )))

    /// Runs the commands in a session; the caller provides atomicity when there
    /// is more than one.
    let run (s: Execution.Session) (model: Model) (def: BulkDef) (cmds: Command list) : Task<obj> =
        task {
            match def with
            | UpdateAll(_, _, true)
            | DeleteAll(_, true) ->
                // The rows written, then (an update's) what they own and link,
                // read in the same transaction.
                let all = ResizeArray<obj>()

                for c in cmds do
                    match c with
                    | Rows(cmd, schema) ->
                        let! rows = Execution.query s cmd (Execution.readEntity schema)
                        let nodes = Planner.planLoads s.Provider.Dialect model schema

                        match nodes with
                        | Ok nodes when not nodes.IsEmpty -> do! Execution.Round.run s (Execution.loads s rows nodes)
                        | Ok _ -> ()
                        | Error e -> raiseT e

                        for r in rows do
                            all.Add(schema.Shape.Construct r)
                    | Count cmd ->
                        let! _ = Execution.execute s cmd
                        ()

                return box (List.ofSeq all)
            | InsertAll _ ->
                let all = ResizeArray<obj>()

                for c in cmds do
                    match c with
                    | Rows(cmd, schema) ->
                        let! rows = Execution.query s cmd (Execution.readEntity schema)

                        for r in rows do
                            all.Add(schema.Shape.Construct r)
                    | Count cmd ->
                        let! _ = Execution.execute s cmd
                        ()

                return box (List.ofSeq all)
            | _ ->
                let mutable n = 0

                for c in cmds do
                    match c with
                    | Count cmd ->
                        let! k = Execution.execute s cmd
                        n <- n + k
                    | Rows(cmd, _) ->
                        let! _ = Execution.execute s cmd
                        ()

                return box n
        }

    let sqlOf (cmds: Command list) =
        cmds
        |> List.map (function
            | Rows(c, _)
            | Count c -> c.Sql)
