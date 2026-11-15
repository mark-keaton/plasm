namespace Plasm

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection

/// The mappings domain scalars get without being registered
/// (scalar-conventions.feature). A mapping given to `Model.build` overrides them.
module internal Conventions =
    open System.Reflection

    /// The `create: 'P -> Result<'T, string>` functions of `t`: a static member,
    /// or a function in the type's module (compiled as `<T>Module`). Named as
    /// the domain writes them.
    let private creates (t: Type) (p: Type) =
        let result = typedefof<Result<_, _>>.MakeGenericType(t, typeof<string>)

        let wanted (m: MethodInfo) =
            m.Name = "create"
            && m.ReturnType = result
            && (let ps = m.GetParameters() in ps.Length = 1 && ps.[0].ParameterType = p)

        let onType = t.GetMethods(BindingFlags.Public ||| BindingFlags.Static) |> Array.filter wanted |> Array.map (fun m -> $"{t.Name}.create (a member)", m)

        let inModule =
            match t.Assembly.GetType(t.FullName + "Module") with
            | null -> [||]
            | m -> m.GetMethods(BindingFlags.Public ||| BindingFlags.Static) |> Array.filter wanted |> Array.map (fun f -> $"{t.Name}.create (in module {t.Name})", f)

        Array.append onType inModule

    /// Reads `create`'s Result: the value, or the domain's reason.
    let private outcome (r: obj) : Result<obj, string> =
        let case, fields = FSharpValue.GetUnionFields(r, r.GetType())
        if case.Name = "Ok" then Ok fields.[0] else Error(string fields.[0])

    /// A fieldless union, stored as its case names in snake case, ordered as
    /// it is declared.
    let private fieldless (t: Type) (cases: UnionCaseInfo[]) =
        let tag = FSharpValue.PreComputeUnionTagReader(t, true)
        let values = cases |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||], true))
        let stored = cases |> Array.map (fun c -> Paths.snakeCase c.Name)
        let byName = Array.zip stored values |> dict

        { DomainType = t
          ProviderType = typeof<string>
          ToProvider = fun v -> box stored.[tag v]
          FromProvider =
            fun v ->
                match byName.TryGetValue(unbox<string> v) with
                | true, c -> Ok c
                | _ -> Error $"'{v}' is not a known {t.Name}"
          Ordered = Some(List.ofArray stored)
          Cases = Array.zip cases stored |> Array.map (fun (c, n) -> c.Name, n) |> List.ofArray
          StoreType = None
          Description = $"{t.Name} as text, by convention" }

    /// A single-case union over a built-in type, stored as that type and read
    /// through its create function when it has one.
    let private wrapper (t: Type) (case: UnionCaseInfo) (p: Type) : Result<ScalarMapping, string> =
        let read = FSharpValue.PreComputeUnionReader(case, true)
        let toProvider (v: obj) = (read v).[0]

        let mapping fromProvider description =
            { DomainType = t
              ProviderType = p
              ToProvider = toProvider
              FromProvider = fromProvider
              Ordered = None
              Cases = []
              StoreType = None
              Description = description }

        match creates t p with
        | [| _, create |] -> Ok(mapping (fun v -> outcome (create.Invoke(null, [| v |]))) $"{t.Name} as {p.Name}, read through {t.Name}.create")
        | [||] when not (FSharpType.IsUnion(t, false)) ->
            Error
                $"type {t.Name} has a private representation and no create function, so Plasm cannot read it without bypassing the domain; give it `static member create: {p.Name} -> Result<{t.Name}, string>`, or register Scalar.tryWrap"
        | [||] ->
            let make = FSharpValue.PreComputeUnionConstructor(case, true)
            Ok(mapping (fun v -> Ok(make [| v |])) $"{t.Name} as {p.Name}, by convention")
        | several ->
            let names = several |> Array.map fst |> String.concat " and "
            Error $"type {t.Name} has more than one create function ({names}); keep one, or register a mapping"

    /// The mapping `t` gets by convention: Some(Ok) for a single-case union over
    /// a built-in type or a fieldless union, Some(Error) for one Plasm must not
    /// read, None for any other type.
    let tryFor (t: Type) : Result<ScalarMapping, string> option =
        if not (FSharpType.IsUnion(t, true)) || Reflection.isOption t || Reflection.isList t || Reflection.isAssoc t then
            None
        else
            let cases = FSharpType.GetUnionCases(t, true)

            if cases |> Array.forall (fun c -> c.GetFields().Length = 0) then
                Some(Ok(fieldless t cases))
            elif cases.Length = 1 && cases.[0].GetFields().Length = 1 && Scalar.isBuiltIn (cases.[0].GetFields().[0].PropertyType) then
                Some(wrapper t cases.[0] (cases.[0].GetFields().[0].PropertyType))
            else
                None

