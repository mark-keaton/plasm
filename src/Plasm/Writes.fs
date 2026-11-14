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

let private versionValue (schema: SchemaInfo) (s: CsState) : int option =
    match schema.Version with
    | None -> None
    | Some v ->
        match s.ExpectedVersion with
        | Some e -> Some e
        | None -> s.Data |> Option.map (fun d -> unbox<int> (schema.Shape.Deconstruct d).[v.Index])

/// For a record without a version field, the update or delete applies only if
/// every column still holds what the change was made from (concurrency.feature).
/// A change made from a key alone has nothing to compare.
let private originalWhere (schema: SchemaInfo) (s: CsState) =
    match schema.Version, s.Data with
    | None, Some d ->
        let values = schema.Shape.Deconstruct d

        schema.Fields
        |> Array.filter (fun f -> f.Index <> schema.Key.Index)
        |> Array.toList
        |> List.collect (fun f ->
            Sql.fieldColumns f values.[f.Index]
            |> List.map (fun (column, value) -> Sql.Binary("IS NOT DISTINCT FROM", Sql.Column(schema.Table, column), value)))
    | _ -> []

/// Owned lists (owned-collections.feature) as child writes. An insert writes
/// every item; a change made from a record read compares the old list with the
/// new by key (inserting new items, updating changed ones, deleting missing
/// ones); a delete deletes what the owner owns first.
let rec private owned (model: Model) (s: CsState) : CsState =
    let schema = schemaOf model s.Type
    let declared = s.Children |> List.map (fun w -> w.Assoc) |> set

    let items (a: AssocMap) (list: obj) =
        match a.Ctors.Read list with
        | Some l -> Reflection.listItems a.Target l |> List.ofSeq
        | None -> []

    let state t action (v: obj) = Cs.emptyState t (Some v) action

    // Linked lists: only join-table rows are written (linked-collections.feature).
    let linksDeclared = s.Links |> List.map (fun l -> l.Assoc) |> set

    let s =
        schema.Assocs
        |> Array.filter (fun a -> a.Linked && not (linksDeclared.Contains a.Name))
        |> Array.fold
            (fun (st: CsState) a ->
                let target = schemaOf model a.Target
                let keyOf (c: obj) = (target.Shape.Deconstruct c).[target.Key.Index]
                let put targets = { st with Links = st.Links @ [ { Assoc = a.Name; Mode = PutLinks; Targets = targets } ] }

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
            let child children final = { Assoc = a.Name; Replace = None; Children = children; Final = Some final }

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
                    { st with Children = st.Children @ [ { Assoc = a.Name; Replace = Some AllChildren; Children = []; Final = Some [] } ] })
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
        | Some v when s.Data.IsNone && s.ExpectedVersion.IsNone ->
            st <-
                err
                    st
                    v.Name
                    (FieldErrorKind.Custom "version")
                    "a key-only change to a versioned record must state the expected version (Changeset.expectVersion)"
        | _ -> ()
    | ChangeAction.Insert -> ()

    let children =
        s.Children
        |> List.map (fun w ->
            match schema.TryAssoc w.Assoc with
            | Some a when a.Kind = AssocKind.HasMany ->
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
                            w.Assoc
                            FieldErrorKind.DuplicateIdentity
                            $"more than one change for {target.Name} {d}; combine them into one changeset"

                for c in checkedChildren do
                    if c.Type <> target.Type then
                        st <- err st w.Assoc (FieldErrorKind.Custom "type") $"child changeset is for {c.Type.Name}, expected {target.Name}"

                { w with Children = checkedChildren }
            | Some a when a.Kind = AssocKind.ManyToMany ->
                st <-
                    err
                        st
                        w.Assoc
                        (FieldErrorKind.Custom "relationship")
                        $"{schema.Name}.{a.Name} is many-to-many; change its links with Changeset.putLinks, addLinks or removeLinks"
                w
            | Some a ->
                st <- err st w.Assoc (FieldErrorKind.Custom "relationship") $"{a.Kind} relationships cannot be written through the parent yet"
                w
            | None ->
                st <- err st w.Assoc (FieldErrorKind.Custom "relationship") "not a declared relationship"
                w)

    for l in s.Links do
        match schema.TryAssoc l.Assoc with
        | Some a when a.Kind = AssocKind.ManyToMany ->
            let target = schemaOf model a.Target

            if l.Targets |> List.exists (fun t -> isNull t || t.GetType() <> target.Type) then
                st <- err st l.Assoc (FieldErrorKind.Custom "type") $"links of {schema.Name}.{a.Name} must be {target.Name} records"
        | Some a ->
            st <-
                err
                    st
                    l.Assoc
                    (FieldErrorKind.Custom "relationship")
                    $"{schema.Name}.{a.Name} is {a.Kind}, not many-to-many; links are only written through manyToMany relationships (use putChildren for has-many)"
        | None -> st <- err st l.Assoc (FieldErrorKind.Custom "relationship") "not a declared relationship"

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
        let stem = $"{table}_{f.Column}"

        if name = stem + "_key" then Some(f.Name, FieldErrorKind.UniqueConstraint name, "has already been taken")
        elif name = stem + "_fkey" then Some(f.Name, FieldErrorKind.ForeignKeyConstraint name, "does not exist")
        elif name = stem + "_check" then Some(f.Name, FieldErrorKind.CheckConstraint name, "is invalid")
        else None)

