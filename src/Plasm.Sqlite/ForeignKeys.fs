/// Which foreign key a statement broke (constraints.feature). SQLite says only
/// "FOREIGN KEY constraint failed": no table, no columns, no name. So when a
/// statement fails so, it is run again under a savepoint with the check
/// deferred, PRAGMA foreign_key_check before and after finds the row it left
/// without a parent, and the savepoint is rolled back: the database is as the
/// failure left it. The check scans only the tables whose foreign keys the
/// statement can break (an INSERT's or UPDATE's own table, and for an UPDATE
/// or DELETE the tables that refer to it), twice, on that failure only; a
/// database with triggers is not run again.
module internal Plasm.Sqlite.ForeignKeys

open System
open Microsoft.Data.Sqlite

/// SQLITE_CONSTRAINT_FOREIGNKEY.
[<Literal>]
let Code = 787

/// SQLite's foreign key failure, with the table and the columns (in the key's
/// order) of the foreign key it broke.
type Broken(failure: SqliteException, table: string, columns: string list) =
    inherit Data.Common.DbException(failure.Message, failure)
    override _.ErrorCode = failure.ErrorCode
    member _.Failure = failure
    member _.Table = table
    member _.Columns = columns

let private command (failed: SqliteCommand) (sql: string) =
    let c = failed.Connection.CreateCommand()
    c.CommandText <- sql
    c.Transaction <- failed.Transaction
    c

let private run (failed: SqliteCommand) (sql: string) =
    use c = command failed sql
    c.ExecuteNonQuery() |> ignore

/// Called with the command just before it is run again: a seam for the native
/// check (tests/Plasm.Aot.Sqlite), which changes what the re-run meets.
let mutable internal rerunning: SqliteCommand -> unit = ignore

/// The tables whose foreign keys a statement can break: an INSERT its own
/// table's, an UPDATE those and the keys of the tables that refer to it, a
/// DELETE only the latter. None for a statement of another form, whose check
/// is the whole file's.
let private scope (failed: SqliteCommand) : string list option =
    let m =
        Text.RegularExpressions.Regex.Match(
            failed.CommandText,
            "^\\s*(INSERT\\s+INTO|UPDATE|DELETE\\s+FROM)\\s+\"([^\"]+)\"",
            Text.RegularExpressions.RegexOptions.IgnoreCase
        )

    if not m.Success then
        None
    else
        let table = m.Groups.[2].Value
        let verb = m.Groups.[1].Value.ToUpperInvariant()

        let referring () =
            use c =
                command
                    failed
                    "SELECT DISTINCT m.name FROM sqlite_schema AS m JOIN pragma_foreign_key_list(m.name) AS f WHERE m.type = 'table' AND f.\"table\" = $t COLLATE NOCASE"

            c.Parameters.AddWithValue("$t", table) |> ignore
            use r = c.ExecuteReader()
            [ while r.Read() do yield r.GetString 0 ]

        match verb with
        | v when v.StartsWith "INSERT" -> Some [ table ]
        | "UPDATE" -> Some(table :: referring () |> List.distinct)
        | _ -> Some(referring ())

/// The violations pragma_foreign_key_check finds in `tables` (every table's
/// when None): (table, rowid, fkid).
let private violations (failed: SqliteCommand) (tables: string list option) =
    let read (sql: string) (table: string option) =
        use check = command failed sql

        match table with
        | Some t -> check.Parameters.AddWithValue("$t", t) |> ignore
        | None -> ()

        use r = check.ExecuteReader()
        [ while r.Read() do yield r.GetString 0, r.GetInt64 1, r.GetInt64 2 ]

    match tables with
    | None -> set (read "SELECT \"table\", coalesce(rowid, -1), fkid FROM pragma_foreign_key_check" None)
    | Some ts -> set [ for t in ts do yield! read "SELECT \"table\", coalesce(rowid, -1), fkid FROM pragma_foreign_key_check($t)" (Some t) ]

/// The columns of `table`'s foreign key `id`, in the key's order.
let private columnsOf (failed: SqliteCommand) (table: string) (id: int64) =
    use keys = command failed "SELECT \"from\" FROM pragma_foreign_key_list($table) WHERE id = $id ORDER BY seq"
    keys.Parameters.AddWithValue("$table", table) |> ignore
    keys.Parameters.AddWithValue("$id", id) |> ignore
    use k = keys.ExecuteReader()
    [ while k.Read() do yield k.GetString 0 ]

/// The foreign key `failed` broke: the violation it adds when run again,
/// over those the database held before (rows written with foreign keys off,
/// or by another tool). `e` as it was, unnamed, when it is not a foreign key
/// failure; when the database has a trigger, which would fire again; when
/// `cancelled` (nothing more is run once the caller's token is); and when
/// the re-run fails, or adds no violation. The savepoint is rolled back and
/// released and defer_foreign_keys set back whatever happens, so the
/// transaction is as the failure left it.
let diagnose (failed: SqliteCommand) (cancelled: unit -> bool) (e: exn) : exn =
    match e with
    | :? SqliteException as se when se.SqliteExtendedErrorCode = Code && not (isNull failed.Connection) && not (cancelled ()) ->
        try
            let triggers =
                use c = command failed "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger'"
                Convert.ToInt64(c.ExecuteScalar())

            if triggers > 0L then
                e
            else
                let deferred =
                    use c = command failed "PRAGMA defer_foreign_keys"
                    Convert.ToInt64(c.ExecuteScalar())

                run failed "SAVEPOINT plasm_foreign_key"

                let found =
                    try
                        try
                            run failed "PRAGMA defer_foreign_keys = ON"
                            let tables = scope failed
                            let before = violations failed tables
                            rerunning failed
                            failed.ExecuteNonQuery() |> ignore

                            Set.difference (violations failed tables) before
                            |> Seq.tryHead
                            |> Option.bind (fun (table, _, id) ->
                                match columnsOf failed table id with
                                | [] -> None
                                | columns -> Some(table, columns))
                        with _ ->
                            None
                    finally
                        run failed "ROLLBACK TO plasm_foreign_key"
                        run failed "RELEASE plasm_foreign_key"
                        run failed $"PRAGMA defer_foreign_keys = {deferred}"

                match found with
                | Some(table, columns) -> Broken(se, table, columns) :> exn
                | None -> e
        with _ ->
            e
    | _ -> e
