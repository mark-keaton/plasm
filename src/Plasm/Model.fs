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

    // Each conversion is compiled once per type, so a value read by convention
    // costs what a registered mapping costs (no reflection per value).
    type private X = System.Linq.Expressions.Expression

    let private lambda<'R> (body: System.Linq.Expressions.ParameterExpression -> X) : Func<obj, 'R> =
        let v = X.Parameter(typeof<obj>, "v")
        X.Lambda<Func<obj, 'R>>(body v, v).Compile()

    let private boxed (e: X) : X = X.Convert(e, typeof<obj>)

    /// `fun v -> create (v :?> 'P)` read as a Result<obj, string>.
    let private compileCreate (create: MethodInfo) (p: Type) : Func<obj, Result<obj, string>> =
        let resultType = create.ReturnType
        let ok = typeof<Result<obj, string>>.GetMethod("NewOk")
        let error = typeof<Result<obj, string>>.GetMethod("NewError")

        lambda (fun v ->
            let r = X.Variable(resultType, "r")

            X.Block(
                [ r ],
                X.Assign(r, X.Call(create, X.Convert(v, p))),
                X.Condition(
                    X.Equal(X.Property(r, "Tag"), X.Constant 0),
                    X.Call(ok, boxed (X.Property(r, "ResultValue"))),
                    X.Call(error, X.Property(r, "ErrorValue"))
                )
            ))

    /// A fieldless union, stored as its case names in snake case, ordered as
    /// it is declared.
    let private fieldless (t: Type) (cases: UnionCaseInfo[]) =
        let tag = lambda<int> (fun v -> X.Property(X.Convert(v, t), "Tag"))
        let values = cases |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||], true))
        let stored = cases |> Array.map (fun c -> Paths.snakeCase c.Name)
        let byName = Array.zip stored values |> dict

        { DomainType = t
          ProviderType = typeof<string>
          ToProvider = fun v -> box stored.[tag.Invoke v]
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
        let field = case.GetFields().[0]
        let read = lambda<obj> (fun v -> boxed (X.Property(X.Convert(v, t), field)))
        let toProvider (v: obj) = read.Invoke v

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
        | [| _, create |] ->
            let create = compileCreate create p
            Ok(mapping create.Invoke $"{t.Name} as {p.Name}, read through {t.Name}.create")
        | [||] when not (FSharpType.IsUnion(t, false)) ->
            Error
                $"type {t.Name} has a private representation and no create function, so Plasm cannot read it without bypassing the domain; give it `static member create: {p.Name} -> Result<{t.Name}, string>`, or register Scalar.tryWrap"
        | [||] ->
            let ctor = FSharpValue.PreComputeUnionConstructorInfo(case, true)
            let make = lambda<obj> (fun v -> boxed (X.Call(ctor, X.Convert(v, p))))
            Ok(mapping (fun v -> Ok(make.Invoke v)) $"{t.Name} as {p.Name}, by convention")
        | several ->
            let names = several |> Array.map fst |> String.concat " and "
            Error $"type {t.Name} has more than one create function ({names}); keep one, or register a mapping"

    /// The mapping `t` gets by convention: Some(Ok) for a single-case union over
    /// a built-in type or a fieldless union, Some(Error) for one Plasm must not
    /// read, None for any other type.
    let tryFor (t: Type) : Result<ScalarMapping, string> option =
        if not (FSharpType.IsUnion(t, true)) || Reflection.isOption t || Reflection.isList t then
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

    // Made once: a lambda passed to GetOrAdd would be a new delegate per call.
    let byConvention =
        Func<Type, ScalarMapping option>(fun t ->
            match Conventions.tryFor t with
            | Some(Ok m) -> Some m
            | _ -> None)

    /// A type's mapping: registered, else by convention; (false, _) when none.
    let tryScalar (t: Type) =
        match registered.TryGetValue t with
        | true, m -> true, m
        | _ ->
            match found.GetOrAdd(t, byConvention) with
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
    /// the domain type `t`, which may be an option. A provider value converts
    /// only when the type holds it exactly (Reflection.exactly).
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
            let convert (target: Type) (v: obj) = Reflection.exactly target v

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
          Union = None
          Value = None
          Json = None }

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

    /// A value (value-records.feature): a record with no table and no Id,
    /// stored in its owner's row as `<column>_<field>` columns.
    let rec private valueLayout (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (owner: string) (t: Type) (column: string) (optional: bool) : Result<ValueLayout, string> =
        let props = FSharpType.GetRecordFields(t, true)

        if props |> Array.exists (fun p -> p.Name = "Id") then
            Error $"is a {t.Name}, which looks like a stored record without its table (it has an Id): give {t.Name} a [<Table>] to store it on its own, or remove its Id to store it in {owner}'s row"
        else
            let field (i: int) (p: Reflection.PropertyInfo) : Result<FieldMap, string> =
                let ft = p.PropertyType
                let inner = if Reflection.isOption ft then Reflection.elementType ft else ft
                let partColumn = column + "_" + Paths.snakeCase p.Name
                let has (a: Type) = p.GetCustomAttributes(a, true).Length > 0

                let unread =
                    [ "Unique", typeof<UniqueAttribute>
                      "Json", typeof<JsonAttribute>
                      "Version", typeof<VersionAttribute>
                      "Generated", typeof<GeneratedAttribute>
                      "Assigned", typeof<AssignedAttribute>
                      "NotReference", typeof<NotReferenceAttribute>
                      "OrderedBy", typeof<OrderedByAttribute>
                      "JoinTable", typeof<JoinTableAttribute> ]
                    |> List.tryFind (snd >> has)

                match unread with
                | Some(name, _) -> Error $"{t.Name}.{p.Name} is marked {name}, but {name} is not supported inside a value"
                | None when has typeof<ColumnAttribute> ->
                    Error $"{t.Name}.{p.Name} names its column, but Column is not supported inside a value (its column is {partColumn})"
                | None when Reflection.isList ft || (inner.IsArray && inner <> typeof<byte[]>) ->
                    Error $"{t.Name}.{p.Name} is a {Reflection.typeName ft}: a value cannot hold a list"
                | None when Derive.isStored inner ->
                    Error $"{t.Name}.{p.Name} is a {inner.Name}, a stored record: a value cannot hold a stored record (hold its id instead)"
                | None ->
                    match scalarMapping scalars inner with
                    | Ok mapping -> Ok { scalarField p.Name i partColumn ft mapping with Index = i }
                    | Error _ when FSharpType.IsRecord(inner, true) ->
                        valueLayout scalars owner inner partColumn (Reflection.isOption ft)
                        |> Result.map (fun v ->
                            { scalarField p.Name i partColumn ft None with
                                Index = i
                                Value = Some v })
                    | Error reason -> Error $"{t.Name}.{p.Name}: {reason}"

            let fields = props |> Array.mapi field |> List.ofArray

            match fields |> List.tryPick (function Error e -> Some e | _ -> None) with
            | Some e -> Error e
            | None ->
                let fs = fields |> List.map (function Ok f -> f | Error _ -> failwith "unreachable")

                if optional && fs |> List.forall (fun f -> f.IsOption) then
                    Error $"is an optional {t.Name}, which needs a field that is not optional: with every field optional, a {t.Name} with nothing in it could not be told from None"
                else
                    Ok
                        { Type = t
                          Fields = fs
                          Make = FSharpValue.PreComputeRecordConstructor(t, true)
                          Read = FSharpValue.PreComputeRecordReader(t, true) }

    /// How a [<Json>] field's type is a document (json-columns.feature).
    let rec private jsonShape (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (t: Type) : Result<JsonShape, string> =
        if Reflection.isList t then
            let e = Reflection.elementType t
            jsonShape scalars e |> Result.map (fun s -> JList(s, e))
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
            Error "Map is not supported in a document"
        elif Reflection.isOption t then
            Error $"{Reflection.typeName t} nests an option, which a document cannot store unambiguously"
        elif Derive.isStored t then
            Error $"{t.Name} is a stored record, which cannot be part of a document: hold its id instead"
        elif FSharpType.IsRecord(t, true) && (scalarMapping scalars t |> Result.isError) then
            let props = FSharpType.GetRecordFields(t, true)

            if props |> Array.exists (fun p -> p.Name = "Id") then
                Error $"{t.Name} looks like a stored record (it has an Id): give it a [<Table>] and hold its id, or remove its Id"
            else
                let keys =
                    props
                    |> Array.toList
                    |> List.map (fun p ->
                        let ft = p.PropertyType
                        let optional = Reflection.isOption ft
                        let inner = if optional then Reflection.elementType ft else ft

                        jsonShape scalars inner
                        |> Result.mapError (fun e -> $"{t.Name}.{p.Name}: {e}")
                        |> Result.map (fun shape ->
                            { Field = p.Name
                              Key = Paths.snakeCase p.Name
                              Optional = optional
                              Shape = shape
                              ReadOption = if optional then Reflection.getOptionValue ft else (fun v -> Some v)
                              WrapSome = if optional then Reflection.makeSome ft else id }))

                match keys |> List.tryPick (function Error e -> Some e | _ -> None) with
                | Some e -> Error e
                | None ->
                    Ok(
                        JRecord(
                            t,
                            keys |> List.map (function Ok k -> k | Error _ -> failwith "unreachable"),
                            FSharpValue.PreComputeRecordConstructor(t, true),
                            FSharpValue.PreComputeRecordReader(t, true)
                        )
                    )
        else
            match scalarMapping scalars t with
            | Ok mapping ->
                let provider = mapping |> Option.map (fun m -> m.ProviderType) |> Option.defaultValue t

                if JsonDoc.supports provider then
                    Ok(JScalar(provider, mapping))
                else
                    Error $"a document cannot hold a {Reflection.typeName t}"
            | Error _ when FSharpType.IsUnion(t, true) ->
                Error $"{t.Name} is a union with fields, which a document cannot hold yet; give it no fields, or store it outside the document"
            | Error reason -> Error reason

    /// A [<Json>] field: one jsonb column holding its value as a document,
    /// sent as text cast to jsonb and read back as text.
    let private jsonField (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (prop: Reflection.PropertyInfo) (i: int) (column: string) : Result<FieldMap, string> =
        let t = prop.PropertyType
        let inner = if Reflection.isOption t then Reflection.elementType t else t

        match jsonShape scalars inner with
        | Error e -> Error e
        | Ok(JScalar _) ->
            Error $"is a {Reflection.typeName t}: a scalar is stored in its own column; [<Json>] is for a record, a list, or an option of these"
        | Ok shape ->
            let mapping =
                { DomainType = inner
                  ProviderType = typeof<string>
                  ToProvider = fun v -> box ((JsonDoc.write shape v).ToJsonString())
                  FromProvider =
                    fun raw ->
                        let parsed =
                            try
                                Ok(System.Text.Json.Nodes.JsonNode.Parse(string raw))
                            with ex ->
                                Error $"{prop.Name}: the column does not hold JSON ({ex.Message})"

                        parsed |> Result.bind (JsonDoc.read shape prop.Name)
                  Ordered = None
                  Cases = []
                  StoreType = Some [ "jsonb" ]
                  Description = $"{Reflection.typeName inner} as a jsonb document" }

            Ok
                { scalarField prop.Name i column t (Some mapping) with
                    Json = Some shape }

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
                    err None "has no field named Id, and a stored record's key is its field named Id"
                    None, false

            let fields = ResizeArray<FieldMap>()
            let assocs = ResizeArray<AssocMap>()

            for i, prop in Array.indexed shape.Fields do
                let t = prop.PropertyType

                match assocDefs.TryFind prop.Name with
                | Some a ->
                    do
                        // An owned or linked field is its own payload: a plain list,
                        // an option or a record.
                        let payload = t

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
                                targetDef, def

                            if a.Kind = AssocKind.ManyToMany then
                                match a.Through with
                                | Some j when String.IsNullOrWhiteSpace j.Table || String.IsNullOrWhiteSpace j.OwnerColumn || String.IsNullOrWhiteSpace j.TargetColumn ->
                                    err (Some prop.Name) "links needs a join table and both of its key columns"
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

                                    ignore fkOptional
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
                                  Ctors =
                                    if (a.Owned || a.Linked) && cardinality = Many then Reflection.listCtors t
                                    else Reflection.ownedOneCtors
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
                | None ->
                    let isOpt = Reflection.isOption t
                    let scalarType = if isOpt then Reflection.elementType t else t

                    if prop.GetCustomAttributes(typeof<JsonAttribute>, true).Length > 0 then
                        let column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)

                        match jsonField scalars prop i column with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok field -> fields.Add field
                    elif Reflection.isOption scalarType then
                        err (Some prop.Name) "nested options cannot be stored unambiguously"
                    elif FSharpType.IsRecord(scalarType, true) && defs.ContainsKey scalarType then
                        err
                            (Some prop.Name)
                            $"{prop.Name} is a {scalarType.Name}, a stored record: to own it, give {scalarType.Name} a field holding {def.Type.Name}'s key; to refer to it, hold its id instead"
                    elif FSharpType.IsRecord(scalarType, true) && (scalarMapping scalars scalarType |> Result.isError) then
                        let column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)

                        for name, marked in [ "Unique", def.Unique.Contains prop.Name; "Generated", def.Generated.Contains prop.Name ] do
                            if marked then
                                err (Some prop.Name) $"is marked {name}, but a value is stored in several columns: {name} is read only on one column"

                        match valueLayout scalars def.Type.Name scalarType column isOpt with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok layout ->
                            fields.Add
                                { scalarField prop.Name i column t None with
                                    Value = Some layout }
                    elif Reflection.isList t then
                        err
                            (Some prop.Name)
                            $"{prop.Name} is a {Reflection.typeName t}: a list is stored as owned or linked records, or as one document; mark it [<Json>] to store it as one JSON document"
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
                            for name, marked in [ "Unique", def.Unique.Contains prop.Name; "Generated", def.Generated.Contains prop.Name ] do
                                if marked then
                                    err (Some prop.Name) $"is marked {name}, but a union with fields is stored in several columns: {name} is read only on one column"

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
                                  Union = None
                                  Value = None
                                  Json = None }

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

            for column, sharing in fieldArr |> Array.groupBy (fun f -> f.Column) do
                if sharing.Length > 1 then
                    let names = sharing |> Array.map (fun f -> f.Name) |> String.concat " and "
                    err None $"{names} both map to the column {column}; give each field its own column"

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
    let internal buildSchemas (scalars: ScalarMapping list) (schemas: ISchema list) : Result<Model, ConfigError list> =
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

        // A typed id references the schema whose key it is. Built-in key types
        // (int64, Guid, ...) are never inferred. A typed id that is the key of
        // two schemas would make every field of that type ambiguous, so the
        // model is refused rather than left without the reference.
        let keyOwners =
            resolved.Values
            |> Seq.filter (fun s -> not (Scalar.isBuiltIn s.Key.ScalarType))
            |> Seq.groupBy (fun s -> s.Key.ScalarType)
            |> Seq.map (fun (t, owners) -> t, owners |> Seq.map (fun o -> o.Type) |> Seq.sortBy (fun o -> o.Name) |> List.ofSeq)
            |> List.ofSeq

        for t, owners in keyOwners do
            match owners with
            | _ :: _ :: _ ->
                let names = owners |> List.map (fun o -> o.Name) |> String.concat " and "

                errors.Add
                    { Model = t.Name
                      Property = None
                      Message = $"is the key type of {names}; a typed id must be the key of one schema, so each field of type {t.Name} names one table. Give each schema its own key type" }
            | _ -> ()

        let keysOf =
            keyOwners
            |> List.choose (fun (t, owners) -> match owners with [ owner ] -> Some(t, owner) | _ -> None)
            |> dict

        for t in List.ofSeq resolved.Keys do
            let s = resolved.[t]

            for f in s.Fields do
                if defs.[t].NotReference.Contains f.Name && not (keysOf.ContainsKey f.ScalarType) then
                    let name = Reflection.fsharpName f.ScalarType
                    let article = if "AEIOUaeiou".Contains name.[0] then "an" else "a"

                    errors.Add
                        { Model = t.Name
                          Property = Some f.Name
                          Message = $"is marked NotReference, but {article} {name} refers to nothing already: only a field of a record's key type (a typed id) is a reference" }

            let fields =
                s.Fields
                |> Array.map (fun f ->
                    match keysOf.TryGetValue f.ScalarType with
                    | true, target when f.Name <> s.Key.Name && not (defs.[t].NotReference.Contains f.Name) -> { f with References = Some target }
                    | _ -> f)

            resolved.[t] <- { s with Fields = fields; Key = fields |> Array.find (fun f -> f.Name = s.Key.Name) }

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok(Model(resolved, scalarDict))

    /// The model of the records `types` lists and the records they own
    /// (stored-records.feature). Each says how it is stored: its [<Table>],
    /// its field named Id, and the attributes on its fields; the rest follows
    /// from the types. `scalars` overrides a convention for a scalar type.
    /// Fails with every problem found, each naming the record and field.
    let build (scalars: ScalarMapping list) (types: Type list) : Result<Model, ConfigError list> =
        match Derive.model types with
        | Error es -> Error es
        | Ok defs -> buildSchemas scalars [ for d in defs -> { new ISchema with member _.Definition = d } ]

    let private orFail result =
        match result with
        | Ok m -> m
        | Error es ->
            let lines = es |> List.map (fun e -> "  - " + string e) |> String.concat "\n"
            raise (PlasmConfigurationException $"Invalid persistence configuration:\n{lines}")

    /// Like `build` but raises `PlasmConfigurationException` listing every error.
    /// Intended for application start-up.
    let buildOrFail scalars (types: Type list) = orFail (build scalars types)