let private constraintError (schema: SchemaInfo) (s: CsState) (f: DbFailure) : CsState option =
    match f.Constraint with
    | Some name ->
        conventional schema name |> Option.map (fun (field, kind, message) -> Cs.addError s field kind message)
    | None -> None

/// Runs a command, translating declared constraint violations into
/// changeset errors. Other database failures stay database failures.
let private guarded (sess: Session) (schema: SchemaInfo) (s: CsState) (f: unit -> Task<'a>) : Task<Result<'a, Failure>> =
    task {
        try
            let! v = f ()
            return Ok v
        with
        | :? OperationCanceledException as ex -> return Err.rethrow ex
        | MaterializationFailure _ as ex -> return Err.rethrow ex
        | ex ->
            let failure = classifyOrRaise sess ex

            match constraintError schema s failure with
            | Some withError -> return Error(Invalid withError)
            | None -> return Error(Failed failure)
    }

let private returning (schema: SchemaInfo) = Sql.returningFields schema.Table schema.Fields

let private keyFilter (schema: SchemaInfo) (key: obj) =
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
          if s.Children |> List.exists (fun w -> w.Replace.IsSome) && not c.RowLocking then
              "row locking (SELECT ... FOR UPDATE, to replace children)"
          if not s.Links.IsEmpty && not c.ArrayParameters then
              "array parameters (to write many-to-many links)"
          yield! s.Children |> List.collect (fun w -> w.Children) |> List.collect needs ]

    match needs s |> List.distinct with
    | [] -> None
    | missing -> Some(String.concat "; " missing)

let rec write (model: Model) (sess: Session) (s: CsState) : Task<Result<obj, Failure>> =
    task {
      match unsupported sess.Provider.Dialect.Capabilities s with
      | Some op -> return Error(Unsupported op)
      | None ->
        let schema = schemaOf model s.Type

        match s.Action with
        | ChangeAction.Insert -> return! insert model sess schema s
        | ChangeAction.Update -> return! update model sess schema s
        | ChangeAction.Delete -> return! delete model sess schema s
    }

and private insert model sess schema s =
    task {
        let proposed = s.Proposed.Value
        let values = schema.Shape.Deconstruct proposed

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

        let! inserted = guarded sess schema s (fun () -> query sess cmd (readEntity schema))

        match inserted with
        | Error e -> return Error e
        | Ok rows ->
            let row = rows.[0]
            let key = row.[schema.Key.Index]
            let! children = writeChildren model sess schema s key true

            match children with
            | Error e -> return Error e
            | Ok written ->
                // Keep relationship values supplied on the inserted record.
                for a in schema.Assocs do
                    row.[a.Index] <- values.[a.Index]

                for KeyValue(name, value) in written do
                    row.[(schema.TryAssoc name).Value.Index] <- value

                return Ok(schema.Shape.Construct row)
    }

