namespace Plasm

open System
open System.Reflection
open System.Collections.Concurrent
open Microsoft.FSharp.Quotations

/// How a record is stored, read from the record itself (stored-records.feature):
/// its [<Table>], its field named Id, the attributes on its fields, and the
/// types of its fields. Nothing here guesses: what the types leave ambiguous
/// is an error naming the record and the field.
module internal Derive =

    let private attr<'A when 'A :> Attribute> (m: MemberInfo) : 'A option =
        match m.GetCustomAttribute(typeof<'A>, false) with
        | null -> None
        | a -> Some(a :?> 'A)

    let private has<'A when 'A :> Attribute> (m: MemberInfo) = (attr<'A> m).IsSome

    let tableName (t: Type) = attr<TableAttribute> t |> Option.map (fun a -> a.Name)

    /// A record that names its table.
    let isStored (t: Type) = Shape.isRecord t && (tableName t).IsSome

    let fields (t: Type) = (Shape.record t).Fields

    let keyField (t: Type) = fields t |> Array.tryFind (fun p -> p.Name = "Id")

    let private integers = Collections.Generic.HashSet<Type>([ typeof<int64>; typeof<int32>; typeof<int16> ])

    /// An integer, or a single-case union whose one field is an integer.
    let private isIntegerKey (t: Type) =
        integers.Contains t
        || (Shape.isUnion t
            && not (Reflection.isOption t || Reflection.isList t)
            && (let cases = (Shape.union t).Cases
                cases.Length = 1
                && (let fs = cases.[0].Fields
                    fs.Length = 1 && integers.Contains(snd fs.[0]))))

    /// The database generates an integer key unless the field says [<Assigned>].
    let keyGenerated (key: PropertyInfo) = isIntegerKey key.PropertyType && not (has<AssignedAttribute> key)

    /// The record a list, option or record field holds.
    let targetOf (t: Type) = if Reflection.isList t || Reflection.isOption t then Reflection.elementType t else t

    let private unwrapOption (t: Type) = if Reflection.isOption t then Reflection.elementType t else t

    /// The fields of `child` that hold a key of type `ownerKey`. A built-in key
    /// type (int64, Guid, ...) is never matched: only a typed id says whose
    /// key a field holds.
    let backReferences (child: Type) (ownerKey: Type) =
        if Scalar.isBuiltIn ownerKey then
            [||]
        else
            fields child |> Array.filter (fun p -> unwrapOption p.PropertyType = ownerKey)

    /// What a field holds, as far as storage is concerned.
    type FieldShape =
        /// A list, record or optional record of a stored type.
        | OfStored of target: Type * isList: bool
        /// A list of a record that names no table.
        | ListOfUnstored of target: Type
        | Other

    let shapeOf (p: PropertyInfo) =
        let t = p.PropertyType

        if Reflection.isList t then
            let e = Reflection.elementType t

            if isStored e then OfStored(e, true)
            elif Shape.isRecord e then ListOfUnstored e
            else Other
        else
            let e = unwrapOption t
            if isStored e then OfStored(e, false) else Other

    let private snake = Paths.snakeCase

    /// The order an owned or linked list is read in, from [<OrderedBy>].
    let private order (owner: Type) (p: PropertyInfo) (target: Type) : Result<Expr option, string> =
        match attr<OrderedByAttribute> p with
        | None -> Ok None
        | Some o ->
            match fields target |> Array.tryFind (fun f -> f.Name = o.Field) with
            | Some f ->
                let v = Var("x", target)
                Ok(Some(Expr.Lambda(v, Expr.PropertyGet(Expr.Var v, f))))
            | None ->
                let names = fields target |> Array.map (fun f -> f.Name) |> String.concat ", "
                Error $"is ordered by {o.Field}, but {target.Name} has no field named {o.Field} (its fields are {names})"

    /// The schema definition the type describes, or every problem with it.
    let definition (t: Type) : Result<SchemaDef, ConfigError list> =
        let errors = ResizeArray<ConfigError>()
        let err (prop: string option) msg = errors.Add { Model = t.Name; Property = prop; Message = msg }

        if not (Shape.isRecord t) then
            err None "is not a record, and only records are stored"
            Error(List.ofSeq errors)
        else
            let table = tableName t

            if table.IsNone then
                err None $"names no table: say where it is stored with [<Table \"{snake t.Name}s\">]"
            elif String.IsNullOrWhiteSpace table.Value then
                err None $"names an empty table: say where it is stored with [<Table \"{snake t.Name}s\">]"

            let key = keyField t

            if key.IsNone then
                err None "has no field named Id, and a stored record's key is its field named Id"

            // An attribute where Plasm would not read it says something the
            // model does not do (stored-records.feature).
            for p in fields t do
                let isKey = key |> Option.exists (fun k -> k.Name = p.Name)
                let shape = shapeOf p

                if has<AssignedAttribute> p then
                    if not isKey then
                        err (Some p.Name) "is marked Assigned, but only a key is assigned: Assigned says the application, not the database, gives an integer key its value"
                    elif not (isIntegerKey p.PropertyType) then
                        err (Some p.Name) $"is marked Assigned, but a {p.PropertyType.Name} key is assigned already: only an integer key is generated by the database"

                match shape with
                | OfStored(_, true) -> ()
                | _ ->
                    if has<OrderedByAttribute> p && (match shape with OfStored(_, false) -> false | _ -> true) then
                        err (Some p.Name) "is marked OrderedBy, but only a list has an order"

                    if has<JoinTableAttribute> p then
                        err (Some p.Name) "is marked JoinTable, but only a list of stored records is linked through a join table"

                for name, marked in [ "Unique", has<UniqueAttribute> p; "Generated", has<GeneratedAttribute> p ] do
                    if marked then
                        match shape with
                        | OfStored(_, isList) ->
                            let what = if isList then "a list of stored records (an owned list, or a linked one)" else "an owned record"
                            err (Some p.Name) $"is marked {name}, but {what} is not a column: {name} is read only on a column of the record's own row"
                        | _ when has<JsonAttribute> p ->
                            err (Some p.Name) $"is marked {name}, but a Json document is one column Plasm does not compare or fill: {name} is read only on a scalar column"
                        | _ -> ()

            let versions = fields t |> Array.filter has<VersionAttribute>

            if versions.Length > 1 then
                err None $"""has two fields marked Version ({versions |> Array.map (fun p -> p.Name) |> String.concat ", "}); a record has one version"""

            for v in versions do
                if v.PropertyType <> typeof<int> then
                    err (Some v.Name) "is marked Version, so it must be an int"

            let assocs =
                [ for p in fields t |> Array.filter (fun p -> not (has<JsonAttribute> p)) do
                      match shapeOf p, key with
                      | ListOfUnstored e, _ ->
                          err (Some p.Name) $"is a list of {e.Name}, which names no table; a list of records is stored only if its records are (give {e.Name} a [<Table>])"
                      | OfStored(target, isList), Some k ->
                          let refs = backReferences target k.PropertyType

                          if refs.Length > 1 then
                              let names = refs |> Array.map (fun r -> r.Name) |> String.concat " and "
                              err (Some p.Name) $"holds {target.Name}s, which have two fields of type {k.PropertyType.Name} ({names}); say which one refers to its owner by giving {target.Name} only one"
                          elif refs.Length = 1 then
                              if has<JoinTableAttribute> p then
                                  err (Some p.Name) $"is marked JoinTable, but its {target.Name}s hold its key, so it is owned and needs no join table: remove [<JoinTable>], or remove the field of type {k.PropertyType.Name} from {target.Name} to link them"

                              match order t p target with
                              | Error m -> err (Some p.Name) m
                              | Ok o ->
                                  if not isList && o.IsSome then
                                      err (Some p.Name) "is one record, so it has no order"

                                  yield
                                      { Property = p
                                        Kind = (if isList then AssocKind.HasMany else AssocKind.HasOne)
                                        ForeignKey = refs.[0]
                                        Through = None
                                        Owned = true
                                        Linked = false
                                        Order = o }
                          elif not isList then
                              err
                                  (Some p.Name)
                                  $"is a {target.Name}, which has [<Table>]: as an owned record it needs a field of type {k.PropertyType.Name} to hold its {t.Name}'s key, and it has none. To store it in {t.Name}'s row instead, remove [<Table>] and its Id from {target.Name}; to refer to it, hold its id"
                          else
                              match attr<JoinTableAttribute> p, keyField target with
                              | None, _ ->
                                  let hint =
                                      if Scalar.isBuiltIn k.PropertyType then
                                          $"; to own them instead, give {t.Name} a typed id as its key and {target.Name} a field of that type"
                                      else
                                          $"; to own them instead, give {target.Name} a field of type {k.PropertyType.Name}"

                                  err (Some p.Name) $"links to {target.Name}s, but names no join table: say where the links are stored with [<JoinTable \"{snake t.Name}_{snake p.Name}\">]{hint}"
                              | Some _, None -> () // the target's own definition reports its missing Id
                              | Some j, Some _ ->
                                  match order t p target with
                                  | Error m -> err (Some p.Name) m
                                  | Ok o ->
                                      yield
                                          { Property = p
                                            Kind = AssocKind.ManyToMany
                                            ForeignKey = null
                                            Through =
                                              Some
                                                  { Table = j.Name
                                                    OwnerColumn = (if isNull j.OwnerColumn then snake t.Name + "_id" else j.OwnerColumn)
                                                    TargetColumn = (if isNull j.TargetColumn then snake target.Name + "_id" else j.TargetColumn) }
                                            Owned = false
                                            Linked = true
                                            Order = o }
                      | _ -> () ]

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                let named (pick: PropertyInfo -> bool) =
                    fields t |> Array.filter pick |> Array.map (fun p -> p.Name) |> Set.ofArray

                Ok
                    { Type = t
                      Table = table.Value
                      Key = Some(key.Value, keyGenerated key.Value)
                      Columns =
                        fields t
                        |> Array.choose (fun p -> attr<ColumnAttribute> p |> Option.map (fun c -> p.Name, c.Name))
                        |> Map.ofArray
                      Version = Array.tryHead versions
                      Generated = named has<GeneratedAttribute>
                      Unique = named has<UniqueAttribute>
                      NotReference = named has<NotReferenceAttribute>
                      Assocs = assocs }

    let private cache = ConcurrentDictionary<Type, Result<SchemaDef, ConfigError list>>()
    let private derive = Func<Type, Result<SchemaDef, ConfigError list>>(definition)

    /// `definition`, once per type.
    let cached (t: Type) = cache.GetOrAdd(t, derive)

    /// The definitions of `roots` and of every record they own, transitively,
    /// with every problem a model of them would have.
    let model (roots: Type list) : Result<SchemaDef list, ConfigError list> =
        let errors = ResizeArray<ConfigError>()
        let defs = Collections.Generic.List<SchemaDef>()
        let ownerOf = Collections.Generic.Dictionary<Type, Type>()
        let seen = Collections.Generic.HashSet<Type>()
        let rootSet = Collections.Generic.HashSet<Type>(roots)

        let rec visit (t: Type) =
            if seen.Add t then
                match cached t with
                | Error es -> errors.AddRange es
                | Ok d ->
                    defs.Add d

                    for a in d.Assocs do
                        let target = targetOf a.Property.PropertyType

                        if a.Owned then
                            match ownerOf.TryGetValue target with
                            | true, other when other <> t ->
                                errors.Add
                                    { Model = target.Name
                                      Property = None
                                      Message = $"is owned by both {other.Name} and {t.Name}; a record has one owner (refer to it by id from the other)" }
                            | _ ->
                                ownerOf.[target] <- t

                                if rootSet.Contains target then
                                    errors.Add
                                        { Model = target.Name
                                          Property = None
                                          Message = $"is owned by {t.Name}, so it comes with {t.Name}: leave it out of Model.build" }

                                visit target
                        elif a.Linked && not (rootSet.Contains target) then
                            errors.Add
                                { Model = t.Name
                                  Property = Some a.Property.Name
                                  Message = $"links to {target.Name}s, which are not in the model: list {target.Name} in Model.build, or give {target.Name} a field of type {(keyField t).Value.PropertyType.Name} to make it owned" }

        for r in roots do
            visit r

        if errors.Count > 0 then Error(List.ofSeq errors) else Ok(List.ofSeq defs)

[<AutoOpen; Trimming.QuerySurface>]
module StoredExtensions =

    /// The records of type `'T`, as a query source: `for c in tableOf<Card> do`.
    /// What is stored, and where, is read from `'T` (see Model.build).
    let tableOf<'T> : Table<'T> =
        match Derive.cached typeof<'T> with
        | Ok d -> Table<'T>(d)
        | Error _ ->
            // Reported by Model.build; a query only needs the type.
            Table<'T>(
                { Type = typeof<'T>
                  Table = defaultArg (Derive.tableName typeof<'T>) ""
                  Key = None
                  Columns = Map.empty
                  Version = None
                  Generated = Set.empty
                  Unique = Set.empty
                  NotReference = Set.empty
                  Assocs = [] }
            )
