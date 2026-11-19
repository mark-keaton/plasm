namespace Plasm

open System
open System.Threading.Tasks

/// The catalogue type names a column of a provider type may have: the
/// dialect's, and for an array column (array-columns.feature) its elements'
/// names followed by [], as the schema check reads an array column's type.
module internal ColumnTypes =
    let ofType (d: ISqlDialect) (t: Type) : string list =
        if t.IsArray && t <> typeof<byte[]> then
            d.ColumnTypes(t.GetElementType()) |> List.map (fun n -> n + "[]")
        else
            d.ColumnTypes t

module internal SchemaCheck =

    type private Column =
        { Name: string
          DataType: string
          /// The column type's schema and name (for an enum column, the enum type's).
          UdtSchema: string
          UdtName: string
          Nullable: bool
          HasDefault: bool }

    /// The scalar fields of a value, nested values flattened (value-records.feature).
    let rec private valueLeaves (g: FieldMap) : FieldMap list =
        match g.Value with
        | Some v -> v.Fields |> List.collect valueLeaves
        | None -> [ g ]

    let run (model: Model) (s: Execution.Session) : Task<SchemaIssue list> =
        task {
            let dialect = s.Provider.Dialect
            let schemas = model.Schemas.Values |> Seq.sortBy (fun x -> x.Name) |> List.ofSeq

            let joinTables =
                schemas
                |> List.collect (fun sc -> sc.Assocs |> Array.toList |> List.choose (fun a -> a.Through |> Option.map (fun j -> sc, a, j)))

            let tables =
                (schemas |> List.map (fun x -> x.Table)) @ (joinTables |> List.map (fun (_, _, j) -> j.Table))
                |> List.distinct
                |> Array.ofList

            let cmd: Sql.Command =
                { Sql =
                    // An array column's data_type is ARRAY: its type is read as its
                    // elements' type followed by [] (array-columns.feature).
                    "SELECT table_name, column_name, CASE WHEN data_type = 'ARRAY' THEN (SELECT format_type(t.typelem, NULL) || '[]' FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE t.typname = udt_name AND n.nspname = udt_schema) ELSE data_type END, "
                    + "is_nullable, column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER', udt_schema, udt_name "
                    + "FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ANY("
                    + dialect.ParameterName 0
                    + ")"
                  Parameters = [ box tables ] }

            let! rows =
                Execution.query s cmd (fun r ->
                    r.GetString 0,
                    { Name = r.GetString 1
                      DataType = r.GetString 2
                      UdtSchema = r.GetString 5
                      UdtName = r.GetString 6
                      Nullable = (r.GetString 3 = "YES")
                      HasDefault = r.GetBoolean 4 })

            // Fields stored as an enum type (Scalar.asPgEnum), and each type's
            // labels in the database's order, keyed by (schema, name).
            let enumFields =
                schemas
                |> List.collect (fun sc ->
                    sc.Fields
                    |> Array.toList
                    |> List.filter (fun f -> f.Json.IsNone)
                    |> List.choose (fun f -> f.Mapping |> Option.bind (fun m -> m.StoreType)))

            let! currentSchema =
                if enumFields.IsEmpty then
                    Task.FromResult ""
                else
                    task {
                        let! r = Execution.query s { Sql = "SELECT current_schema()"; Parameters = [] } (fun r -> r.GetString 0)
                        return Seq.head r
                    }

            let qualified (parts: string list) =
                match parts with
                | [ schema; name ] -> schema, name
                | _ -> currentSchema, List.last parts

            let! labelRows =
                if enumFields.IsEmpty then
                    Task.FromResult(ResizeArray())
                else
                    let names = enumFields |> List.map (qualified >> snd) |> List.distinct |> Array.ofList

                    Execution.query
                        s
                        { Sql =
                            "SELECT n.nspname, t.typname, e.enumlabel FROM pg_type t "
                            + "JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_enum e ON e.enumtypid = t.oid "
                            + "WHERE t.typname = ANY("
                            + dialect.ParameterName 0
                            + ") ORDER BY n.nspname, t.typname, e.enumsortorder"
                          Parameters = [ box names ] }
                        (fun r -> (r.GetString 0, r.GetString 1), r.GetString 2)

            let enumLabels =
                labelRows |> List.ofSeq |> List.groupBy fst |> List.map (fun (k, rs) -> k, rs |> List.map snd) |> Map.ofList

            // Single-column unique indexes (constraints included), and
            // single-column foreign keys with the table they reference.
            let! uniqueRows =
                Execution.query
                    s
                    { Sql =
                        "SELECT t.relname::text, a.attname::text, ic.relname::text, EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.conrelid = t.oid AND c.contype IN ('u', 'p')) FROM pg_index i "
                        + "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace "
                        + "JOIN pg_class ic ON ic.oid = i.indexrelid "
                        + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0] "
                        + "WHERE i.indisunique AND i.indnkeyatts = 1 AND n.nspname = current_schema() AND t.relname::text = ANY("
                        + dialect.ParameterName 0
                        + ")"
                      Parameters = [ box tables ] }
                    (fun r -> (r.GetString 0, r.GetString 1), (r.GetString 2, r.GetBoolean 3))

            let! referenceRows =
                Execution.query
                    s
                    { Sql =
                        "SELECT t.relname::text, a.attname::text, r.relname::text, c.conname::text FROM pg_constraint c "
                        + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid "
                        + "JOIN pg_namespace n ON n.oid = t.relnamespace "
                        + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = c.conkey[1] "
                        + "WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1 AND n.nspname = current_schema() AND t.relname::text = ANY("
                        + dialect.ParameterName 0
                        + ")"
                      Parameters = [ box tables ] }
                    (fun r -> ((r.GetString 0, r.GetString 1), r.GetString 2), r.GetString 3)

            let uniqueNames = uniqueRows |> List.ofSeq
            let uniques = uniqueNames |> List.map fst |> set
            let referenceNames = referenceRows |> List.ofSeq
            let references = referenceNames |> List.map fst

            let bare (table: string) = table.Split('.') |> Array.last

            // A fact the model states, held under a name violations are not
            // mapped by: the field's error would become a database failure.
            let misnamed (holders: (string * bool) list) (expected: string) table =
                if not holders.IsEmpty && not (holders |> List.exists (fun (n, _) -> n = expected)) then
                    let actual, isConstraint = holders.Head

                    let how =
                        if isConstraint then
                            $"alter table {table} rename constraint {actual} to {expected}"
                        else
                            $"alter index {actual} rename to {expected}"

                    Some(actual, $"rename {actual} to {expected} (`{how}`)")
                else
                    None

            let byTable =
                rows |> Seq.groupBy fst |> Seq.map (fun (t, cs) -> t, cs |> Seq.map snd |> List.ofSeq) |> Map.ofSeq

            let issues = ResizeArray<SchemaIssue>()

            let add kind modelName field table column message =
                issues.Add
                    { Kind = kind
                      Model = modelName
                      Field = field
                      Table = table
                      Column = column
                      Message = message }

            let typeCheck modelName field table (c: Column) (providerType: Type) =
                match ColumnTypes.ofType dialect providerType with
                | [] -> ()
                | allowed when not (List.contains c.DataType allowed) ->
                    add
                        SchemaIssueKind.IncompatibleType
                        modelName
                        field
                        table
                        (Some c.Name)
                        $"""column is {c.DataType}; {Reflection.typeName providerType} needs {String.concat " or " allowed}"""
                | _ -> ()

            // A field stored as an enum type: the column must have that type, and
            // the type's labels must be the stored names, in the cases' order.
            let enumCheck modelName (f: FieldMap) table (c: Column) (m: ScalarMapping) (storeType: string list) =
                let schemaName, typeName = qualified storeType
                let display = String.concat "." storeType
                let domain = Reflection.typeName m.DomainType
                let issue kind message = add kind modelName (Some f.Name) table (Some c.Name) message

                match enumLabels.TryFind(schemaName, typeName) with
                | None -> issue SchemaIssueKind.IncompatibleType $"enum type {display} does not exist"
                | Some _ when c.DataType <> "USER-DEFINED" ->
                    issue SchemaIssueKind.IncompatibleType $"column is {c.DataType}; {domain} needs the enum type {display}"
                | Some _ when (c.UdtSchema, c.UdtName) <> (schemaName, typeName) ->
                    issue SchemaIssueKind.IncompatibleType $"column is {c.UdtName}; {domain} needs the enum type {display}"
                | Some labels ->
                    let stored = m.Cases |> List.map snd

                    for l in labels do
                        if not (List.contains l stored) then
                            issue SchemaIssueKind.EnumLabels $"label {l} is not a {domain} case; reading it would fail"

                    for case, l in m.Cases do
                        if not (List.contains l labels) then
                            issue SchemaIssueKind.EnumLabels $"no label {l} for {case}; writing it would fail"

                    if List.sort labels = List.sort stored && labels <> stored then
                        let order = String.concat ", " labels
                        let cases = m.Cases |> List.map fst |> String.concat ", "

                        issue
                            SchemaIssueKind.EnumLabels
                            $"labels are ordered {order} but the cases {cases}; the database would sort differently from F#"

            let unwritable modelName table (columns: Column list) (written: string list) =
                for c in columns do
                    if not c.Nullable && not c.HasDefault && not (List.contains c.Name written) then
                        add
                            SchemaIssueKind.UnwritableColumn
                            modelName
                            None
                            table
                            (Some c.Name)
                            "NOT NULL column without a default that the model does not map; inserts would fail"

            for sc in schemas do
                match byTable.TryFind sc.Table with
                | None -> add SchemaIssueKind.MissingTable sc.Name None sc.Table None "table does not exist"
                | Some columns ->
                    // A union with fields: each case field's column must exist with
                    // a fitting type, and allow NULL (other cases store NULL in it).
                    for f in sc.Fields |> Array.filter (fun f -> f.Union.IsSome) do
                        for p in f.Parts |> List.tail do
                            match columns |> List.tryFind (fun c -> c.Name = p.Name) with
                            | None -> add SchemaIssueKind.MissingColumn sc.Name (Some f.Name) sc.Table (Some p.Name) "column does not exist"
                            | Some c ->
                                typeCheck sc.Name (Some f.Name) sc.Table c p.ProviderType

                                if not c.Nullable then
                                    add
                                        SchemaIssueKind.IncompatibleType
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some c.Name)
                                        $"column is NOT NULL, but {Reflection.typeName f.ScalarType}'s other cases store NULL in it"

                    // A value: each of its fields' columns, NOT NULL where the
                    // value requires it (value-records.feature).
                    for f in sc.Fields |> Array.filter (fun f -> f.Value.IsSome) do
                        for sf in valueLeaves f do
                            match columns |> List.tryFind (fun c -> c.Name = sf.Column) with
                            | None -> add SchemaIssueKind.MissingColumn sc.Name (Some f.Name) sc.Table (Some sf.Column) "column does not exist"
                            | Some c ->
                                match sf.Mapping |> Option.bind (fun m -> m.StoreType |> Option.map (fun t -> m, t)) with
                                | Some(m, t) -> enumCheck sc.Name sf sc.Table c m t
                                | None -> typeCheck sc.Name (Some f.Name) sc.Table c sf.ProviderType

                                if c.Nullable && f.Requires sf.Column then
                                    add
                                        SchemaIssueKind.NullableColumn
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some c.Name)
                                        $"column is nullable but {sc.Name}.{f.Name}.{sf.Name} is not optional; a NULL would fail to load. Make the field an option, or the column NOT NULL"

                    for f in sc.Fields |> Array.filter (fun f -> f.Value.IsNone) do
                        match columns |> List.tryFind (fun c -> c.Name = f.Column) with
                        | None -> add SchemaIssueKind.MissingColumn sc.Name (Some f.Name) sc.Table (Some f.Column) "column does not exist"
                        | Some c ->
                            match f.Mapping |> Option.bind (fun m -> m.StoreType |> Option.map (fun t -> m, t)) with
                            | Some _ when f.Json.IsSome ->
                                if c.DataType <> "jsonb" then
                                    add
                                        SchemaIssueKind.IncompatibleType
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some c.Name)
                                        $"column is {c.DataType}; {sc.Name}.{f.Name} is a document and needs jsonb"
                            | Some(m, t) -> enumCheck sc.Name f sc.Table c m t
                            | None -> typeCheck sc.Name (Some f.Name) sc.Table c f.ProviderType

                            if c.Nullable && not f.IsOption then
                                add
                                    SchemaIssueKind.NullableColumn
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some c.Name)
                                    $"column is nullable but {sc.Name}.{f.Name} is not optional; a NULL would fail to load. Make the field an option, or the column NOT NULL"

                    unwritable sc.Name sc.Table columns (sc.Fields |> Array.toList |> List.collect (fun f -> f.Parts |> List.map (fun p -> p.Name)))

                    for f in sc.Fields do
                        if columns |> List.exists (fun c -> c.Name = f.Column) then
                            if f.Unique then
                                let holders = uniqueNames |> List.filter (fun (k, _) -> k = (sc.Table, f.Column)) |> List.map snd

                                match misnamed holders $"{bare sc.Table}_{f.Column}_key" sc.Table with
                                | Some(actual, fix) ->
                                    add
                                        SchemaIssueKind.MisnamedConstraint
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some f.Column)
                                        $"{actual} holds the uniqueness of {sc.Name}.{f.Name}, but a violation is mapped to the field by its default name; {fix}"
                                | None -> ()

                            if f.Unique && not (uniques.Contains(sc.Table, f.Column)) then
                                add
                                    SchemaIssueKind.MissingUnique
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some f.Column)
                                    $"the schema states {sc.Name}.{f.Name} is unique, but no unique constraint or index holds it; add `constraint {sc.Table}_{f.Column}_key unique ({f.Column})`"

                            // A reference over several columns is checked below.
                            match f.References |> Option.bind (fun t -> model.Schemas.TryGetValue t |> function | true, target -> Some target | _ -> None) with
                            | Some _ when f.Composite -> ()
                            | Some target when not (references |> List.contains ((sc.Table, f.Column), target.Table)) ->
                                add
                                    SchemaIssueKind.MissingReference
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some f.Column)
                                    $"{sc.Name}.{f.Name} holds a {target.Name} key, but no foreign key references {target.Table}; add `references {target.Table}`"
                            | Some target ->
                                let holders =
                                    referenceNames
                                    |> List.filter (fun (k, _) -> k = ((sc.Table, f.Column), target.Table))
                                    |> List.map (fun (_, n) -> n, true)

                                match misnamed holders $"{bare sc.Table}_{f.Column}_fkey" sc.Table with
                                | Some(actual, fix) ->
                                    add
                                        SchemaIssueKind.MisnamedConstraint
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some f.Column)
                                        $"{actual} holds the reference from {sc.Name}.{f.Name} to {target.Table}, but a violation is mapped to the field by its default name; {fix}"
                                | None -> ()
                            | _ -> ()

            for sc, a, j in joinTables do
                let target = model.Schemas.[a.Target]

                match byTable.TryFind j.Table with
                | None -> add SchemaIssueKind.MissingTable sc.Name (Some a.Name) j.Table None "join table does not exist"
                | Some columns ->
                    for column, key in [ j.OwnerColumn, sc.Key; j.TargetColumn, target.Key ] do
                        match columns |> List.tryFind (fun c -> c.Name = column) with
                        | None -> add SchemaIssueKind.MissingColumn sc.Name (Some a.Name) j.Table (Some column) "join table column does not exist"
                        | Some c -> typeCheck sc.Name (Some a.Name) j.Table c key.ProviderType

                    unwritable sc.Name j.Table columns [ j.OwnerColumn; j.TargetColumn ]

                    // Each side of the join table references its record's table.
                    for column, table in [ j.OwnerColumn, sc.Table; j.TargetColumn, target.Table ] do
                        if columns |> List.exists (fun c -> c.Name = column) && not (references |> List.contains ((j.Table, column), table)) then
                            add
                                SchemaIssueKind.MissingReference
                                sc.Name
                                (Some a.Name)
                                j.Table
                                (Some column)
                                $"{j.Table}.{column} has no foreign key to {table}; add `references {table}`"

                    // A linked list maps a link to a missing record by the
                    // target side's default name.
                    if a.Linked then
                        let holders =
                            referenceNames
                            |> List.filter (fun (k, _) -> k = ((j.Table, j.TargetColumn), target.Table))
                            |> List.map (fun (_, n) -> n, true)

                        match misnamed holders $"{bare j.Table}_{j.TargetColumn}_fkey" j.Table with
                        | Some(actual, fix) ->
                            add
                                SchemaIssueKind.MisnamedConstraint
                                sc.Name
                                (Some a.Name)
                                j.Table
                                (Some j.TargetColumn)
                                $"{actual} holds the links of {sc.Name}.{a.Name} to {target.Table}, but a link to a missing record is mapped to the field by its default name; {fix}"
                        | None -> ()

            // A key of several parts (composite-keys.feature): the table's
            // primary key is over its parts, in the key record's order.
            let compositeSchemas = schemas |> List.filter (fun sc -> sc.Key.Composite)

            if not compositeSchemas.IsEmpty then
                let! primaryRows =
                    Execution.query
                        s
                        { Sql =
                            "SELECT t.relname::text, string_agg(a.attname::text, ',' ORDER BY k.ord) FROM pg_constraint c "
                            + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace "
                            + "CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) "
                            + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum "
                            + "WHERE c.contype = 'p' AND n.nspname = current_schema() AND t.relname::text = ANY("
                            + dialect.ParameterName 0
                            + ") GROUP BY t.relname"
                          Parameters = [ box (compositeSchemas |> List.map (fun sc -> bare sc.Table) |> Array.ofList) ] }
                        (fun r -> r.GetString 0, r.GetString 1)

                let primary = primaryRows |> Map.ofSeq

                for sc in compositeSchemas do
                    let parts = sc.Key.Parts |> List.map (fun p -> p.Name)
                    let wanted = String.concat ", " parts

                    match primary.TryFind(bare sc.Table) with
                    | Some actual when actual = String.concat "," parts -> ()
                    | found ->
                        let has =
                            match found with
                            | Some actual ->
                                let columns = actual.Replace(",", ", ")
                                $"the primary key is ({columns})"
                            | None -> "the table has no primary key"

                        add
                            SchemaIssueKind.PrimaryKey
                            sc.Name
                            (Some sc.Key.Name)
                            sc.Table
                            None
                            $"{has}; {sc.Name}'s key of several parts needs primary key ({wanted}), in that order"

            // A reference to a key of several parts (composite-references.feature):
            // one foreign key over its columns, in order, to that table. A
            // record's own key may hold such a reference as a part.
            let compositeReferences =
                [ for sc in schemas do
                      let candidates =
                          [ for f in sc.Fields do
                                yield f, f.Name

                                if f.Name = sc.Key.Name then
                                    match f.Value with
                                    | Some v -> for part in v.Fields -> part, $"{f.Name}.{part.Name}"
                                    | None -> () ]

                      for f, path in candidates do
                          match f.References |> Option.bind (fun t -> model.Schemas.TryGetValue t |> function | true, r -> Some r | _ -> None) with
                          | Some target when f.Composite -> yield sc, f, path, target
                          | _ -> () ]

            if not compositeReferences.IsEmpty then
                let! foreignRows =
                    Execution.query
                        s
                        { Sql =
                            "SELECT t.relname::text, r.relname::text, (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord) FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) "
                            + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum) FROM pg_constraint c "
                            + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid JOIN pg_namespace n ON n.oid = t.relnamespace "
                            + "WHERE c.contype = 'f' AND n.nspname = current_schema() AND t.relname::text = ANY("
                            + dialect.ParameterName 0
                            + ")"
                          Parameters = [ box (compositeReferences |> List.map (fun (sc, _, _, _) -> bare sc.Table) |> List.distinct |> Array.ofList) ] }
                        (fun r -> r.GetString 0, r.GetString 1, r.GetString 2)

                let foreign = foreignRows |> List.ofSeq

                for sc, f, path, target in compositeReferences do
                    let columns = f.Parts |> List.map (fun p -> p.Name)

                    if not (foreign |> List.contains (bare sc.Table, bare target.Table, String.concat "," columns)) then
                        let listed = String.concat ", " columns
                        let keyColumns = target.Key.Parts |> List.map (fun p -> p.Name) |> String.concat ", "

                        add
                            SchemaIssueKind.MissingReference
                            sc.Name
                            (Some path)
                            sc.Table
                            None
                            $"{sc.Name}.{path} refers to {target.Name}, but no foreign key over ({listed}) references {target.Table}; add `foreign key ({listed}) references {target.Table} ({keyColumns})`"

            return List.ofSeq issues
        }

