/// Write execution for changesets.
module internal Plasm.Writes

open System
open System.Collections.Generic
open System.Threading.Tasks
open Plasm.Execution

type Failure =
    | Invalid of CsState
    | Stale of CsState
    | Failed of DbFailure
    /// The provider cannot perform this write; names what it lacks.
    | Unsupported of operation: string

let private schemaOf (model: Model) (t: Type) =
    match model.Schema t with
    | Ok s -> s
    | Error e -> raise (Translate.TranslationFailure e)

let private keyValue (schema: SchemaInfo) (s: CsState) : obj option =
    match s.Key with
    | Some(_, k) -> Some k
    | None -> s.Data |> Option.map (fun d -> (schema.Shape.Deconstruct d).[schema.Key.Index])

/// The key of the record a stale write was made from: its key field's value.
/// A write only checks a row it has the key of, so a stale one has a key.
let keyOf (model: Model) (s: CsState) : obj =
    match keyValue (schemaOf model s.Type) s with
    | Some key -> key
    | None -> invalidOp $"a stale {s.Type.Name} has no key"

let private versionValue (schema: SchemaInfo) (s: CsState) : int option =
    match schema.Version with
    | None -> None
    | Some v ->
        s.Data |> Option.map (fun d -> unbox<int> (schema.Shape.Deconstruct d).[v.Index])

/// For a record without a version field, the update or delete applies only if
/// every column still holds what the change was made from (concurrency.feature).
/// A change made from a key alone has nothing to compare. A column `scope`
/// already compares with the same value (an owned record's owner) is compared
/// once. Each comparison is the dialect's null-safe operator, written as the
/// binary operation it is, bracketed in the WHERE as one.
let private originalWhere (dialect: ISqlDialect) (schema: SchemaInfo) (scope: Sql.Expr list) (s: CsState) =
    match schema.Version, s.Data with
    | None, Some d ->
        let values = schema.Shape.Deconstruct d

        schema.Fields
        |> Array.filter (fun f -> f.Index <> schema.Key.Index)
        |> Array.toList
        |> List.collect (fun f ->
            Sql.fieldColumns f values.[f.Index]
            |> List.filter (fun (column, value) -> not (List.contains (Sql.Binary("=", Sql.Column(schema.Table, column), value)) scope))
            |> List.map (fun (column, value) -> Sql.Binary(dialect.NullSafeComparison false, Sql.Column(schema.Table, column), value)))
    | _ -> []

/// Owned lists (owned-collections.feature) as child writes. An insert writes
/// every item; a change made from a record read compares the old list with the
/// new by key (inserting new items, updating changed ones, deleting missing
/// ones); a delete deletes what the owner owns first.
/// A generated key's value before the database assigns one (0, an empty
/// Guid, or null).
let private unsavedKey (schema: SchemaInfo) (key: obj) =
    schema.Key.ToColumns key
    |> List.forall (fun v ->
        isNull v
        || v = box DBNull.Value
        || (v.GetType().IsValueType && v.Equals(Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(v.GetType()))))

