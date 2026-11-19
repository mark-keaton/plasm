namespace Plasm

open System
open System.Collections.Generic

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
    let private fieldless (t: Type) (u: UnionShape) =
        let cases = u.Cases
        let values = cases |> Array.map (fun c -> c.Make [||])
        let stored = cases |> Array.map (fun c -> Paths.snakeCase c.Name)
        let byName = Array.zip stored values |> dict

        { DomainType = t
          ProviderType = typeof<string>
          ToProvider = fun v -> box stored.[u.Tag v]
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
    let private wrapper (t: Type) (u: UnionShape) (p: Type) : Result<ScalarMapping, string> =
        let case = u.Cases.[0]
        let toProvider (v: obj) = case.Unwrap v

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
        | [||] when u.Private ->
            Error
                $"type {t.Name} has a private representation and no create function, so Plasm cannot read it without bypassing the domain; give it `static member create: {p.Name} -> Result<{t.Name}, string>`, or register Scalar.tryWrap"
        | [||] -> Ok(mapping (fun v -> Ok(case.Wrap v)) $"{t.Name} as {p.Name}, by convention")
        | several ->
            let names = several |> Array.map fst |> String.concat " and "
            Error $"type {t.Name} has more than one create function ({names}); keep one, or register a mapping"

    /// The mapping `t` gets by convention: Some(Ok) for a single-case union over
    /// a built-in type or a fieldless union, Some(Error) for one Plasm must not
    /// read, None for any other type.
    let tryFor (t: Type) : Result<ScalarMapping, string> option =
        if not (Shape.isUnion t) || Reflection.isOption t || Reflection.isList t then
            None
        else
            let u = Shape.union t
            let cases = u.Cases

            if cases |> Array.forall (fun c -> c.Fields.Length = 0) then
                Some(Ok(fieldless t u))
            elif cases.Length = 1 && cases.[0].Fields.Length = 1 && Scalar.isBuiltIn (snd cases.[0].Fields.[0]) then
                Some(wrapper t u (snd cases.[0].Fields.[0]))
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
          Json = None
          Array = None }

    /// A union with fields (one no scalar mapping or convention covers), stored
    /// across columns (union-fields.feature).
    let rec private unionLayout (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (t: Type) (column: string) : Result<UnionLayout, string> =
        let u = Shape.union t

        if u.Private then
            Error $"union {t.Name} has a private representation; Plasm would construct its cases behind the domain's back, so give it a scalar mapping instead"
        else
            let cases = u.Cases

            let caseLayout (c: UnionCaseShape) : Result<UnionCaseLayout, string> =
                let parts =
                    c.Fields
                    |> Array.toList
                    |> List.map (fun (fieldName, ft) ->
                        // Unnamed fields (Item1, Item2) are named after their case.
                        let name = if fieldName.StartsWith "Item" then $"{c.Name}_{fieldName}" else fieldName
                        let partColumn = column + "_" + Paths.snakeCase name
                        let inner = if Reflection.isOption ft then Reflection.elementType ft else ft

                        if Reflection.isList ft || Shape.isRecord inner || (inner.IsArray && inner <> typeof<byte[]>) then
                            Error $"{t.Name}.{c.Name}.{fieldName} is a {Reflection.typeName ft}; a case field must be a scalar or a union to be stored in columns"
                        else
                            match scalarMapping scalars inner with
                            | Ok mapping -> Ok(Leaf(scalarField fieldName 0 partColumn ft mapping))
                            | Error reason when Shape.isUnion inner && not (Reflection.isOption ft) ->
                                match Conventions.tryFor inner with
                                | Some(Error r) -> Error r
                                | _ -> unionLayout scalars inner partColumn |> Result.map (fun n -> Nested(fieldName, n))
                            | Error reason -> Error $"{t.Name}.{c.Name}.{fieldName}: {reason}")

                match parts |> List.tryPick (function Error e -> Some e | _ -> None) with
                | Some e -> Error e
                | None ->
                    Ok
                        { Case = c
                          Stored = Paths.snakeCase c.Name
                          Parts = parts |> List.map (function Ok p -> p | Error _ -> failwith "unreachable")
                          Make = c.Make
                          Read = c.Read }

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
                          Tag = u.Tag }

    /// A value (value-records.feature): a record with no table and no Id,
    /// stored in its owner's row as `<column>_<field>` columns.
    let rec private valueLayout (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (owner: string) (t: Type) (column: string) (optional: bool) : Result<ValueLayout, string> =
        let shape = Shape.record t
        let props = shape.Fields

        if props |> Array.exists (fun p -> p.Name = "Id") then
            Error $"is a {t.Name}, which looks like a stored record without its table (it has an Id): give {t.Name} a [<Table>] to store it on its own, or remove its Id to store it in {owner}'s row"
        else
            let field (i: int) (p: Reflection.PropertyInfo) : Result<FieldMap, string> =
                let ft = p.PropertyType
                let inner = if Reflection.isOption ft then Reflection.elementType ft else ft
                // A key's parts are the table's own columns (composite-keys.feature):
                // their layout has no prefix.
                let partColumn = if column = "" then Paths.snakeCase p.Name else column + "_" + Paths.snakeCase p.Name
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
                    | Error _ when Shape.isRecord inner ->
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
                          Make = shape.Construct
                          Read = shape.Deconstruct }

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
        elif Shape.isRecord t && (scalarMapping scalars t |> Result.isError) then
            let shape = Shape.record t
            let props = shape.Fields

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
                            shape.Construct,
                            shape.Deconstruct
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
            | Error _ when Shape.isUnion t ->
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

    /// The key record of a key of several parts, and the schema it keys: a
    /// record type that is the type of a stored record's Id and names no table.
    let private compositeKeyOf (defs: IReadOnlyDictionary<Type, SchemaDef>) (t: Type) : SchemaDef option =
        if not (Shape.isRecord t) || defs.ContainsKey t then
            None
        else
            defs.Values |> Seq.tryFind (fun d -> match d.Key with Some(k, _) -> k.PropertyType = t | None -> false)

    /// The layout `layout` with its columns, leaf by leaf in order, named
    /// `names` (composite-references.feature).
    let rec private renamed (layout: ValueLayout) (names: string list) : ValueLayout * string list =
        let fields, rest =
            layout.Fields
            |> List.mapFold
                (fun (left: string list) (f: FieldMap) ->
                    match f.Value with
                    | Some v ->
                        let v', left' = renamed v left
                        { f with Value = Some v' }, left'
                    | None -> { f with Column = List.head left }, List.tail left)
                names

        { layout with Fields = fields }, rest

    /// A key of several parts (composite-keys.feature, composite-references.feature):
    /// the field named Id is a record with no table, whose fields are the key's
    /// parts, none optional, each a scalar or another record's key (then a
    /// reference unless marked [<NotReference>]). A part's columns are the
    /// table's own, by their own names, or as [<Column>] names them.
    let rec private keyLayout (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (defs: IReadOnlyDictionary<Type, SchemaDef>) (owner: string) (t: Type) : Result<ValueLayout, string> =
        let shape = Shape.record t
        let props = shape.Fields
        let name = Reflection.typeName

        if props.Length < 2 then
            let p = props.[0]
            Error $"is a {t.Name} of one part ({p.Name}): a key of one part is a single-case wrapper (type {t.Name} = {t.Name} of {name p.PropertyType})"
        else
            let part (i: int) (p: Reflection.PropertyInfo) : Result<FieldMap, string> =
                let ft = p.PropertyType
                let at = $"{t.Name}.{p.Name}"
                let has (a: Type) = p.GetCustomAttributes(a, true).Length > 0

                let names =
                    match p.GetCustomAttributes(typeof<ColumnAttribute>, true) with
                    | [| :? ColumnAttribute as c |] -> Some(List.ofArray c.Names)
                    | _ -> None

                let unread =
                    [ "Unique", typeof<UniqueAttribute>
                      "Json", typeof<JsonAttribute>
                      "Version", typeof<VersionAttribute>
                      "Generated", typeof<GeneratedAttribute>
                      "Assigned", typeof<AssignedAttribute> ]
                    |> List.tryFind (snd >> has)

                match unread with
                | Some(attribute, _) -> Error $"{at} is marked {attribute}, but {attribute} is not supported inside a key"
                | None when Reflection.isOption ft -> Error $"{at} is a {name ft}: a part of a key cannot be optional"
                | None ->
                    match compositeKeyOf defs ft with
                    | Some keyed ->
                        keyLayout scalars defs owner ft
                        |> Result.bind (fun nested ->
                            let wanted = nested.Columns.Length

                            match names with
                            | Some ns when ns.Length <> wanted ->
                                let plural = if ns.Length = 1 then "" else "s"
                                Error $"{at} names {ns.Length} column{plural} for a key of {wanted} parts"
                            | Some ns -> Ok(fst (renamed nested ns))
                            | None -> Ok nested)
                        |> Result.map (fun nested ->
                            { scalarField p.Name i "" ft None with
                                Index = i
                                Value = Some nested
                                References = if has typeof<NotReferenceAttribute> then None else Some keyed.Type })
                    | None when has typeof<NotReferenceAttribute> ->
                        Error $"{at} is marked NotReference, but only a part that is another record's key refers to a row"
                    | None when Reflection.isList ft || Result.isError (scalarMapping scalars ft) ->
                        Error $"{at} is a {name ft}: a part of a key is a scalar, or another record's key"
                    | None ->
                        match names, scalarMapping scalars ft with
                        | Some ns, _ when ns.Length <> 1 -> Error $"{at} is one column, and Column names one"
                        | _, Ok mapping ->
                            let column = match names with Some [ n ] -> n | _ -> Paths.snakeCase p.Name
                            Ok { scalarField p.Name i column ft mapping with Index = i }
                        | _, Error reason -> Error $"{at}: {reason}"

            let parts = props |> Array.mapi part |> List.ofArray

            match parts |> List.tryPick (function Error e -> Some e | _ -> None) with
            | Some e -> Error e
            | None ->
                let fs = parts |> List.map (function Ok f -> f | Error _ -> failwith "unreachable")
                let layout = { Type = t; Fields = fs; Make = shape.Construct; Read = shape.Deconstruct }

                // Two parts in the same columns would read one value twice.
                let owners =
                    fs |> List.collect (fun f -> f.Parts |> List.map (fun c -> c.Name, f.Name))

                match owners |> List.groupBy fst |> List.tryFind (fun (_, ps) -> (ps |> List.map snd |> List.distinct).Length > 1) with
                | Some(column, ps) ->
                    let both = ps |> List.map (fun (_, f) -> $"{t.Name}.{f}") |> List.distinct |> String.concat " and "
                    Error $"{both} both map to the column {column}; name one's columns with [<Column>]"
                | None -> Ok layout

    /// The provider types an array column can hold, each with its array type
    /// and a maker of that array from boxed elements: a closed list, so that
    /// no array type is made at run time (a native binary has only those its
    /// code names).
    let internal arrayOf (provider: Type) : (Type * (obj list -> obj)) option =
        let typed (f: obj -> 'a) = Some(typeof<'a[]>, fun (items: obj list) -> box (items |> List.map f |> Array.ofList))

        if provider = typeof<string> then typed unbox<string>
        elif provider = typeof<int16> then typed unbox<int16>
        elif provider = typeof<int> then typed unbox<int>
        elif provider = typeof<int64> then typed unbox<int64>
        elif provider = typeof<bool> then typed unbox<bool>
        elif provider = typeof<decimal> then typed unbox<decimal>
        elif provider = typeof<float> then typed unbox<float>
        elif provider = typeof<float32> then typed unbox<float32>
        elif provider = typeof<Guid> then typed unbox<Guid>
        elif provider = typeof<DateTime> then typed unbox<DateTime>
        elif provider = typeof<DateTimeOffset> then typed unbox<DateTimeOffset>
        elif provider = typeof<DateOnly> then typed unbox<DateOnly>
        elif provider = typeof<TimeOnly> then typed unbox<TimeOnly>
        elif provider = typeof<TimeSpan> then typed unbox<TimeSpan>
        else None

    /// A list of scalars (array-columns.feature): one array column of the
    /// elements' provider type, each element read and written as its own
    /// column would be. Never NULL: an empty list is an empty array.
    let private arrayField (scalars: IReadOnlyDictionary<Type, ScalarMapping>) (prop: Reflection.PropertyInfo) (i: int) (column: string) : Result<FieldMap, string> =
        let t = prop.PropertyType
        let e = Reflection.elementType t
        let name = Reflection.typeName t
        let asDocument = "mark it [<Json>] to store it as one document"

        if Reflection.isOption e then
            Error $"is a {name}: an element of an array cannot be absent; leave it out of the list instead"
        elif Reflection.isList e || (e.IsArray && e <> typeof<byte[]>) then
            Error $"is a {name}: an array column holds scalars, not lists; {asDocument}"
        else
            match scalarMapping scalars e with
            | Error _ -> Error $"is a {name}: its elements are not scalars stored in one column; {asDocument}"
            | Ok(Some m) when m.StoreType.IsSome ->
                let stored = String.concat "." m.StoreType.Value
                Error $"is a {name}: an array of a type stored as {stored} is not supported; store {e.Name} as text, or {asDocument}"
            | Ok mapping ->
                let provider = mapping |> Option.map (fun m -> m.ProviderType) |> Option.defaultValue e

                match arrayOf provider with
                | None -> Error $"is a {name}: an array of {provider.Name} is not supported; {asDocument}"
                | Some(arrayType, make) ->
                    let toProvider = mapping |> Option.map (fun m -> m.ToProvider) |> Option.defaultValue id
                    let fromProvider = mapping |> Option.map (fun m -> m.FromProvider) |> Option.defaultValue Ok
                    let items = Reflection.listItems e
                    let makeList = Reflection.makeList e

                    let read (raw: obj) : Result<obj, string> =
                        match raw with
                        | :? Array as a ->
                            let read = ResizeArray<obj>(a.Length)
                            let mutable problem = None
                            let mutable j = 0

                            while problem.IsNone && j < a.Length do
                                match a.GetValue j with
                                | x when isNull x || x = box DBNull.Value ->
                                    problem <- Some $"{prop.Name}[{j}] is NULL, but an element of {name} cannot be absent"
                                | x ->
                                    match fromProvider x with
                                    | Ok v -> read.Add v
                                    | Error m -> problem <- Some $"{prop.Name}[{j}]: {m}"

                                j <- j + 1

                            match problem with
                            | Some m -> Error m
                            | None -> Ok(makeList read)
                        | other -> Error $"{prop.Name}: the column holds {other.GetType().Name}, not an array"

                    let mapping =
                        { DomainType = t
                          ProviderType = arrayType
                          ToProvider = fun v -> make [ for x in items v -> toProvider x ]
                          FromProvider = read
                          Ordered = None
                          Cases = []
                          StoreType = None
                          Description = $"{name} as an array of {provider.Name}" }

                    Ok
                        { scalarField prop.Name i column t (Some mapping) with
                            Array = Some e }

    let private resolveSchema
        (scalars: IReadOnlyDictionary<Type, ScalarMapping>)
        (defs: IReadOnlyDictionary<Type, SchemaDef>)
        (def: SchemaDef)
        : Result<SchemaInfo, ConfigError list> =
        let errors = ResizeArray<ConfigError>()
        let model = def.Type.Name
        let err prop msg = errors.Add { Model = model; Property = prop; Message = msg }

        if not (Shape.isRecord def.Type) then
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

            // A key of several parts (composite-keys.feature): what depends on
            // how other records refer to one is refused until it is built.
            let composite (d: SchemaDef) =
                match d.Key with
                | Some(k, _) -> Shape.isRecord k.PropertyType && not (defs.ContainsKey k.PropertyType)
                | None -> false


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

                        let verb = if a.Linked then "links" else "owns"

                        if composite def && a.Linked then
                            err (Some prop.Name) $"links {target.Name}, but its key has several parts: a linked list under such a key is not supported yet"
                        else
                            match defs.TryGetValue target with
                            | true, td when composite td ->
                                err (Some prop.Name) $"{verb} {target.Name}, whose key has several parts: an owned or linked list of such records is not supported yet"
                            | _ -> ()

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
                    elif Shape.isRecord scalarType && defs.ContainsKey scalarType then
                        err
                            (Some prop.Name)
                            $"{prop.Name} is a {scalarType.Name}, a stored record: to own it, give {scalarType.Name} a field holding {def.Type.Name}'s key; to refer to it, hold its id instead"
                    elif Some prop.Name = keyName && Shape.isRecord scalarType && not isOpt && (scalarMapping scalars scalarType |> Result.isError) then
                        if def.Generated.Contains prop.Name then
                            err (Some prop.Name) "is marked Generated, but a key of several parts is given by the insert: no part is generated"

                        match keyLayout scalars defs def.Type.Name scalarType with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok layout -> fields.Add { scalarField prop.Name i "" t None with Value = Some layout }
                    // A reference to a key of several parts (composite-references.feature):
                    // a column per part, the field's column followed by the part's,
                    // or as [<Column>] names them in the key record's order.
                    elif (compositeKeyOf defs scalarType).IsSome then
                        let keyed = (compositeKeyOf defs scalarType).Value
                        let prefix = Paths.snakeCase prop.Name

                        match keyLayout scalars defs keyed.Type.Name scalarType with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok layout ->
                            let wanted = layout.Columns.Length

                            match def.ColumnNames.TryFind prop.Name with
                            | Some ns when ns.Length <> wanted ->
                                let plural = if ns.Length = 1 then "" else "s"
                                err (Some prop.Name) $"names {ns.Length} column{plural} for a key of {wanted} parts ({keyed.Type.Name})"
                            | named ->
                                let names = named |> Option.defaultValue (layout.Columns |> List.map (fun c -> prefix + "_" + c.Name))
                                fields.Add { scalarField prop.Name i prefix t None with Value = Some(fst (renamed layout names)) }
                    elif (def.ColumnNames.TryFind prop.Name |> Option.map List.length |> Option.defaultValue 1) > 1 then
                        err (Some prop.Name) $"is one column, and Column names one; several names are for a field of a key of several parts"
                    elif Shape.isRecord scalarType && (scalarMapping scalars scalarType |> Result.isError) then
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
                    elif isOpt && Reflection.isList scalarType then
                        err
                            (Some prop.Name)
                            $"is a {Reflection.typeName t}: an empty list is already no elements, so store the {Reflection.typeName scalarType} itself"
                    elif t.IsArray && t <> typeof<byte[]> then
                        err (Some prop.Name) $"is a {t.Name}: use a list ({t.GetElementType().Name} list) for an array column"
                    // A list of scalars is one array column (array-columns.feature);
                    // a list of stored records is owned or linked, declared on the
                    // record it holds.
                    elif Reflection.isList t && not (defs.ContainsKey(Reflection.elementType t)) then
                        let column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)

                        for name, marked in [ "Unique", def.Unique.Contains prop.Name; "Generated", def.Generated.Contains prop.Name ] do
                            if marked then
                                err (Some prop.Name) $"is marked {name}, but an array column is read and written whole"

                        match arrayField scalars prop i column with
                        | Error reason -> err (Some prop.Name) reason
                        | Ok field -> fields.Add field
                    elif Reflection.isList t then
                        err
                            (Some prop.Name)
                            $"{prop.Name} is a {Reflection.typeName t}: a list is stored as owned or linked records, or as one document; mark it [<Json>] to store it as one JSON document"
                    else
                        let column = def.Columns.TryFind prop.Name |> Option.defaultValue (Paths.snakeCase prop.Name)

                        let mapping =
                            match scalarMapping scalars scalarType with
                            | Ok m -> Ok(Choice1Of2 m)
                            | Error reason when Shape.isUnion scalarType && (Conventions.tryFor scalarType).IsNone ->
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
                                  Json = None
                                  Array = None }

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

            // Every column each field is stored in: a union's, a value's or a
            // key's several, or the one.
            let stored =
                fieldArr |> Array.collect (fun f -> f.Parts |> List.map (fun p -> p.Name, f.Name) |> Array.ofList)

            for column, sharing in stored |> Array.groupBy fst do
                let owners = sharing |> Array.map snd |> Array.distinct

                if owners.Length > 1 then
                    let names = owners |> String.concat " and "
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