and private update model sess schema s =
    task {
        let key = (keyValue schema s).Value
        let version = versionValue schema s

        let sets =
            s.Changes
            |> Map.toList
            |> List.collect (fun (name, v) -> Sql.fieldColumns (schema.Field name) v)

        let versionSet, versionWhere =
            match schema.Version, version with
            | Some vf, Some v ->
                [ vf.Column, Sql.Binary("+", Sql.Column(schema.Table, vf.Column), Sql.Param(box 1)) ],
                [ Sql.Binary("=", Sql.Column(schema.Table, vf.Column), Sql.Param(box v)) ]
            | _ -> [], []

        let where = keyFilter schema key :: versionWhere @ originalWhere schema s

        let! row =
            // Nothing to write on the row itself (relationship-only changes without
            // a version to bump). Re-read only when the full record is unknown.
            if sets.IsEmpty && (versionSet.IsEmpty || (s.Children.IsEmpty && s.Links.IsEmpty)) then
                match s.Data with
                | Some d -> Task.FromResult(Ok(Some(schema.Shape.Deconstruct d)))
                | None ->
                    guarded sess schema s (fun () ->
                        task {
                            let select =
                                { Sql.select (Sql.Table(schema.Table, schema.Table)) with
                                    Columns = Translate.entityColumns schema.Table schema
                                    Where = where }

                            let! rows = query sess (Sql.renderSelect sess.Provider.Dialect select) (readEntity schema)
                            return Seq.tryHead rows
                        })
            else
                // A changed parent bumps its version; child-only writes still take
                // the parent's version so concurrent writers conflict.
                let cmd =
                    Sql.renderUpdate
                        sess.Provider.Dialect
                        { Table = schema.Table
                          Set =
                            (if sets.IsEmpty && versionSet.IsEmpty then
                                 [ schema.Key.Column, Sql.Column(schema.Table, schema.Key.Column) ]
                             else
                                 sets @ versionSet)
                          Where = where
                          Returning = returning schema }

                guarded sess schema s (fun () ->
                    task {
                        let! rows = query sess cmd (readEntity schema)
                        return Seq.tryHead rows
                    })

        match row with
        | Error e -> return Error e
        | Ok None -> return Error(Stale s)
        | Ok(Some row) ->
            let! children = writeChildren model sess schema s key false

            match children with
            | Error e -> return Error e
            | Ok written -> return Ok(finish schema s (Array.copy row) written)
    }

and private delete model sess schema s =
    task {
        let key = (keyValue schema s).Value

        // What the record owns goes first (owned-collections.feature).
        let! children =
            if s.Children.IsEmpty && s.Links.IsEmpty then Task.FromResult(Ok Map.empty) else writeChildren model sess schema s key false

        match children with
        | Error e -> return Error e
        | Ok _ ->

        let versionWhere =
            match schema.Version, versionValue schema s with
            | Some vf, Some v -> [ Sql.Binary("=", Sql.Column(schema.Table, vf.Column), Sql.Param(box v)) ]
            | _ -> []

        let cmd =
            Sql.renderDelete
                sess.Provider.Dialect
                { Table = schema.Table
                  Where = keyFilter schema key :: versionWhere @ originalWhere schema s
                  Returning = [] }

        let! n = guarded sess schema s (fun () -> execute sess cmd)

        match n with
        | Error e -> return Error e
        | Ok 0 -> return Error(Stale s)
        | Ok _ -> return Ok(s.Data |> Option.toObj)
    }

/// Writes child changesets for each relationship write. Returns relationship
/// values to place on the parent where the written set is complete.
and private writeChildren model sess (schema: SchemaInfo) (s: CsState) (parentKey: obj) (parentInserted: bool) =
    task {
        let! children = writeChildList model sess schema s parentKey parentInserted

        match children with
        | Error e -> return Error e
        | Ok written when s.Links.IsEmpty -> return Ok written
        | Ok written ->
            let! links = guarded sess schema s (fun () -> writeLinks model sess schema s parentKey)
            return links |> Result.map (Map.fold (fun m k v -> Map.add k v m) written)
    }