let rec private owned (model: Model) (s: CsState) : CsState =
    let schema = schemaOf model s.Type
    let declared = s.Children |> List.map (fun w -> w.Field) |> set

    // An owned record is written as a list of none or one.
    let items (a: AssocMap) (list: obj) =
        match a.Ctors.Read list with
        | Some null -> []
        | Some l when a.Cardinality = OptionalOne -> [ l.GetType().GetProperty("Value").GetValue l ]
        | Some l when a.Cardinality = RequiredOne -> [ l ]
        | Some l -> Reflection.listItems a.Target l |> List.ofSeq
        | None -> []

    let state t action (v: obj) = Cs.emptyState t (Some v) action

    // Linked lists: only join-table rows are written (linked-collections.feature).
    let linksDeclared = s.Links |> List.map (fun l -> l.Field) |> set

    let s =
        schema.Assocs
        |> Array.filter (fun a -> a.Linked && not (linksDeclared.Contains a.Name))
        |> Array.fold
            (fun (st: CsState) a ->
                let target = schemaOf model a.Target
                let keyOf (c: obj) = (target.Shape.Deconstruct c).[target.Key.Index]
                let put targets = { st with Links = st.Links @ [ { Field = a.Name; Mode = PutLinks; Targets = targets } ] }

                match st.Action with
                | ChangeAction.Insert ->
                    match st.Proposed with
                    | Some p ->
                        match items a (schema.Shape.Deconstruct p).[a.Index] with
                        | [] -> st
                        | targets -> put targets
                    | None -> st
                | ChangeAction.Update ->
                    match st.Changes.TryFind a.Name, st.Data with
                    | None, _ -> st
                    | Some _, None -> Cs.addError st a.Name (FieldErrorKind.Custom "linked") $"{a.Name} is linked: change it from a {schema.Name} you read, not from its key alone"
                    | Some newList, Some old ->
                        let before = Collections.Generic.Dictionary<obj, obj>(HashIdentity.Structural)

                        for c in items a (schema.Shape.Deconstruct old).[a.Index] do
                            before.[keyOf c] <- c

                        let after = items a newList

                        let changed =
                            after
                            |> List.tryFind (fun c ->
                                match before.TryGetValue(keyOf c) with
                                | true, o -> not (Cs.fieldEquals o c)
                                | _ -> false)

                        match changed with
                        | Some _ ->
                            Cs.addError
                                st
                                a.Name
                                (FieldErrorKind.Custom "linked")
                                $"a linked {target.Name} belongs to its own aggregate: change it there, not through {schema.Name}.{a.Name}"
                        | None -> { put after with Changes = st.Changes.Remove a.Name }
                | ChangeAction.Delete -> put [])
            s

    schema.Assocs
    |> Array.filter (fun a -> a.Owned && not (declared.Contains a.Name))
    |> Array.fold
        (fun (st: CsState) a ->
            let target = schemaOf model a.Target
            let keyOf (c: obj) = (target.Shape.Deconstruct c).[target.Key.Index]
            let child children final = { Field = a.Name; Replace = None; Children = children; Final = Some final }

            match st.Action with
            | ChangeAction.Insert ->
                match st.Proposed with
                | Some p ->
                    let after = items a (schema.Shape.Deconstruct p).[a.Index]
                    { st with Children = st.Children @ [ child (after |> List.map (state a.Target ChangeAction.Insert)) (after |> List.map (fun c -> c, true)) ] }
                | None -> st
            | ChangeAction.Update ->
                match st.Changes.TryFind a.Name, st.Data with
                | None, _ -> st
                | Some _, None -> Cs.addError st a.Name (FieldErrorKind.Custom "owned") $"{a.Name} is owned: change it from a {schema.Name} you read, not from its key alone"
                | Some newList, Some old when
                    (let after = items a newList |> List.map keyOf |> List.filter (fun k -> not (target.KeyGenerated && unsavedKey target k))
                     after.Length <> (List.distinct after).Length)
                    ->
                    // One stored record listed twice: which copy to write would
                    // be decided by the order of the list.
                    Cs.addError st a.Name FieldErrorKind.DuplicateIdentity $"{a.Name} lists the same {target.Name} more than once"
                | Some newList, Some old ->
                    let before = items a (schema.Shape.Deconstruct old).[a.Index]
                    let after = items a newList
                    let byKey = Collections.Generic.Dictionary<obj, obj>(HashIdentity.Structural)

                    for c in before do
                        byKey.[keyOf c] <- c

                    let shape = target.Shape

                    // Each item of the new list: kept as it is, updated, or inserted.
                    let plan =
                        after
                        |> List.map (fun c ->
                            match byKey.TryGetValue(keyOf c) with
                            | true, o when Cs.fieldEquals o c -> c, None
                            | true, o ->
                                let was, now = shape.Deconstruct o, shape.Deconstruct c

                                let changes =
                                    shape.Fields
                                    |> Array.indexed
                                    |> Array.filter (fun (i, _) -> i <> target.Key.Index && not (Cs.fieldEquals was.[i] now.[i]))
                                    |> Array.fold (fun (m: Map<string, obj>) (i, p) -> m.Add(p.Name, now.[i])) Map.empty

                                c, Some { state a.Target ChangeAction.Update o with Changes = changes }
                            // A generated key the owner did not have: the record is
                            // another owner's (or not stored). Written as an update,
                            // the ownership check refuses it instead of inserting a copy.
                            | _ when target.KeyGenerated && not (unsavedKey target (keyOf c)) -> c, Some(state a.Target ChangeAction.Update c)
                            | _ -> c, Some(state a.Target ChangeAction.Insert c))

                    let kept = Collections.Generic.HashSet<obj>(after |> List.map keyOf, HashIdentity.Structural)
                    let removed = before |> List.filter (fun o -> not (kept.Contains(keyOf o))) |> List.map (state a.Target ChangeAction.Delete)

                    { st with
                        Changes = st.Changes.Remove a.Name
                        Children = st.Children @ [ child ((plan |> List.choose snd) @ removed) (plan |> List.map (fun (c, w) -> c, w.IsSome)) ] }
            | ChangeAction.Delete ->
                let ownsMore = target.Assocs |> Array.exists (fun t -> t.Owned)

                match st.Data with
                | Some d when ownsMore ->
                    // Each item deletes what it owns in turn.
                    let doomed = items a (schema.Shape.Deconstruct d).[a.Index] |> List.map (state a.Target ChangeAction.Delete)
                    { st with Children = st.Children @ [ child doomed [] ] }
                | None when ownsMore ->
                    Cs.addError st a.Name (FieldErrorKind.Custom "owned") $"{a.Name} owns lists of its own: delete the {schema.Name} you read, not its key alone"
                | _ ->
                    // Every item the owner has, whatever the record held.
                    { st with Children = st.Children @ [ { Field = a.Name; Replace = Some AllChildren; Children = []; Final = Some [] } ] })
        s

