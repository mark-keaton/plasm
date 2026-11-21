namespace Plasm

open System
open System.Threading.Tasks

/// The catalogue type names a column of a provider type may have, the
/// dialect's, an array column's (array-columns.feature) among them; and the
/// type such a column is declared as, which Ddl prints.
module internal ColumnTypes =
    // A list of scalars is asked for as its array type: the dialect says how it
    // names one (PostgreSQL: its element's names followed by [], SQLite: TEXT).
    let ofType (d: ISqlDialect) (t: Type) : string list = d.CatalogueTypes t

    let declared (d: ISqlDialect) (t: Type) : string option = d.ColumnType t

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
                { Sql = dialect.Catalogue.Columns(dialect.ParameterName 0)
                  Parameters = [ dialect.ListValue tables ] }

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
                        let! r = Execution.query s { Sql = dialect.Catalogue.CurrentSchema; Parameters = [] } (fun r -> r.GetString 0)
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
                        { Sql = dialect.Catalogue.EnumLabels(dialect.ParameterName 0)
                          Parameters = [ dialect.ListValue names ] }
                        (fun r -> (r.GetString 0, r.GetString 1), r.GetString 2)

            let enumLabels =
                labelRows |> List.ofSeq |> List.groupBy fst |> List.map (fun (k, rs) -> k, rs |> List.map snd) |> Map.ofList

            // Unique indexes (constraints included) with their key columns in
            // order, joined by commas (one column for most; several for a field
            // unique within others, unique-within.feature), and single-column
            // foreign keys with the table they reference.
            let! uniqueRows =
                Execution.query
                    s
                    { Sql = dialect.Catalogue.UniqueIndexes(dialect.ParameterName 0)
                      Parameters = [ dialect.ListValue tables ] }
                    (fun r -> (r.GetString 0, r.GetString 1), (r.GetString 2, r.GetBoolean 3))

            let! referenceRows =
                Execution.query
                    s
                    { Sql = dialect.Catalogue.References(dialect.ParameterName 0)
                      Parameters = [ dialect.ListValue tables ] }
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
                                let documents = Sql.storeName dialect Store.jsonDocument |> String.concat "."

                                if c.DataType <> documents then
                                    add
                                        SchemaIssueKind.IncompatibleType
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some c.Name)
                                        $"column is {c.DataType}; {sc.Name}.{f.Name} is a document and needs {documents}"
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
                            // A uniqueness is held by an index over its columns, in order.
                            let unique = String.concat "," f.UniqueColumns
                            let listed = String.concat ", " f.UniqueColumns

                            if f.Unique then
                                let holders = uniqueNames |> List.filter (fun (k, _) -> k = (sc.Table, unique)) |> List.map snd

                                match misnamed holders $"""{bare sc.Table}_{String.concat "_" f.UniqueColumns}_key""" sc.Table with
                                | Some(actual, fix) ->
                                    add
                                        SchemaIssueKind.MisnamedConstraint
                                        sc.Name
                                        (Some f.Name)
                                        sc.Table
                                        (Some f.Column)
                                        $"{actual} holds the uniqueness of {sc.Name}.{f.Name}, but a violation is mapped to the field by its default name; {fix}"
                                | None -> ()

                            if f.Unique && not (uniques.Contains(sc.Table, unique)) then
                                let within = if f.Within.IsEmpty then "" else $""" within {String.concat ", " f.Within}"""

                                add
                                    SchemaIssueKind.MissingUnique
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some f.Column)
                                    $"""the schema states {sc.Name}.{f.Name} is unique{within}, but no unique constraint or index holds it; add `constraint {sc.Table}_{String.concat "_" f.UniqueColumns}_key unique ({listed})`"""

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

                    // A typed id among a union's case fields or a value's
                    // fields, in its own column (union-references.feature).
                    for f in sc.Fields |> Array.filter (fun f -> f.Name <> sc.Key.Name) do
                        for label, leaf in f.NestedReferences do

                            match model.Schemas.TryGetValue leaf.References.Value with
                            | true, target when columns |> List.exists (fun c -> c.Name = leaf.Column) ->
                                if not (references |> List.contains ((sc.Table, leaf.Column), target.Table)) then
                                    add
                                        SchemaIssueKind.MissingReference
                                        sc.Name
                                        (Some label)
                                        sc.Table
                                        (Some leaf.Column)
                                        $"{sc.Name}.{label} holds a {target.Name} key, but no foreign key references {target.Table}; add `references {target.Table}`"
                                else
                                    let holders =
                                        referenceNames
                                        |> List.filter (fun (k, _) -> k = ((sc.Table, leaf.Column), target.Table))
                                        |> List.map (fun (_, n) -> n, true)

                                    match misnamed holders $"{bare sc.Table}_{leaf.Column}_fkey" sc.Table with
                                    | Some(actual, fix) ->
                                        add
                                            SchemaIssueKind.MisnamedConstraint
                                            sc.Name
                                            (Some label)
                                            sc.Table
                                            (Some leaf.Column)
                                            $"{actual} holds the reference from {sc.Name}.{label} to {target.Table}, but a violation is mapped to the field by its default name; {fix}"
                                    | None -> ()
                            | _ -> ()

            for sc, a, j in joinTables do
                let target = model.Schemas.[a.Target]

                match byTable.TryFind j.Table with
                | None -> add SchemaIssueKind.MissingTable sc.Name (Some a.Name) j.Table None "join table does not exist"
                | Some columns ->
                    // Each side's columns: one, or one per part of a key of
                    // several parts (composite-links.feature).
                    let owners = Joins.owners j sc.Key
                    let targets = Joins.targets j target.Key

                    for column, part in owners @ targets do
                        match columns |> List.tryFind (fun c -> c.Name = column) with
                        | None -> add SchemaIssueKind.MissingColumn sc.Name (Some a.Name) j.Table (Some column) "join table column does not exist"
                        | Some c -> typeCheck sc.Name (Some a.Name) j.Table c part.ProviderType

                    unwritable sc.Name j.Table columns (owners @ targets |> List.map fst)

                    // Each side of the join table references its record's table;
                    // a side of several columns is checked with the other
                    // references over several columns, below.
                    let single =
                        [ if not sc.Key.Composite then yield fst owners.Head, sc.Table
                          if not target.Key.Composite then yield fst targets.Head, target.Table ]

                    for column, table in single do
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
                    if a.Linked && not target.Key.Composite then
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
                        { Sql = dialect.Catalogue.PrimaryKeys(dialect.ParameterName 0)
                          Parameters = [ dialect.ListValue(compositeSchemas |> List.map (fun sc -> bare sc.Table) |> Array.ofList) ] }
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
                          | Some target when f.Composite -> yield sc.Name, path, sc.Table, f.Parts |> List.map (fun p -> p.Name), target
                          | _ -> ()

                  // A join table's side for a key of several parts
                  // (composite-links.feature), where the table has every column.
                  for sc, a, j in joinTables do
                      let target = model.Schemas.[a.Target]

                      match byTable.TryFind j.Table with
                      | Some columns ->
                          for key, side, referred in [ sc.Key, Joins.owners j sc.Key, sc; target.Key, Joins.targets j target.Key, target ] do
                              let names = side |> List.map fst

                              if key.Composite && names |> List.forall (fun n -> columns |> List.exists (fun c -> c.Name = n)) then
                                  yield sc.Name, a.Name, j.Table, names, referred
                      | None -> () ]

            if not compositeReferences.IsEmpty then
                let! foreignRows =
                    Execution.query
                        s
                        { Sql = dialect.Catalogue.ForeignKeys(dialect.ParameterName 0)
                          Parameters = [ dialect.ListValue(compositeReferences |> List.map (fun (_, _, table, _, _) -> bare table) |> List.distinct |> Array.ofList) ] }
                        (fun r -> r.GetString 0, r.GetString 1, r.GetString 2)

                let foreign = foreignRows |> List.ofSeq

                for name, path, table, columns, target in compositeReferences do
                    if not (foreign |> List.contains (bare table, bare target.Table, String.concat "," columns)) then
                        let listed = String.concat ", " columns
                        let keyColumns = target.Key.Parts |> List.map (fun p -> p.Name) |> String.concat ", "

                        add
                            SchemaIssueKind.MissingReference
                            name
                            (Some path)
                            table
                            None
                            $"{name}.{path} refers to {target.Name}, but no foreign key over ({listed}) of {table} references {target.Table}; add `foreign key ({listed}) references {target.Table} ({keyColumns})`"

            return List.ofSeq issues
        }