/// Join-table writes for many-to-many link changes. Returns the relationship
/// value for the parent: the new set after putLinks, NotLoaded otherwise.
and private writeLinks model (sess: Session) (schema: SchemaInfo) (s: CsState) (parentKey: obj) : Task<Map<string, obj>> =
    task {
        let d = sess.Provider.Dialect
        let q = d.QuoteIdentifier
        let mutable written = Map.empty

        for l in s.Links do
            let a = (schema.TryAssoc l.Assoc).Value
            let j = a.Through.Value
            let target = schemaOf model a.Target
            let owner = schema.Key.ScalarToDb parentKey

            let keys =
                l.Targets
                |> List.map (fun t -> target.Shape.Deconstruct t |> fun vs -> vs.[target.Key.Index])
                |> List.distinct

            let arr = Array.CreateInstance(target.Key.ProviderType, keys.Length)
            keys |> List.iteri (fun i k -> arr.SetValue(target.Key.ScalarToDb k, i))
            let p i = d.ParameterName i
            let jt, oc, tc = q j.Table, q j.OwnerColumn, q j.TargetColumn

            let run (sql: string) (ps: obj list) =
                execute sess { Sql = sql; Parameters = ps }

            let insertMissing () =
                run
                    $"INSERT INTO {jt} ({oc}, {tc}) SELECT {p 0}, u.k FROM unnest({p 1}) AS u(k) WHERE NOT EXISTS (SELECT 1 FROM {jt} AS x WHERE x.{oc} = {p 2} AND x.{tc} = u.k)"
                    [ owner; box arr; owner ]

            match l.Mode with
            | PutLinks ->
                let! _ = run $"DELETE FROM {jt} WHERE {oc} = {p 0} AND NOT ({tc} = ANY({p 1}))" [ owner; box arr ]
                let! _ = insertMissing ()
                written <- written.Add(a.Name, a.Ctors.Loaded(a.MakeList l.Targets))
            | AddLinks ->
                let! _ = insertMissing ()
                written <- written.Add(a.Name, a.Ctors.NotLoaded)
            | RemoveLinks ->
                let! _ = run $"DELETE FROM {jt} WHERE {oc} = {p 0} AND {tc} = ANY({p 1})" [ owner; box arr ]
                written <- written.Add(a.Name, a.Ctors.NotLoaded)

        return written
    }