/// Model-aware structural validation, run before any command is sent.
let rec check (model: Model) (s: CsState) : CsState =
    let s = owned model s
    let schema = schemaOf model s.Type
    let err (st: CsState) field kind msg = Cs.addError st field kind msg
    let mutable st = s

    match s.Action with
    | ChangeAction.Insert when s.Data.IsNone -> st <- err st schema.Key.Name (FieldErrorKind.Custom "insert") "an insert needs a complete value"
    | ChangeAction.Update
    | ChangeAction.Delete ->
        if s.Changes.ContainsKey schema.Key.Name then
            st <- err st schema.Key.Name FieldErrorKind.KeyChange "the primary key cannot be changed"

        match schema.Version with
        | Some v when s.Changes.ContainsKey v.Name ->
            st <- err st v.Name (FieldErrorKind.Custom "version") "the version is managed by optimistic concurrency and cannot be set"
        | Some v when s.Data.IsNone ->
            st <-
                err
                    st
                    v.Name
                    (FieldErrorKind.Custom "version")
                    "a change to a versioned record is made from the record read, whose version it checks: read a caller's copy with Tx.oneAsSeen"
        | _ -> ()
    | ChangeAction.Insert -> ()

    let children =
        s.Children
        |> List.map (fun w ->
            match schema.TryAssoc w.Field with
            | Some a when a.Kind = AssocKind.HasMany || (a.Owned && a.Kind = AssocKind.HasOne) ->
                let target = schemaOf model a.Target
                let checkedChildren = w.Children |> List.map (check model)

                let identities =
                    checkedChildren
                    |> List.filter (fun c -> c.Action <> ChangeAction.Insert)
                    |> List.choose (keyValue target)

                let duplicates =
                    identities |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

                for d in duplicates do
                    st <-
                        err
                            st
                            w.Field
                            FieldErrorKind.DuplicateIdentity
                            $"more than one change for {target.Name} {d}; combine them into one changeset"

                for c in checkedChildren do
                    if c.Type <> target.Type then
                        st <- err st w.Field (FieldErrorKind.Custom "type") $"child changeset is for {c.Type.Name}, expected {target.Name}"

                { w with Children = checkedChildren }
            | _ ->
                st <- err st w.Field (FieldErrorKind.Custom "relationship") "not a declared relationship"
                w)

    for l in s.Links do
        match schema.TryAssoc l.Field with
        | Some a when a.Kind = AssocKind.ManyToMany ->
            let target = schemaOf model a.Target

            if l.Targets |> List.exists (fun t -> isNull t || t.GetType() <> target.Type) then
                st <- err st l.Field (FieldErrorKind.Custom "type") $"links of {schema.Name}.{a.Name} must be {target.Name} records"
        | _ -> st <- err st l.Field (FieldErrorKind.Custom "relationship") "not a linked list"

    { st with Children = children }

let rec isValid (s: CsState) =
    s.Errors.IsEmpty && s.Children |> List.forall (fun w -> w.Children |> List.forall isValid)

/// The field a violated constraint belongs to, by PostgreSQL's default
/// names: <table>_<column>_key (unique), _fkey (reference) and _check (a
/// column check). Any other name stays a database failure (constraints.feature).
let private conventional (schema: SchemaInfo) (name: string) =
    let table = schema.Table.Split('.') |> Array.last

    // A linked list's join table: a link to a record that does not exist.
    let linked =
        schema.Assocs
        |> Array.tryPick (fun a ->
            match a.Through with
            | Some j when a.Linked && name = $"{j.Table}_{j.TargetColumn}_fkey" -> Some(a.Name, FieldErrorKind.ForeignKeyConstraint name, "does not exist")
            | _ -> None)

    if linked.IsSome then linked else

    schema.Fields
    |> Array.tryPick (fun f ->
        // A field of several columns (a reference to a key of several parts):
        // PostgreSQL names its constraint by every column.
        let columns = if f.Composite then f.Parts |> List.map (fun p -> p.Name) |> String.concat "_" else f.Column
        let stem = $"{table}_{columns}"

        // A union's case field or a value's field that refers to a record is
        // the field's (union-references.feature).
        let caseReference =
            f.Name <> schema.Key.Name
            && f.NestedReferences |> List.exists (fun (_, l) -> name = $"{table}_{l.Column}_fkey")

        // A field unique within others is named by all its columns
        // (unique-within.feature).
        let uniqueName = $"""{table}_{String.concat "_" f.UniqueColumns}_key"""

        if f.Unique && name = uniqueName then Some(f.Name, FieldErrorKind.UniqueConstraint name, "has already been taken")
        elif name = stem + "_key" then Some(f.Name, FieldErrorKind.UniqueConstraint name, "has already been taken")
        elif name = stem + "_fkey" || caseReference then Some(f.Name, FieldErrorKind.ForeignKeyConstraint name, "does not exist")
        elif name = stem + "_check" then Some(f.Name, FieldErrorKind.CheckConstraint name, "is invalid")
        else None)