/// A reference the model infers (constraints.feature): `Model.Field` holds the
/// key of `Target`, because its type is `Target`'s key type (a typed id).
type ModelReference =
    { Model: string
      Field: string
      Target: string }

/// A validated set of schemas and scalar mappings. Construct with `Model.build`;
/// all configuration errors are reported together, before any query runs.
type Model internal (schemas: IReadOnlyDictionary<Type, SchemaInfo>, registered: IReadOnlyDictionary<Type, ScalarMapping>) =

    // The registered mappings, then those found by convention (a type met
    // only in a query, never in a field, gets its mapping on first use).
    let found = Collections.Concurrent.ConcurrentDictionary<Type, ScalarMapping option>()

    /// A type's mapping: registered, else by convention; (false, _) when none.
    let tryScalar (t: Type) =
        match registered.TryGetValue t with
        | true, m -> true, m
        | _ ->
            match found.GetOrAdd(t, fun t -> match Conventions.tryFor t with Some(Ok m) -> Some m | _ -> None) with
            | Some m -> true, m
            | None -> false, Unchecked.defaultof<ScalarMapping>

    member internal _.Schemas = schemas
    member internal _.Scalars = registered

    member internal _.TrySchema(t: Type) =
        match schemas.TryGetValue t with
        | true, s -> Some s
        | _ -> None

    /// F# Interactive: the submission of a mapped type that `t` (from another
    /// submission) is a redefinition of, or the reverse.
    member internal _.Redefinition(t: Type) =
        match Reflection.Fsi.submission t with
        | Some(used, name) ->
            schemas.Keys
            |> Seq.tryPick (fun k ->
                match Reflection.Fsi.submission k with
                | Some(mapped, n) when n = name && mapped <> used -> Some(used, mapped)
                | _ -> None)
        | None -> None

    member internal this.Schema(t: Type) =
        match this.TrySchema t with
        | Some s -> Ok s
        | None ->
            match this.Redefinition t with
            | Some(used, mapped) -> Error(TranslationError.RedefinedType(t.Name, Reflection.Fsi.redefined t.Name used mapped))
            | None -> Error(TranslationError.UnmappedType(Reflection.typeName t))

    /// Converts a closed (application-side) value of the given domain type to a
    /// provider parameter value. Returns Error if the type has no mapping.
    member internal _.ScalarToDb(t: Type, value: obj) : Result<obj, string> =
        if isNull value then
            Ok(box DBNull.Value)
        elif Scalar.isBuiltIn t then
            Ok value
        else
            match tryScalar t with
            | true, m -> Ok(m.ToProvider value)
            | _ -> Error $"type {Reflection.typeName t} has no scalar mapping"

    /// Converts a value read from the database (any provider type, or NULL) to
    /// the domain type `t`, which may be an option. Numeric provider values are
    /// converted to the requested numeric type (e.g. COUNT's bigint to int).
    member internal this.ScalarFromDb(t: Type, value: obj) : Result<obj, string> =
        let isNullValue = isNull value || value = box DBNull.Value

        if Reflection.isOption t then
            if isNullValue then
                Ok null
            else
                this.ScalarFromDb(Reflection.elementType t, value)
                |> Result.map (Reflection.makeSome t)
        elif isNullValue then
            Error $"NULL read for non-optional {Reflection.typeName t}"
        else
            let convert (target: Type) (v: obj) =
                if target.IsInstanceOfType v then
                    Ok v
                else
                    try
                        Ok(Convert.ChangeType(v, target, Globalization.CultureInfo.InvariantCulture))
                    with ex ->
                        Error $"cannot convert {v.GetType().Name} to {Reflection.typeName target}: {ex.Message}"

            if Scalar.isBuiltIn t then
                convert t value
            else
                match tryScalar t with
                | true, m -> convert m.ProviderType value |> Result.bind m.FromProvider
                | _ -> Error $"type {Reflection.typeName t} has no scalar mapping"

    /// The stored names of `t` (or of its option's element) in F#'s comparison
    /// order, when `t` is stored as text from a finite table.
    member internal _.OrderedNames(t: Type) =
        let t = if Reflection.isOption t then Reflection.elementType t else t

        match tryScalar t with
        | true, m -> m.Ordered
        | _ -> None

    /// The database type `t` (or its option's element) is stored as, when it is
    /// not the provider type's own (Scalar.asPgEnum).
    member internal _.StoreTypeOf(t: Type) =
        let t = if Reflection.isOption t then Reflection.elementType t else t

        match tryScalar t with
        | true, m -> m.StoreType
        | _ -> None

    member internal _.ProviderTypeOf(t: Type) =
        if Scalar.isBuiltIn t then
            Some t
        else
            match tryScalar t with
            | true, m -> Some m.ProviderType
            | _ -> None

    /// The references the model infers from typed ids, in schema order.
    member _.References: ModelReference list =
        [ for s in schemas.Values do
              for f in s.Fields do
                  match f.References with
                  | Some target -> yield { Model = s.Type.Name; Field = f.Name; Target = target.Name }
                  | None -> () ]

    /// The record types this model can persist.
    member _.Types = schemas.Keys |> Seq.toList