/// The DDL a model expects (ddl.feature): what a migration should reach. It
/// is printed, never run; Plasm does not write or apply migrations.
module internal Ddl =

    /// A table or column named by one of the dialect's reserved words, such as
    /// `from` or `order`, is printed quoted, as every query quotes it.
    let print (model: Model) (dialect: ISqlDialect) : string =
        let plain = Text.RegularExpressions.Regex "^[a-z_][a-z0-9_]*$"
        let ddl = dialect.Ddl
        let name (n: string) = if plain.IsMatch n && not (ddl.Reserved.Contains n) then n else dialect.QuoteIdentifier n
        let schemas = model.Schemas.Values |> Seq.sortBy (fun s -> s.Table) |> List.ofSeq
        let byType = model.Schemas

        let columnType (p: ColumnPart) =
            match p.Mapping |> Option.bind (fun m -> m.StoreType) with
            | Some parts -> ddl.StoreColumn(Sql.storeName dialect parts |> List.map name |> String.concat ".")
            | None ->
                match ColumnTypes.declared dialect p.ProviderType with
                | Some t -> t
                | None -> failwith $"{dialect.Name} has no column type for {p.ProviderType.Name}"

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
            let typeName = t |> List.map name |> String.concat "."

            match ddl.EnumType(typeName, m.Cases |> List.map snd) with
            | Some statement -> line statement
            | None -> ()

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

                      if f.Name <> s.Key.Name then
                          for _, leaf in f.NestedReferences do
                              match byType.TryGetValue leaf.References.Value with
                              | true, r -> yield s.Table, [ leaf.Column ], r
                              | _ -> ()

                  for a in s.Assocs do
                      match a.Kind with
                      | AssocKind.HasMany
                      | AssocKind.HasOne ->
                          let child = byType.[a.Target]
                          yield child.Table, columnsOf (child.Field a.ForeignKey), s
                      | AssocKind.ManyToMany -> () ]
            |> List.distinctBy (fun (t, c, _) -> t, c)

        let listed (columns: string list) = columns |> List.map name |> String.concat ", "

        // `constraint t_c_fkey foreign key (c) references r (k)`.
        let foreignKey (table: string) (columns: string list) (r: SchemaInfo) =
            let constraintName = name (table + "_" + String.concat "_" columns + "_fkey")
            let keyColumns = r.Key.Parts |> List.map (fun p -> p.Name)
            $"constraint {constraintName} foreign key ({listed columns}) references {name r.Table} ({listed keyColumns})"

        for s in schemas do
            let columns =
                [ for f in s.Fields do
                      for p in f.Parts do
                          let required = f.Requires p.Name

                          let key =
                              if f.Name = s.Key.Name && f.Composite then
                                  ""
                              elif f.Name = s.Key.Name then
                                  if s.KeyGenerated then ddl.GeneratedKey else " primary key"
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
                          let columns = f.UniqueColumns
                          let constraintName = name (s.Table + "_" + String.concat "_" columns + "_key")
                          yield $"""  constraint {constraintName} unique ({columns |> List.map name |> String.concat ", "})"""

                  // A column of an enum, held to its labels where no enum type holds it.
                  for f in s.Fields do
                      for p in f.Parts do
                          match p.Mapping with
                          | Some m when m.StoreType.IsSome && not m.Cases.IsEmpty ->
                              match ddl.EnumCheck(name p.Name, m.Cases |> List.map snd) with
                              | Some check ->
                                  let constraintName = name (s.Table + "_" + p.Name + "_check")
                                  yield $"  constraint {constraintName} {check}"
                              | None -> ()
                          | _ -> ()

                  // A column held to the form its values are stored in where
                  // its type does not hold it: a list (array-columns.feature),
                  // a time (utc-only.feature). An enum's is EnumCheck's.
                  for f in s.Fields do
                      for p in f.Parts do
                          match ddl.ColumnCheck(name p.Name, p.ProviderType) with
                          | Some check ->
                              let constraintName = name (s.Table + "_" + p.Name + "_check")
                              yield $"  constraint {constraintName} {check}"
                          | None -> ()

                  // A key of several parts (composite-keys.feature): one
                  // primary key over them, in the key record's order.
                  if s.Key.Composite then
                      let parts = s.Key.Parts |> List.map (fun p -> name p.Name) |> String.concat ", "
                      yield $"  primary key ({parts})"

                  // Its references, where the dialect declares them in the table.
                  if ddl.ForeignKeysInTables then
                      for table, columns, r in references do
                          if table = s.Table then
                              yield $"  {foreignKey table columns r}" ]

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

        // A column per part of a key of several parts (composite-links.feature).
        let sides (j: JoinTable) (owner: SchemaInfo) (target: SchemaInfo) = Joins.owners j owner.Key, Joins.targets j target.Key

        for j, owner, target in joins do
            let owners, targets = sides j owner target
            line $"create table {name j.Table} ("

            for column, part in owners @ targets do
                line $"  {name column} {columnType part} not null,"

            let key = owners @ targets |> List.map (fst >> name) |> String.concat ", "

            if ddl.ForeignKeysInTables then
                line $"  primary key ({key}),"
                line $"  {foreignKey j.Table (owners |> List.map fst) owner},"
                line $"  {foreignKey j.Table (targets |> List.map fst) target}"
            else
                line $"  primary key ({key})"

            line ");"

        let joinReferences =
            [ for j, owner, target in joins do
                  let owners, targets = sides j owner target
                  yield j.Table, owners |> List.map fst, owner
                  yield j.Table, targets |> List.map fst, target ]

        if not ddl.ForeignKeysInTables then
            for table, columns, r in references @ joinReferences do
                line $"alter table {name table} add {foreignKey table columns r};"

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
                          yield s.Table, f.UniqueColumns
              for j, owner, target in joins do
                  let owners, targets = sides j owner target
                  yield j.Table, owners @ targets |> List.map fst ]

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
