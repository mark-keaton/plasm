namespace Plasm

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection

/// A validated set of schemas and scalar mappings. Construct with `Model.build`;
/// all configuration errors are reported together, before any query runs.
type Model internal (schemas: IReadOnlyDictionary<Type, SchemaInfo>, scalars: IReadOnlyDictionary<Type, ScalarMapping>) =

    member internal _.Schemas = schemas
    member internal _.Scalars = scalars

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
            match scalars.TryGetValue t with
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
                match scalars.TryGetValue t with
                | true, m -> convert m.ProviderType value |> Result.bind m.FromProvider
                | _ -> Error $"type {Reflection.typeName t} has no scalar mapping"

    member internal _.ProviderTypeOf(t: Type) =
        if Scalar.isBuiltIn t then
            Some t
        else
            match scalars.TryGetValue t with
            | true, m -> Some m.ProviderType
            | _ -> None

    /// The record types this model can persist.
    member _.Types = schemas.Keys |> Seq.toList

[<RequireQualifiedAccess>]
module Model =

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

                if Reflection.isAssoc t then
                    match assocDefs.TryFind prop.Name with
                    | None ->
                        err (Some prop.Name) "relationship field has no hasMany, hasOne, belongsTo or manyToMany declaration"
                    | Some a ->
                        let payload = Reflection.elementType t

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
                                  Ctors = Reflection.assocCtors t
                                  MakeList = Reflection.makeList target
                                  WrapSome = Reflection.makeSome (typedefof<option<_>>.MakeGenericType target) }
                else
                    let isOpt = Reflection.isOption t
                    let scalarType = if isOpt then Reflection.elementType t else t

                    if Reflection.isOption scalarType then
                        err (Some prop.Name) "nested options cannot be stored unambiguously"
                    else
                        let mapping =
                            if Scalar.isBuiltIn scalarType then
                                Ok None
                            else
                                match scalars.TryGetValue scalarType with
                                | true, m -> Ok(Some m)
                                | _ -> Error()

                        match mapping with
                        | Error() ->
                            err
                                (Some prop.Name)
                                $"type {Reflection.typeName scalarType} has no persistence mapping; register Scalar.wrap, Scalar.tryWrap or Scalar.enum"
                        | Ok mapping ->
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
                                        fun v -> if isNull v then None else Some v }

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