let private constraintError (schema: SchemaInfo) (s: CsState) (f: DbFailure) : CsState option =
    match f.Constraint with
    | Some name ->
        conventional schema name |> Option.map (fun (field, kind, message) -> Cs.addError s field kind message)
    | None -> None

/// Why a write's statement failed; or that it was not sent, because one
/// before it failed (round-trips.feature).
type private Problem =
    | Fail of Failure
    | NotSent

type private Written<'a> = Result<'a, Problem>

/// The database failure an exception is; cancellation and a row that could
/// not be read are raised as they are.
let private dbFailure (sess: Session) (ex: exn) : DbFailure =
    match ex with
    | :? OperationCanceledException
    | MaterializationFailure _ -> Err.rethrow ex
    | _ -> classifyOrRaise sess ex

/// A failed statement of the write of `s`: a violated constraint of one of its
/// fields is that field's error; any other is a database failure.
let private failedWrite (schema: SchemaInfo) (s: CsState) (f: DbFailure) : Problem =
    match constraintError schema s f with
    | Some withError -> Fail(Invalid withError)
    | None -> Fail(Failed f)

let private replied (sess: Session) (fail: DbFailure -> Problem) (reply: Reply<'a>) : Written<'a> =
    match reply with
    | Ok v -> Ok v
    | Error None -> Error NotSent
    | Error(Some ex) -> Error(fail (dbFailure sess ex))

let private asFailed (f: DbFailure) = Fail(Failed f)

/// The values, or the first failure in the order the statements were sent;
/// a statement not sent only stands for a failure when nothing else does.
let private together (results: Written<'a> list) : Written<'a list> =
    match results |> List.tryPick (function | Error(Fail f) -> Some f | _ -> None) with
    | Some f -> Error(Fail f)
    | None when results |> List.exists Result.isError -> Error NotSent
    | None -> Ok(results |> List.choose Result.toOption)

let private pair (a: Written<'a>) (b: Written<'b>) : Written<'a * 'b> =
    match a, b with
    | Error(Fail f), _
    | _, Error(Fail f) -> Error(Fail f)
    | Error NotSent, _
    | _, Error NotSent -> Error NotSent
    | Ok x, Ok y -> Ok(x, y)

let private returning (schema: SchemaInfo) = Sql.returningFields schema.Table schema.Fields

/// The row of `key`: its one column, or every part of a composite key
/// (composite-keys.feature).
let private keyFilter (schema: SchemaInfo) (key: obj) =
    if schema.Key.Composite then
        List.zip schema.Key.Parts (schema.Key.ToColumns key)
        |> List.map (fun (p, v) -> Sql.Binary("=", Sql.Column(schema.Table, p.Name), Sql.partValue p v))
        |> List.reduce (fun a b -> Sql.Binary("AND", a, b))
    else
        Sql.Binary("=", Sql.Column(schema.Table, schema.Key.Column), Sql.fieldScalar schema.Key key)

let private setField (schema: SchemaInfo) (s: CsState) (name: string) (domainValue: obj) =
    let f = schema.Field name

    let v = f.WrapSome domainValue

    { s with Changes = s.Changes.Add(name, v) }

/// Completes a returned row: relationship fields come from the original value
/// where known, then from what this write produced.
let private finish (schema: SchemaInfo) (s: CsState) (row: obj[]) (written: Map<string, obj>) =
    match s.Data with
    | Some d ->
        let original = schema.Shape.Deconstruct d

        for a in schema.Assocs do
            row.[a.Index] <- original.[a.Index]
    | None -> ()

    for KeyValue(name, value) in written do
        let a = (schema.TryAssoc name).Value
        row.[a.Index] <- value

    schema.Shape.Construct row

/// Every capability this write (with its children) needs that the dialect
/// lacks, as one message; None when it has them all.
let unsupported (c: ProviderCapabilities) (s: CsState) : string option =
    let rec needs (s: CsState) =
        [ if (s.Action = ChangeAction.Insert || s.Action = ChangeAction.Update) && not c.Returning then
              "RETURNING (reading back written rows)"
          if s.Children |> List.exists (fun w -> w.Replace.IsSome) && not c.RowLocking && not c.WritersTakeTurns then
              "row locking (SELECT ... FOR UPDATE, to replace children)"
          yield! s.Children |> List.collect (fun w -> w.Children) |> List.collect needs ]

    match needs s |> List.distinct with
    | [] -> None
    | missing -> Some(String.concat "; " missing)

/// The statements of a write of `s` (with what it owns and links), as round
/// trips: its own statement with what it owns, after the key that needs
/// (round-trips.feature).
let rec private write (model: Model) (sess: Session) (s: CsState) : Round<Written<obj>> = writeWithin model sess [] s

/// A write whose update or delete also requires `scope`: an owned child's
/// owner, so that one command both checks and writes it.
and private writeWithin (model: Model) (sess: Session) (scope: Sql.Expr list) (s: CsState) : Round<Written<obj>> =
    match unsupported sess.Provider.Dialect.Capabilities s with
    | Some op -> Done(Error(Fail(Unsupported op)))
    | None ->
        let schema = schemaOf model s.Type

        match s.Action with
        | ChangeAction.Insert -> insert model sess schema s
        | ChangeAction.Update -> update model sess schema scope s
        | ChangeAction.Delete -> delete model sess schema scope s

/// Inserts a record, then what it owns: with it when its key is the
/// application's, after it when the database generates the key.
and private insert model (sess: Session) (schema: SchemaInfo) (s: CsState) : Round<Written<obj>> =
    let values = schema.Shape.Deconstruct s.Proposed.Value

    let columns =
        schema.Fields
        |> Array.filter (fun f -> not f.Generated)
        |> Array.toList
        |> List.collect (fun f -> Sql.fieldColumns f values.[f.Index])

    let cmd =
        Sql.renderInsert
            sess.Provider.Dialect
            { Table = schema.Table
              Columns = columns |> List.map fst
              Values = columns |> List.map snd
              Returning = returning schema }

    let inserted =
        Round.fetch sess cmd (readEntity schema)
        |> Round.map (replied sess (failedWrite schema s) >> Result.map (fun rows -> rows.[0]))

    // Keep relationship values supplied on the inserted record, then what
    // its own writes produced.
    let complete (row: obj[]) (written: Map<string, obj>) =
        for a in schema.Assocs do
            row.[a.Index] <- values.[a.Index]

        for KeyValue(name, value) in written do
            row.[(schema.TryAssoc name).Value.Index] <- value

        schema.Shape.Construct row

    if schema.KeyGenerated then
        inserted
        |> Round.bind (function
            | Error p -> Done(Error p)
            | Ok row -> writeChildren model sess schema s row.[schema.Key.Index] true |> Round.map (Result.map (complete row)))
    else
        Round.both inserted (writeChildren model sess schema s values.[schema.Key.Index] true)
        |> Round.map (fun (row, written) -> pair row written |> Result.map (fun (row, written) -> complete row written))

and private update model (sess: Session) (schema: SchemaInfo) (scope: Sql.Expr list) (s: CsState) : Round<Written<obj>> =
    let key = (keyValue schema s).Value
    let version = versionValue schema s

    let sets =
        s.Changes
        |> Map.toList
        |> List.collect (fun (name, v) -> Sql.fieldColumns (schema.Field name) v)

    let versionSet, versionWhere =
        match schema.Version, version with
        | Some vf, Some v ->
            [ vf.Column, Sql.Binary("+", Sql.Column(schema.Table, vf.Column), Sql.Literal "1") ],
            [ Sql.Binary("=", Sql.Column(schema.Table, vf.Column), Sql.Param(box v)) ]
        | _ -> [], []

    let where = keyFilter schema key :: scope @ versionWhere @ originalWhere sess.Provider.Dialect schema scope s
    let read cmd = Round.fetch sess cmd (readEntity schema) |> Round.map (replied sess (failedWrite schema s) >> Result.map Seq.tryHead)

    let row: Round<Written<obj[] option>> =
        // Nothing to write on the row itself (relationship-only changes without
        // a version to bump). Re-read only when the full record is unknown.
        if sets.IsEmpty && (versionSet.IsEmpty || (s.Children.IsEmpty && s.Links.IsEmpty)) then
            match s.Data with
            | Some d when scope.IsEmpty -> Done(Ok(Some(schema.Shape.Deconstruct d)))
            | _ ->
                read (
                    Sql.renderSelect
                        sess.Provider.Dialect
                        { Sql.select (Sql.Table(schema.Table, schema.Table)) with
                            Columns = Translate.entityColumns schema.Table schema
                            Where = where }
                )
        else
            // A changed parent bumps its version; child-only writes still take
            // the parent's version so concurrent writers conflict.
            read (
                Sql.renderUpdate
                    sess.Provider.Dialect
                    { Table = schema.Table
                      Set =
                        (if sets.IsEmpty && versionSet.IsEmpty then
                             [ schema.Key.Parts.Head.Name, Sql.Column(schema.Table, schema.Key.Parts.Head.Name) ]
                         else
                             sets @ versionSet)
                      Where = where
                      Returning = returning schema }
            )

    // What it owns goes with it: its key is known.
    Round.both row (writeChildren model sess schema s key false)
    |> Round.map (fun (row, written) ->
        match row with
        | Error p -> Error p
        | Ok None -> Error(Fail(Stale s))
        | Ok(Some row) -> written |> Result.map (finish schema s (Array.copy row)))

and private delete model (sess: Session) (schema: SchemaInfo) (scope: Sql.Expr list) (s: CsState) : Round<Written<obj>> =
    let key = (keyValue schema s).Value

    let versionWhere =
        match schema.Version, versionValue schema s with
        | Some vf, Some v -> [ Sql.Binary("=", Sql.Column(schema.Table, vf.Column), Sql.Param(box v)) ]
        | _ -> []

    let cmd =
        Sql.renderDelete
            sess.Provider.Dialect
            { Table = schema.Table
              Where = keyFilter schema key :: scope @ versionWhere @ originalWhere sess.Provider.Dialect schema scope s
              Returning = [] }

    // What the record owns goes first (owned-collections.feature).
    let owned =
        if s.Children.IsEmpty && s.Links.IsEmpty then Done(Ok Map.empty) else writeChildren model sess schema s key false

    let own =
        Round.exec sess cmd
        |> Round.map (
            replied sess (failedWrite schema s)
            >> Result.bind (fun n -> if n = 0 then Error(Fail(Stale s)) else Ok(s.Data |> Option.toObj))
        )

    Round.andThen owned own |> Round.map (fun (owned, own) -> pair owned own |> Result.map snd)

/// Writes child changesets for each relationship write. Returns relationship
/// values to place on the parent where the written set is complete.
and private writeChildren model sess (schema: SchemaInfo) (s: CsState) (parentKey: obj) (parentInserted: bool) =
    let children = writeChildList model sess schema s parentKey parentInserted

    if s.Links.IsEmpty then
        children
    else
        Round.both children (writeLinks model sess schema s parentKey)
        |> Round.map (fun (children, links) -> pair children links |> Result.map (fun (c, l) -> Map.fold (fun m k v -> Map.add k v m) c l))

/// Join-table writes for many-to-many link changes. Returns the relationship
/// value for the parent: the new set of links.
and private writeLinks model (sess: Session) (schema: SchemaInfo) (s: CsState) (parentKey: obj) : Round<Written<Map<string, obj>>> =
    let d = sess.Provider.Dialect
    let q = d.QuoteIdentifier

    s.Links
    |> List.map (fun l ->
        let a = (schema.TryAssoc l.Field).Value
        let j = a.Through.Value
        let target = schemaOf model a.Target

        let keys =
            l.Targets
            |> List.map (fun t -> target.Shape.Deconstruct t |> fun vs -> vs.[target.Key.Index])
            |> List.distinct

        let p i = d.ParameterName i
        let jt = q j.Table
        let run (sql: string) (ps: obj list) = Round.exec sess { Sql = sql; Parameters = ps }

        // Each side's columns: one, or one per part of a key of several parts
        // (composite-links.feature). The owner is bound once per column.
        let owners = Joins.owners j schema.Key |> List.map (fst >> q)
        let targets = Joins.targets j target.Key
        let ownerValues = if schema.Key.Composite then schema.Key.ToColumns parentKey else [ schema.Key.ScalarToDb parentKey ]
        let n = owners.Length
        let ownedBy (table: string) = owners |> List.mapi (fun i c -> table + c + " = " + p i) |> String.concat " AND "
        let ownerColumns = String.concat ", " owners
        let ownerParameters = List.init n p |> String.concat ", "

        // Written in the hand of the other statements: quoted names, the
        // owner bound once (readable-sql.feature). The lists are sent and
        // read as the dialect sends a list.
        let u = q "u"

        let render (f: (string -> unit) -> unit) =
            let sb = Text.StringBuilder()
            f (fun x -> sb.Append x |> ignore)
            sb.ToString()

        let removeSql, insertSql, targetParameters =
            let owned = ownedBy ""
            let ownedInJoin = ownedBy (jt + ".")

            if not target.Key.Composite then
                let tc, k = q (fst targets.Head), q "k"

                let missing = render (fun w -> d.NotInList(w, (fun () -> w tc), p n, None))
                let rows = render (fun w -> d.ListRows(w, [ p n, None ], Some("u", [ "k" ])))

                $"DELETE FROM {jt}\nWHERE {owned} AND {missing}",
                $"INSERT INTO {jt} ({ownerColumns}, {tc})\nSELECT {ownerParameters}, {u}.{k}\nFROM {rows}\nWHERE NOT EXISTS (SELECT 1 FROM {jt} WHERE {ownedInJoin} AND {jt}.{tc} = {u}.{k})",
                [ d.ListValue(keyArray target keys) ]
            else
                // An array per part, read back in rows by unnest.
                let values = [ for k in keys -> target.Key.ToColumns k ]

                let arrays =
                    targets
                    |> List.mapi (fun i (_, part) ->
                        match Model.arrayOf part.ProviderType with
                        | Some(_, make) -> d.ListValue(make [ for v in values -> v.[i] ] :?> Array)
                        | None -> invalidOp ("a key part of " + part.ProviderType.Name + " cannot be sent as an array"))

                let lists =
                    targets
                    |> List.mapi (fun i (_, part) -> p (n + i), Sql.partStoreType part |> Option.bind (fun t -> Sql.castName d t true))

                let tcs = targets |> List.map (fst >> q)
                let ks = targets |> List.mapi (fun i _ -> q ("k" + string (i + 1)))
                let matches = List.zip tcs ks |> List.map (fun (tc, k) -> jt + "." + tc + " = " + u + "." + k) |> String.concat " AND "
                let targetColumns = String.concat ", " tcs
                let read = ks |> List.map (fun k -> u + "." + k) |> String.concat ", "
                let named = String.concat ", " ks

                let listed = render (fun w -> d.ListRows(w, lists, None))
                let rows = render (fun w -> d.ListRows(w, lists, Some("u", [ for i in 1 .. targets.Length -> "k" + string i ])))

                $"DELETE FROM {jt}\nWHERE {owned} AND ({targetColumns}) NOT IN (SELECT * FROM {listed})",
                $"INSERT INTO {jt} ({ownerColumns}, {targetColumns})\nSELECT {ownerParameters}, {read}\nFROM {rows}\nWHERE NOT EXISTS (SELECT 1 FROM {jt} WHERE {ownedInJoin} AND {matches})",
                arrays

        // A link to a record that does not exist is the field's error, by the
        // target side's default name, over every column of a key of several parts.
        let linkKey = j.Table + "_" + (targets |> List.map fst |> String.concat "_") + "_fkey"

        let failed (f: DbFailure) =
            match f.Constraint with
            | Some name when name = linkKey -> Fail(Invalid(Cs.addError s a.Name (FieldErrorKind.ForeignKeyConstraint name) "does not exist"))
            | _ -> failedWrite schema s f

        match l.Mode with
        | PutLinks ->
            Round.both (run removeSql (ownerValues @ targetParameters)) (run insertSql (ownerValues @ targetParameters))
            |> Round.map (fun (removed, added) ->
                pair (replied sess failed removed) (replied sess failed added)
                |> Result.map (fun _ -> a.Name, a.Ctors.Loaded(a.MakeList l.Targets))))
    |> Round.all
    |> Round.map (together >> Result.map Map.ofList)

and private writeChildList model sess (schema: SchemaInfo) (s: CsState) (parentKey: obj) (parentInserted: bool) : Round<Written<Map<string, obj>>> =
    s.Children
    |> List.mapi (fun wi w -> writeChildWrite model sess schema s wi w parentKey parentInserted |> Round.map (Result.map (fun v -> w.Field, v)))
    |> Round.all
    |> Round.map (together >> Result.map Map.ofList)

/// One relationship's child writes, in order, each its own statement.
and private writeChildWrite model sess (schema: SchemaInfo) (s: CsState) (wi: int) (w: ChildWrite) (parentKey: obj) (parentInserted: bool) =
    let a = (schema.TryAssoc w.Field).Value
    let target = schemaOf model a.Target
    let fk = target.Field a.ForeignKey
    let ownedBy =
        if fk.Composite then
            // A reference over several columns (composite-references.feature):
            // every part equals the owner's.
            let owner = if fk.IsOption then fk.WrapSome parentKey else parentKey

            List.zip fk.Parts (fk.ToColumns owner)
            |> List.map (fun (p, v) -> Sql.Binary("=", Sql.Column(target.Table, p.Name), Sql.partValue p v))
            |> Sql.conj
        else
            Sql.Binary("=", Sql.Column(target.Table, fk.Column), Sql.fieldScalar fk parentKey)

    // The parent, with the i-th child's state replaced by what its write found.
    let placed (i: int) (p: Problem) =
        let replaceChild (child: CsState) =
            let w' = { w with Children = w.Children |> List.mapi (fun j c -> if i = j then child else c) }
            { s with Children = s.Children |> List.mapi (fun j x -> if j = wi then w' else x) }

        match p with
        | Fail(Invalid c) -> Fail(Invalid(replaceChild c))
        | Fail(Stale c) -> Fail(Stale(replaceChild c))
        | other -> other

    // 1. Authoritative replacement: find existing children in scope that are omitted.
    let removal: Round<Written<unit>> =
        match w.Replace with
        | None -> Done(Ok())
        | Some _ when parentInserted -> Done(Ok())
        | Some scope ->
            let scopeWheres =
                match scope with
                | AllChildren -> []

            let select =
                { Sql.select (Sql.Table(target.Table, target.Table)) with
                    Columns = [ Sql.Column(target.Table, target.Key.Column), target.Key.Column ]
                    Where = ownedBy :: scopeWheres
                    // Locked against other writers, unless writers take turns.
                    Lock =
                        if sess.Provider.Dialect.Capabilities.RowLocking then
                            Some { Strength = "UPDATE"; Of = None; Wait = None }
                        else
                            None }

            let reader = columnReader target.Key.ProviderType

            Round.fetch sess (Sql.renderSelect sess.Provider.Dialect select) (fun r ->
                match target.Key.FromDb(reader.Invoke(r, 0)) with
                | Ok v -> v
                | Error m -> failwith m)
            |> Round.bind (fun reply ->
                match replied sess asFailed reply with
                | Error p -> Done(Error p)
                | Ok existing ->
                    let kept =
                        w.Children
                        |> List.filter (fun c -> c.Action = ChangeAction.Update)
                        |> List.choose (keyValue target)
                        |> HashSet

                    let omitted = existing |> Seq.filter (fun k -> not (kept.Contains k)) |> List.ofSeq

                    if omitted.IsEmpty then
                        Done(Ok())
                    else
                        let ids = Sql.AnyOf(Sql.Column(target.Table, target.Key.Column), keyArray target omitted, Sql.storeType target.Key)

                        // What the owner owns and no longer holds is deleted.
                        Round.exec
                            sess
                            (Sql.renderDelete
                                sess.Provider.Dialect
                                { Table = target.Table
                                  Where = [ ownedBy; ids ]
                                  Returning = [] })
                        |> Round.map (replied sess asFailed >> Result.map ignore))

    // 2. Insert / update / delete listed children.
    // The child must belong to this parent. Its own write says so (the owner
    // in its WHERE); only when it writes nothing does a read tell another
    // parent's child from a stale one. A child deleted with what it owns is
    // checked first, so that nothing under it is kept if it is not.
    let changed (i: int) (child: CsState) : Round<Written<obj>> =
        let childKey = (keyValue target child).Value

        let notOwned () =
            Error(Fail(Invalid(Cs.addError child target.Key.Name FieldErrorKind.NotOwned $"{target.Name} {childKey} does not belong to this {schema.Name}")))

        let owned =
            let ownerCheck =
                { Sql.select (Sql.Table(target.Table, target.Table)) with
                    Columns = [ Sql.Literal "1", "one" ]
                    Where = [ keyFilter target childKey; ownedBy ] }

            Round.fetch sess (Sql.renderSelect sess.Provider.Dialect ownerCheck) ignore
            |> Round.map (replied sess asFailed >> Result.map (fun rows -> rows.Count > 0))

        if child.Action = ChangeAction.Delete && not (child.Children.IsEmpty && child.Links.IsEmpty) then
            Round.andThen owned (write model sess child)
            |> Round.map (fun (isOwned, written) ->
                match isOwned with
                | Ok false -> notOwned ()
                | Ok true -> written
                | Error p -> pair (Error p) written |> Result.map snd)
            |> Round.map (Result.mapError (placed i))
        else
            writeWithin model sess [ ownedBy ] child
            |> Round.followUp (function
                | Error(Fail(Stale _)) as stale ->
                    owned
                    |> Round.map (function
                        | Ok false -> notOwned ()
                        // Not sent, because a statement after it failed: what
                        // it is known to be is stale.
                        | Ok true
                        | Error NotSent -> stale
                        | Error p -> Error p)
                    |> Some
                | _ -> None)
            |> Round.map (Result.mapError (placed i))

    let listed: Round<Written<obj> list> =
        w.Children
        |> List.mapi (fun i child ->
            match child.Action with
            | ChangeAction.Insert -> write model sess (setField target child a.ForeignKey parentKey) |> Round.map (Result.mapError (placed i))
            | ChangeAction.Update
            | ChangeAction.Delete -> changed i child)
        |> Round.all

    Round.andThen removal listed
    |> Round.map (fun (removed, results) ->
        pair removed (together results)
        |> Result.map (fun (_, values) ->
            let produced =
                List.zip w.Children values
                |> List.filter (fun (c, _) -> c.Action <> ChangeAction.Delete)
                |> List.map snd

            // The written set is the complete relationship only when it is
            // authoritative for all children (or the parent is new).
            let complete =
                parentInserted
                || (match w.Replace with
                    | Some AllChildren -> true
                    | _ -> false)

            match w.Final with
            // An owned list: the new list, with each written item as stored.
            | Some final ->
                let stored = Queue<obj>(produced)
                final |> List.map (fun (c, written) -> if written then stored.Dequeue() else c) |> Seq.ofList |> a.MakeList |> a.Ctors.Loaded
            | None when complete -> a.Ctors.Loaded(a.MakeList produced)
            | None -> a.Ctors.Empty))

and private keyArray (target: SchemaInfo) (keys: obj list) =
    let arr = Array.CreateInstance(target.Key.ProviderType, keys.Length)
    keys |> List.iteri (fun i k -> arr.SetValue(target.Key.ScalarToDb k, i))
    arr

/// The write of `s` as round trips, once its checks pass: Error when they
/// refuse it, before anything is sent. The round's value is the written
/// record, or why not; None when its statements were not sent because an
/// earlier one failed (Tx.traverse sends several writes together).
let plan (model: Model) (sess: Session) (s: CsState) : Result<Round<Result<obj, Failure option>>, Failure> =
    let s = check model s

    if not (isValid s) then
        Error(Invalid s)
    else
        Ok(
            write model sess s
            |> Round.map (
                Result.mapError (function
                    | Fail f -> Some f
                    | NotSent -> None)
            )
        )

/// Validates then writes `s`: one round trip for it and what it owns, after
/// any generated key that needs.
let run (model: Model) (sess: Session) (s: CsState) : Task<Result<obj, Failure>> =
    task {
        match plan model sess s with
        | Error f -> return Error f
        | Ok round ->
            let! written = Round.run sess round

            return
                written
                |> Result.mapError (function
                    | Some f -> f
                    | None -> invalidOp "a write's statements were not sent, and none failed")
    }

let needsTransaction (s: CsState) = not s.Children.IsEmpty || not s.Links.IsEmpty