and private writeChildList model sess (schema: SchemaInfo) (s: CsState) (parentKey: obj) (parentInserted: bool) =
    task {
        let mutable result: Result<Map<string, obj>, Failure> = Ok Map.empty
        let mutable state = s

        for wi, w in List.indexed s.Children do
            match result with
            | Error _ -> ()
            | Ok written ->
                let a = (schema.TryAssoc w.Assoc).Value
                let target = schemaOf model a.Target
                let fk = target.Field a.ForeignKey

                let ownedBy =
                    Sql.Binary("=", Sql.Column(target.Table, fk.Column), Sql.fieldScalar fk parentKey)

                let replaceChild (i: int) (child: CsState) =
                    let w' =
                        { w with Children = w.Children |> List.mapi (fun j c -> if i = j then child else c) }

                    { state with Children = state.Children |> List.mapi (fun j x -> if j = wi then w' else x) }

                // 1. Authoritative replacement: find existing children in scope that are omitted.
                let! removal =
                    task {
                        match w.Replace with
                        | None -> return Ok()
                        | Some _ when parentInserted -> return Ok()
                        | Some scope ->
                            let ctx = Translate.newCtx model

                            let scopeWheres =
                                match scope with
                                | AllChildren -> []
                                | MatchingChildren def -> fst (Translate.translateBody ctx def target.Table target)

                            let select =
                                { Sql.select (Sql.Table(target.Table, target.Table)) with
                                    Columns = [ Sql.Column(target.Table, target.Key.Column), target.Key.Column ]
                                    Where = ownedBy :: scopeWheres
                                    ForUpdate = true }

                            let reader = columnReader target.Key.ProviderType

                            let! existing =
                                query sess (Sql.renderSelect sess.Provider.Dialect select) (fun r ->
                                    match target.Key.FromDb(reader.Invoke(r, 0)) with
                                    | Ok v -> v
                                    | Error m -> failwith m)

                            let kept =
                                w.Children
                                |> List.filter (fun c -> c.Action = ChangeAction.Update)
                                |> List.choose (keyValue target)
                                |> HashSet

                            let omitted = existing |> Seq.filter (fun k -> not (kept.Contains k)) |> List.ofSeq

                            if omitted.IsEmpty then
                                return Ok()
                            else
                                let ids = Sql.AnyOf(Sql.Column(target.Table, target.Key.Column), keyArray target omitted, Sql.storeType target.Key)

                                match a.OnReplace with
                                | OnReplace.Reject ->
                                    let st =
                                        Cs.addError
                                            state
                                            w.Assoc
                                            FieldErrorKind.ReplacementRejected
                                            $"replacement omits {omitted.Length} existing {target.Name} record(s) and {schema.Name}.{a.Name} has no OnReplace policy; configure OnReplace.Delete or OnReplace.Nilify"

                                    return Error(Invalid st)
                                | OnReplace.Delete ->
                                    let cmd =
                                        Sql.renderDelete
                                            sess.Provider.Dialect
                                            { Table = target.Table
                                              Where = [ ownedBy; ids ]
                                              Returning = [] }

                                    let! _ = execute sess cmd
                                    return Ok()
                                | OnReplace.Nilify ->
                                    let cmd =
                                        Sql.renderUpdate
                                            sess.Provider.Dialect
                                            { Table = target.Table
                                              Set = [ fk.Column, Sql.Literal "NULL" ]
                                              Where = [ ownedBy; ids ]
                                              Returning = [] }

                                    let! _ = execute sess cmd
                                    return Ok()
                    }

                match removal with
                | Error e -> result <- Error e
                | Ok() ->
                    // 2. Insert / update / delete listed children.
                    let produced = ResizeArray<obj>()
                    let mutable i = 0
                    let mutable failed = None

                    for child in w.Children do
                        if failed.IsNone then
                            let! r =
                                task {
                                    match child.Action with
                                    | ChangeAction.Insert -> return! write model sess (setField target child a.ForeignKey parentKey)
                                    | ChangeAction.Update
                                    | ChangeAction.Delete ->
                                        // The child must belong to this parent.
                                        let childKey = (keyValue target child).Value

                                        let ownerCheck =
                                            { Sql.select (Sql.Table(target.Table, target.Table)) with
                                                Columns = [ Sql.Literal "1", "one" ]
                                                Where = [ keyFilter target childKey; ownedBy ] }

                                        let! owned = query sess (Sql.renderSelect sess.Provider.Dialect ownerCheck) ignore

                                        if owned.Count = 0 then
                                            return
                                                Error(
                                                    Invalid(
                                                        Cs.addError
                                                            child
                                                            target.Key.Name
                                                            FieldErrorKind.NotOwned
                                                            $"{target.Name} {childKey} does not belong to this {schema.Name}"
                                                    )
                                                )
                                        else
                                            return! write model sess child
                                }

                            match r with
                            | Ok v -> if child.Action <> ChangeAction.Delete then produced.Add v
                            | Error(Invalid c) -> failed <- Some(Invalid(replaceChild i c))
                            | Error(Stale c) -> failed <- Some(Stale(replaceChild i c))
                            | Error(Failed f) -> failed <- Some(Failed f)
                            | Error(Unsupported op) -> failed <- Some(Unsupported op)

                        i <- i + 1

                    match failed with
                    | Some f -> result <- Error f
                    | None ->
                        // The written set is the complete relationship only when
                        // it is authoritative for all children (or the parent is new).
                        let complete =
                            parentInserted
                            || (match w.Replace with
                                | Some AllChildren -> true
                                | _ -> false)

                        let value =
                            match w.Final with
                            // An owned list: the new list, with each written item as stored.
                            | Some final ->
                                let stored = Collections.Generic.Queue<obj>(produced)
                                final |> List.map (fun (c, written) -> if written then stored.Dequeue() else c) |> Seq.ofList |> a.MakeList |> a.Ctors.Loaded
                            | None when complete -> a.Ctors.Loaded(a.MakeList produced)
                            | None -> a.Ctors.NotLoaded

                        result <- Ok(written.Add(w.Assoc, value))


        return result
    }

and private keyArray (target: SchemaInfo) (keys: obj list) =
    let arr = Array.CreateInstance(target.Key.ProviderType, keys.Length)
    keys |> List.iteri (fun i k -> arr.SetValue(target.Key.ScalarToDb k, i))
    arr

/// Validates then writes `s`, opening a transaction when children are involved.
let run (model: Model) (sess: Session) (s: CsState) : Task<Result<obj, Failure>> =
    task {
        let s = check model s

        if not (isValid s) then
            return Error(Invalid s)
        else
            return! write model sess s
    }

let needsTransaction (s: CsState) = not s.Children.IsEmpty || not s.Links.IsEmpty