/// The DDL a model expects (ddl.feature): what a migration should reach. It
/// is printed, never run; Plasm does not write or apply migrations.
module internal Ddl =

    let print (model: Model) (dialect: ISqlDialect) : string =
        let plain = Text.RegularExpressions.Regex "^[a-z_][a-z0-9_]*$"
        let name (n: string) = if plain.IsMatch n then n else dialect.QuoteIdentifier n
        let schemas = model.Schemas.Values |> Seq.sortBy (fun s -> s.Table) |> List.ofSeq
        let byType = model.Schemas

        let columnType (p: ColumnPart) =
            match p.Mapping |> Option.bind (fun m -> m.StoreType) with
            | Some parts -> parts |> List.map name |> String.concat "."
            | None ->
                match ColumnTypes.ofType dialect p.ProviderType with
                | t :: _ -> t
                | [] -> failwith $"{dialect.Name} has no column type for {p.ProviderType.Name}"

        let out = Text.StringBuilder()
        let line (s: string) = out.AppendLine s |> ignore

        // Enum types (Scalar.asPgEnum), each once, with its labels in the cases' order.
        let enums =
            schemas
            |> List.collect (fun s -> s.Fields |> Array.toList |> List.collect (fun f -> f.Parts))
            |> List.choose (fun p -> p.Mapping |> Option.bind (fun m -> m.StoreType |> Option.map (fun t -> t, m)))
            // Documents (jsonb) have a store type but no labels: not enums.
            |> List.filter (fun (_, m) -> not m.Cases.IsEmpty)
            |> List.distinctBy fst

        for t, m in enums do
            let labels = m.Cases |> List.map (fun (_, l) -> $"'{l}'") |> String.concat ", "
            let typeName = t |> List.map name |> String.concat "."
            line $"create type {typeName} as enum ({labels});"

        // The columns a field is stored in, as one reference's columns: one,
        // or a reference's to a key of several parts (composite-references.feature).
        let columnsOf (f: FieldMap) = if f.Composite then f.Parts |> List.map (fun p -> p.Name) else [ f.Column ]

        // References: (table, columns, referenced schema), each once. A
        // record's own key may hold another record's key as a part.
        let references =
            [ for s in schemas do
                  let rec within (f: FieldMap) =
                      [ match f.References |> Option.bind (fun t -> match byType.TryGetValue t with | true, r -> Some r | _ -> None) with
                        | Some r -> yield s.Table, columnsOf f, r
                        | None -> ()

                        if f.Name = s.Key.Name || f.Value.IsNone then
                            match f.Value with
                            | Some v when f.Name = s.Key.Name -> for part in v.Fields do yield! within part
                            | _ -> () ]

                  for f in s.Fields do
                      yield! within f

                  for a in s.Assocs do
                      match a.Kind with
                      | AssocKind.HasMany
                      | AssocKind.HasOne ->
                          let child = byType.[a.Target]
                          yield child.Table, columnsOf (child.Field a.ForeignKey), s
                      | AssocKind.ManyToMany -> () ]
            |> List.distinctBy (fun (t, c, _) -> t, c)

        for s in schemas do
            let columns =
                [ for f in s.Fields do
                      for p in f.Parts do
                          let required = f.Requires p.Name

                          let key =
                              if f.Name = s.Key.Name && f.Composite then
                                  ""
                              elif f.Name = s.Key.Name then
                                  if s.KeyGenerated then " generated always as identity primary key" else " primary key"
                              else
                                  ""

                          let version = if s.Version |> Option.exists (fun v -> v.Name = f.Name) then " default 1" else ""
                          let notNull = if required then " not null" else ""
                          yield $"  {name p.Name} {columnType p}{notNull}{version}{key}"

                  // An optional value: all its columns NULL, or every column it
                  // requires present (value-records.feature).
                  for f in s.Fields do
                      match f.Value with
                      | Some v when f.IsOption && f.Parts.Length > 1 ->
                          let absent = f.Parts |> List.map (fun p -> $"{name p.Name} is null") |> String.concat " and "

                          let present =
                              f.Parts |> List.filter (fun p -> v.Requires p.Name) |> List.map (fun p -> $"{name p.Name} is not null") |> String.concat " and "

                          let constraintName = name (s.Table + "_" + f.Column + "_present")
                          yield $"  constraint {constraintName} check (({absent}) or ({present}))"
                      | _ -> ()

                  for f in s.Fields do
                      if f.Unique then
                          let constraintName = name (s.Table + "_" + f.Column + "_key")
                          yield $"  constraint {constraintName} unique ({name f.Column})"

                  // A key of several parts (composite-keys.feature): one
                  // primary key over them, in the key record's order.
                  if s.Key.Composite then
                      let parts = s.Key.Parts |> List.map (fun p -> name p.Name) |> String.concat ", "
                      yield $"  primary key ({parts})" ]

            line $"create table {name s.Table} ("
            line (String.concat ",\n" columns)
            line ");"

        // Join tables of linked lists, each once.
        let joins =
            [ for s in schemas do
                  for a in s.Assocs do
                      match a.Through with
                      | Some j -> yield j, s, byType.[a.Target]
                      | None -> () ]
            |> List.distinctBy (fun (j, _, _) -> j.Table)

        for j, owner, target in joins do
            let keyType (s: SchemaInfo) = columnType s.Key.Parts.Head
            line $"create table {name j.Table} ("
            line $"  {name j.OwnerColumn} {keyType owner} not null,"
            line $"  {name j.TargetColumn} {keyType target} not null,"
            line $"  primary key ({name j.OwnerColumn}, {name j.TargetColumn})"
            line ");"

        let joinReferences =
            [ for j, owner, target in joins do
                  yield j.Table, [ j.OwnerColumn ], owner
                  yield j.Table, [ j.TargetColumn ], target ]

        let listed (columns: string list) = columns |> List.map name |> String.concat ", "

        for table, columns, r in references @ joinReferences do
            let constraintName = name (table + "_" + String.concat "_" columns + "_fkey")
            let keyColumns = r.Key.Parts |> List.map (fun p -> p.Name)

            line
                $"alter table {name table} add constraint {constraintName} foreign key ({listed columns}) references {name r.Table} ({listed keyColumns});"

        // The columns the model's reads filter on (an owned table's owner
        // column, a reference to another aggregate, a join table's target
        // column), unless the column already leads a unique constraint or the
        // primary key: a join table's key leads with its owner column.
        // An index's leading columns serve a filter on any prefix of them.
        let leads =
            [ for s in schemas do
                  yield s.Table, s.Key.Parts |> List.map (fun p -> p.Name)

                  for f in s.Fields do
                      if f.Unique then
                          yield s.Table, [ f.Column ]
              for j, _, _ in joins do
                  yield j.Table, [ j.OwnerColumn; j.TargetColumn ] ]

        let led (table: string, columns: string list) =
            leads |> List.exists (fun (t, l) -> t = table && List.truncate columns.Length l = columns)

        let indexed =
            [ for table, columns, _ in references @ joinReferences -> table, columns ]
            |> List.distinct
            |> List.filter (led >> not)

        for table, columns in indexed do
            let index = (table.Split('.') |> Array.last) + "_" + String.concat "_" columns + "_idx"
            line $"create index {name index} on {name table} ({listed columns});"

        out.ToString()