[<RequireQualifiedAccess>]
module Model =

    /// The scalar mapping of a type: built in (None), registered, or by convention.
    let private scalarMapping (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (scalarType: Type) : Result<ScalarMapping option, string> =
        if Scalar.isBuiltIn scalarType then
            Ok None
        else
            match scalars.TryGetValue scalarType with
            | true, m -> Ok(Some m)
            | _ ->
                match Conventions.tryFor scalarType with
                | Some(Ok m) -> Ok(Some m)
                | Some(Error reason) -> Error reason
                | None ->
                    Error
                        $"type {Reflection.typeName scalarType} has no persistence mapping; it is not a single-case union over a built-in type or a fieldless union, so register Scalar.wrap, Scalar.tryWrap or Scalar.enum"

    /// A field (or a union's case field) stored in one column.
    let private scalarField (name: string) (index: int) (column: string) (t: Type) (mapping: ScalarMapping option) : FieldMap =
        let isOpt = Reflection.isOption t
        let scalarType = if isOpt then Reflection.elementType t else t

        { Name = name
          Index = index
          Column = column
          FieldType = t
          ScalarType = scalarType
          IsOption = isOpt
          Generated = false
          Unique = false
          References = None
          Mapping = mapping
          ProviderType =
            match mapping with
            | Some m -> m.ProviderType
            | None -> scalarType
          WrapSome = if isOpt then Reflection.makeSome t else id
          ReadOption = if isOpt then Reflection.getOptionValue t else fun v -> if isNull v then None else Some v
          Union = None }

    /// A union with fields (one no scalar mapping or convention covers), stored
    /// across columns (union-fields.feature).
    let rec private unionLayout (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (t: Type) (column: string) : Result<UnionLayout, string> =
        if not (FSharpType.IsUnion(t, false)) then
            Error $"union {t.Name} has a private representation; Plasm would construct its cases behind the domain's back, so give it a scalar mapping instead"
        else
            let cases = FSharpType.GetUnionCases t

            let caseLayout (c: UnionCaseInfo) : Result<UnionCaseLayout, string> =
                let parts =
                    c.GetFields()
                    |> Array.toList
                    |> List.map (fun p ->
                        // Unnamed fields (Item1, Item2) are named after their case.
                        let name = if p.Name.StartsWith "Item" then $"{c.Name}_{p.Name}" else p.Name
                        let partColumn = column + "_" + Paths.snakeCase name
                        let ft = p.PropertyType
                        let inner = if Reflection.isOption ft then Reflection.elementType ft else ft

                        if Reflection.isList ft || FSharpType.IsRecord(inner, true) || (inner.IsArray && inner <> typeof<byte[]>) then
                            Error $"{t.Name}.{c.Name}.{p.Name} is a {Reflection.typeName ft}; a case field must be a scalar or a union to be stored in columns"
                        else
                            match scalarMapping scalars inner with
                            | Ok mapping -> Ok(Leaf(scalarField p.Name 0 partColumn ft mapping))
                            | Error reason when FSharpType.IsUnion(inner, true) && not (Reflection.isOption ft) ->
                                match Conventions.tryFor inner with
                                | Some(Error r) -> Error r
                                | _ -> unionLayout scalars inner partColumn |> Result.map (fun n -> Nested(p, n))
                            | Error reason -> Error $"{t.Name}.{c.Name}.{p.Name}: {reason}")

                match parts |> List.tryPick (function Error e -> Some e | _ -> None) with
                | Some e -> Error e
                | None ->
                    Ok
                        { Case = c
                          Stored = Paths.snakeCase c.Name
                          Parts = parts |> List.map (function Ok p -> p | Error _ -> failwith "unreachable")
                          Make = FSharpValue.PreComputeUnionConstructor(c, true)
                          Read = FSharpValue.PreComputeUnionReader(c, true) }

            let layouts = cases |> Array.map caseLayout

            match layouts |> Array.tryPick (function Error e -> Some e | _ -> None) with
            | Some e -> Error e
            | None ->
                let cs = layouts |> Array.map (function Ok c -> c | Error _ -> failwith "unreachable")

                let partColumns =
                    cs
                    |> Array.toList
                    |> List.collect (fun c ->
                        c.Parts
                        |> List.collect (function
                            | Leaf f ->
                                [ { Name = f.Column
                                    ProviderType = f.ProviderType
                                    Mapping = f.Mapping } ]
                            | Nested(_, n) -> n.Columns))

                // Case fields of the same name share a column, if their types agree.
                let clash =
                    partColumns
                    |> List.groupBy (fun p -> p.Name)
                    |> List.tryFind (fun (_, ps) -> ps |> List.map (fun p -> p.ProviderType) |> List.distinct |> List.length > 1)

                match clash with
                | Some(name, _) -> Error $"two cases of {t.Name} have a field stored in {name} with different types; rename one"
                | None ->
                    Ok
                        { Type = t
                          TagColumn = column
                          Cases = cs
                          Columns =
                            { Name = column
                              ProviderType = typeof<string>
                              Mapping = None }
                            :: (partColumns |> List.distinctBy (fun p -> p.Name))
                          Tag = FSharpValue.PreComputeUnionTagReader(t, true) }

    let private resolveSchema
        (scalars: IReadOnlyDictionary<Type, ScalarMapping>)
        (defs: IReadOnlyDictionary<Type, SchemaDef>)
        (def: SchemaDef)
        : Result<SchemaInfo, ConfigError list> =
        let errors = ResizeArray<ConfigError>()
        let model = def.Type.Name
        let err prop msg = errors.Add { Model = model; Property = prop; Message = msg }

        if not (FSharpType.IsRecord(def.Type, true)) then
            err None "only F# record types can be mapped"
            Error(List.ofSeq errors)
        else
            let shape = Reflection.recordShape def.Type
            let assocDefs = def.Assocs |> List.map (fun a -> a.Property.Name, a) |> Map.ofList

            let keyName, keyGenerated =
                match def.Key with
                | Some(p, g) -> Some p.Name, g
                | None ->
                    err None "no primary key declared (use key or identityKey)"
                    None, false

            let fields = ResizeArray<FieldMap>()
            let assocs = ResizeArray<AssocMap>()

            for i, prop in Array.indexed shape.Fields do
                let t = prop.PropertyType

                let owned = assocDefs.TryFind prop.Name |> Option.exists (fun a -> a.Owned || a.Linked)

                if Reflection.isAssoc t || owned then
                    match assocDefs.TryFind prop.Name with
                    | None ->
                        err (Some prop.Name) "relationship field has no hasMany, hasOne, belongsTo or manyToMany declaration"
                    | Some a ->
                        // An owned list is its own payload: a plain list.
                        let payload = if a.Owned || a.Linked then t else Reflection.elementType t

                        let cardinality, target =
                            if Reflection.isList payload then
                                Many, Reflection.elementType payload
                            elif Reflection.isOption payload then
                                OptionalOne, Reflection.elementType payload
                            else
                                RequiredOne, payload

                        let kindOk =
                            match a.Kind, cardinality with
                            | AssocKind.HasMany, Many -> true
                            | AssocKind.HasOne, OptionalOne -> true
                            | AssocKind.HasOne, RequiredOne when a.Owned -> true
                            | AssocKind.BelongsTo, (OptionalOne | RequiredOne) -> true
                            | AssocKind.ManyToMany, Many -> true
                            | _ -> false

                        if not kindOk then
                            err (Some prop.Name) $"{a.Kind} cannot be represented by {Reflection.typeName t}"

                        match defs.TryGetValue target with
                        | false, _ ->
                            err (Some prop.Name) $"relationship target {target.Name} has no schema in this model"
                        | true, targetDef ->
                            // Validate the foreign key's type against the referenced key.
                            let fkOwner, refDef =
                                match a.Kind with
                                | AssocKind.BelongsTo -> def, targetDef
                                | AssocKind.HasMany
                                | AssocKind.HasOne
                                | AssocKind.ManyToMany -> targetDef, def

                            if a.Kind = AssocKind.ManyToMany then
                                match a.Through with
                                | Some j when String.IsNullOrWhiteSpace j.Table || String.IsNullOrWhiteSpace j.OwnerColumn || String.IsNullOrWhiteSpace j.TargetColumn ->
                                    err (Some prop.Name) "manyToMany needs a join table and both of its key columns"
                                | _ -> ()
                            elif a.ForeignKey.DeclaringType <> fkOwner.Type then
                                err
                                    (Some prop.Name)
                                    $"foreign key must be a field of {fkOwner.Type.Name}, not {a.ForeignKey.DeclaringType.Name}"
                            else
                                match refDef.Key with
                                | Some(refKey, _) ->
                                    let fkType = a.ForeignKey.PropertyType

                                    let fkInner, fkOptional =
                                        if Reflection.isOption fkType then
                                            Reflection.elementType fkType, true
                                        else
                                            fkType, false

                                    if fkInner <> refKey.PropertyType then
                                        err
                                            (Some prop.Name)
                                            $"foreign key {fkOwner.Type.Name}.{a.ForeignKey.Name} has type {Reflection.typeName fkType} but {refDef.Type.Name}.{refKey.Name} is {Reflection.typeName refKey.PropertyType}"

                                    if a.Kind = AssocKind.BelongsTo && cardinality = RequiredOne && fkOptional then
                                        err
                                            (Some prop.Name)
                                            "an optional foreign key requires Assoc<'T option> (the related record may be absent)"

                                    if a.Kind = AssocKind.BelongsTo && cardinality = OptionalOne && not fkOptional then
                                        err
                                            (Some prop.Name)
                                            "a required foreign key should use Assoc<'T> rather than Assoc<'T option>"

                                    if a.OnReplace = OnReplace.Nilify && not fkOptional then
                                        err (Some prop.Name) "OnReplace.Nilify requires an optional foreign key"
                                | None -> ()

                            assocs.Add
                                { Name = prop.Name
                                  Index = i
                                  Kind = a.Kind
                                  Cardinality = cardinality
                                  Owner = def.Type
                                  Target = target
                                  FieldType = t
                                  ForeignKey = (if isNull a.ForeignKey then "" else a.ForeignKey.Name)
                                  Through = a.Through
                                  OnReplace = a.OnReplace
                                  Ctors =
                                    if (a.Owned || a.Linked) && cardinality = Many then Reflection.listCtors t
                                    elif a.Owned then Reflection.ownedOneCtors
                                    else Reflection.assocCtors t
                                  // An owned record (owned-records.feature) is written as a
                                  // list of none or one: this rebuilds the field from it.
                                  MakeList =
                                    if a.Owned && cardinality <> Many then
                                        let some = Reflection.makeSome (typedefof<option<_>>.MakeGenericType target)
                                        fun items ->
                                            match Seq.tryHead items with
                                            | None -> null
                                            | Some x -> if cardinality = OptionalOne then some x else x
                                    else
                                        Reflection.makeList target
                                  WrapSome = Reflection.makeSome (typedefof<option<_>>.MakeGenericType target)
                                  Owned = a.Owned
                                  Linked = a.Linked
                                  Order = a.Order }
                else
                    let isOpt = Reflection.isOption t
                    let scalarType = if isOpt then Reflection.elementType t else t

                    if Reflection.isOption scalarType then
                        err (Some prop.Name) "nested options cannot be stored unambiguously"
                    elif FSharpType.IsRecord(scalarType, true) && defs.ContainsKey scalarType then
                        let op = if isOpt then "ownsOptional" else "ownsOne"

                        err
                            (Some prop.Name)
                            $"{prop.Name} is a {scalarType.Name}, a record with its own schema, so it must be owned to be stored: declare `{op} (fun x -> x.{prop.Name}) (fun item -> item.Owner)`, or hold its id instead to refer to it"
                    elif Reflection.isList t then
                        err
                            (Some prop.Name)
                            $"{prop.Name} is a list, and a list must be owned to be stored: declare `owns (fun x -> x.{prop.Name}) (fun item -> item.Owner)`, or make it Assoc<_ list> for a relationship loaded on request"
                    else
                        let column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)

                        let mapping =
                            match scalarMapping scalars scalarType with
                            | Ok m -> Ok(Choice1Of2 m)
                            | Error reason when FSharpType.IsUnion(scalarType, true) && (Conventions.tryFor scalarType).IsNone ->
                                if isOpt then
                                    Error $"an optional union with fields ({Reflection.typeName t}) is not supported; give the union a case for absence"
                                else
                                    unionLayout scalars scalarType column |> Result.map Choice2Of2
                            | Error reason -> Error reason

                        match mapping with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok(Choice2Of2 layout) ->
                            fields.Add
                                { scalarField prop.Name i column t None with
                                    ProviderType = typeof<string>
                                    Union = Some layout }
                        | Ok(Choice1Of2 mapping) ->
                            fields.Add
                                { Name = prop.Name
                                  Index = i
                                  Column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)
                                  FieldType = t
                                  ScalarType = scalarType
                                  IsOption = isOpt
                                  Generated =
                                    def.Generated.Contains prop.Name
                                    || (keyGenerated && Some prop.Name = keyName)
                                  Unique = def.Unique.Contains prop.Name
                                  References = None
                                  Mapping = mapping
                                  ProviderType =
                                    match mapping with
                                    | Some m -> m.ProviderType
                                    | None -> scalarType
                                  WrapSome = if isOpt then Reflection.makeSome t else id
                                  ReadOption =
                                    if isOpt then
                                        Reflection.getOptionValue t
                                    else
                                        fun v -> if isNull v then None else Some v
                                  Union = None }

            for a in def.Assocs do
                if not (shape.Fields |> Array.exists (fun f -> f.Name = a.Property.Name)) then
                    err (Some a.Property.Name) "declared relationship is not a field of the record"

            let fieldArr = fields.ToArray()

            let key =
                keyName |> Option.bind (fun k -> fieldArr |> Array.tryFind (fun f -> f.Name = k))

            if keyName.IsSome && key.IsNone then
                err keyName "primary key must be a mapped scalar field"

            match key with
            | Some k when k.IsOption -> err (Some k.Name) "primary key cannot be optional"
            | _ -> ()

            let version =
                def.Version |> Option.bind (fun v -> fieldArr |> Array.tryFind (fun f -> f.Name = v.Name))

            let columns = fieldArr |> Array.map (fun f -> f.Column)

            if Array.length (Array.distinct columns) <> columns.Length then
                err None "two fields map to the same column"

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                Ok
                    { Type = def.Type
                      Name = def.Type.Name
                      Table = def.Table
                      Fields = fieldArr
                      Key = key.Value
                      KeyGenerated = keyGenerated
                      Version = version
                      Assocs = assocs.ToArray()
                      Shape = shape
                      FieldCount = shape.Fields.Length }

    /// Validates schemas and scalar mappings together. Fails with every problem
    /// found, each identifying the model and property.
    let build (scalars: ScalarMapping list) (schemas: ISchema list) : Result<Model, ConfigError list> =
        let errors = ResizeArray<ConfigError>()
        let scalarDict = Dictionary<Type, ScalarMapping>()

        for s in scalars do
            if scalarDict.ContainsKey s.DomainType then
                errors.Add
                    { Model = s.DomainType.Name
                      Property = None
                      Message = "scalar mapping registered more than once" }
            elif Scalar.isBuiltIn s.DomainType then
                errors.Add
                    { Model = s.DomainType.Name
                      Property = None
                      Message = "built-in types cannot be remapped" }
            else
                scalarDict.[s.DomainType] <- s

        let defs = Dictionary<Type, SchemaDef>()

        for s in schemas do
            let d = s.Definition

            if defs.ContainsKey d.Type then
                errors.Add
                    { Model = d.Type.Name
                      Property = None
                      Message = "schema registered more than once" }
            else
                defs.[d.Type] <- d

        let resolved = Dictionary<Type, SchemaInfo>()

        for d in defs.Values do
            match resolveSchema scalarDict defs d with
            | Ok s -> resolved.[d.Type] <- s
            | Error es -> errors.AddRange es

        // A typed id that is the key type of exactly one schema references it.
        // Built-in key types (int64, Guid, ...) are never inferred.
        let keysOf =
            resolved.Values
            |> Seq.filter (fun s -> not (Scalar.isBuiltIn s.Key.ScalarType))
            |> Seq.groupBy (fun s -> s.Key.ScalarType)
            |> Seq.choose (fun (t, owners) -> match List.ofSeq owners with [ owner ] -> Some(t, owner.Type) | _ -> None)
            |> dict

        for t in List.ofSeq resolved.Keys do
            let s = resolved.[t]

            let fields =
                s.Fields
                |> Array.map (fun f ->
                    match keysOf.TryGetValue f.ScalarType with
                    | true, target when f.Name <> s.Key.Name -> { f with References = Some target }
                    | _ -> f)

            resolved.[t] <- { s with Fields = fields; Key = fields |> Array.find (fun f -> f.Name = s.Key.Name) }

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok(Model(resolved, scalarDict))

    /// Like `build` but raises `PlasmConfigurationException` listing every error.
    /// Intended for application start-up.
    let buildOrFail scalars schemas =
        match build scalars schemas with
        | Ok m -> m
        | Error es ->
            let lines = es |> List.map (fun e -> "  - " + string e) |> String.concat "\n"
            raise (PlasmConfigurationException $"Invalid persistence configuration:\n{lines}")
