namespace Plasm

open System
open System.Threading.Tasks
open Microsoft.FSharp.Quotations

type internal ConflictDef =
    | IgnoreConflict of target: Expr
    | UpdateOnConflict of target: Expr * change: Expr

type internal BulkDef =
    | UpdateAll of QueryDef * change: Expr
    | DeleteAll of QueryDef
    | InsertAll of Type * rows: obj list * conflict: ConflictDef option

/// A set-based write: one statement over every row a query matches, or many
/// records inserted at once. Like a query, it is a value: building it touches
/// nothing; run it with `repo.Execute` or `Tx.execute`, inspect it with
/// `repo.Explain`.
///
/// Bulk writes do not run changeset validations. Database constraints still
/// apply, and versioned rows have their version incremented.
[<Sealed>]
type BulkWrite<'R> internal (def: BulkDef, finish: obj -> 'R) =
    member internal _.Def = def
    member internal _.Finish(v: obj) = finish v

type Bulk =
    /// Updates every row the query matches: `Bulk.update(q, fun o -> { o with Status = Cancelled })`.
    /// Assigned values may be computed from the current row (`o.Total + 10m`).
    /// Returns the number of rows updated.
    static member update(query: Query<'T>, [<ReflectedDefinition>] change: Expr<'T -> 'T>) : BulkWrite<int> =
        BulkWrite<int>(UpdateAll(query.Def, change), unbox<int>)

    /// Deletes every row the query matches. Returns the number of rows deleted.
    static member delete(query: Query<'T>) : BulkWrite<int> =
        BulkWrite<int>(DeleteAll query.Def, unbox<int>)

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
            | UpdateAll(q, change) -> [ Count(Sql.renderUpdate dialect (ok (Translate.bulkUpdate model vol q change))) ]
            | DeleteAll q -> [ Count(Sql.renderDelete dialect (ok (Translate.bulkDelete model vol q))) ]
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
                let perCommand = max 1 (maxParameters / max 1 cols.Length)

                rows
                |> List.chunkBySize perCommand
                |> List.map (fun chunk ->
                    let values =
                        chunk
                        |> List.map (fun r ->
                            let vs = schema.Shape.Deconstruct r
                            cols |> List.map (fun f -> Sql.fieldValue f vs.[f.Index]))

                    Rows(
                        Sql.renderInsertMany
                            dialect
                            { Table = schema.Table
                              Columns = cols |> List.map (fun f -> f.Column)
                              Rows = values
                              OnConflict = onConflict
                              Returning = returning },
                        schema
                    )))

    /// Runs the commands in a session; the caller provides atomicity when there
    /// is more than one.
    let run (s: Execution.Session) (def: BulkDef) (cmds: Command list) : Task<obj> =
        task {
            match def with
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
