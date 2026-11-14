namespace Plasm

open System
open System.Threading.Tasks

/// What a schema check found.
[<RequireQualifiedAccess>]
type SchemaIssueKind =
    /// A mapped table (or join table) does not exist.
    | MissingTable
    /// A mapped column does not exist.
    | MissingColumn
    /// The column's type cannot hold the field's values.
    | IncompatibleType
    /// A non-optional field reads a nullable column: a NULL would fail to load.
    | NullableColumn
    /// A NOT NULL column without a default that the model never writes:
    /// inserts would fail.
    | UnwritableColumn
    /// An enum type's labels differ from the union's cases (missing, extra or
    /// in a different order).
    | EnumLabels
    /// A field the schema states is unique has no unique constraint or index
    /// on its column alone.
    | MissingUnique
    /// A field that references another record (a typed id) has no foreign key
    /// to that record's table.
    | MissingReference

/// A difference between the model and the database that would make a mapped
/// read or write fail.
type SchemaIssue =
    { Kind: SchemaIssueKind
      Model: string
      /// The field or relationship, when the issue concerns one.
      Field: string option
      Table: string
      Column: string option
      Message: string }

    override i.ToString() =
        let field = i.Field |> Option.map (fun f -> "." + f) |> Option.defaultValue ""
        let column = i.Column |> Option.map (fun c -> "." + c) |> Option.defaultValue ""
        $"{i.Kind} {i.Model}{field} ({i.Table}{column}): {i.Message}"

module internal SchemaCheck =

    type private Column =
        { Name: string
          DataType: string
          /// The column type's schema and name (for an enum column, the enum type's).
          UdtSchema: string
          UdtName: string
          Nullable: bool
          HasDefault: bool }

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
                    "SELECT table_name, column_name, data_type, is_nullable, column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER', udt_schema, udt_name "
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
                        "SELECT t.relname::text, a.attname::text FROM pg_index i "
                        + "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace "
                        + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0] "
                        + "WHERE i.indisunique AND i.indnkeyatts = 1 AND n.nspname = current_schema() AND t.relname::text = ANY("
                        + dialect.ParameterName 0
                        + ")"
                      Parameters = [ box tables ] }
                    (fun r -> r.GetString 0, r.GetString 1)

            let! referenceRows =
                Execution.query
                    s
                    { Sql =
                        "SELECT t.relname::text, a.attname::text, r.relname::text FROM pg_constraint c "
                        + "JOIN pg_class t ON t.oid = c.conrelid JOIN pg_class r ON r.oid = c.confrelid "
                        + "JOIN pg_namespace n ON n.oid = t.relnamespace "
                        + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = c.conkey[1] "
                        + "WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1 AND n.nspname = current_schema() AND t.relname::text = ANY("
                        + dialect.ParameterName 0
                        + ")"
                      Parameters = [ box tables ] }
                    (fun r -> (r.GetString 0, r.GetString 1), r.GetString 2)

            let uniques = set uniqueRows
            let references = referenceRows |> List.ofSeq

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
                match dialect.ColumnTypes providerType with
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
                    for f in sc.Fields do
                        match columns |> List.tryFind (fun c -> c.Name = f.Column) with
                        | None -> add SchemaIssueKind.MissingColumn sc.Name (Some f.Name) sc.Table (Some f.Column) "column does not exist"
                        | Some c ->
                            match f.Mapping |> Option.bind (fun m -> m.StoreType |> Option.map (fun t -> m, t)) with
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

                    unwritable sc.Name sc.Table columns (sc.Fields |> Array.toList |> List.map (fun f -> f.Column))

                    for f in sc.Fields do
                        if columns |> List.exists (fun c -> c.Name = f.Column) then
                            if f.Unique && not (uniques.Contains(sc.Table, f.Column)) then
                                add
                                    SchemaIssueKind.MissingUnique
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some f.Column)
                                    $"the schema states {sc.Name}.{f.Name} is unique, but no unique constraint or index holds it; add `constraint {sc.Table}_{f.Column}_key unique ({f.Column})`"

                            match f.References |> Option.bind (fun t -> model.Schemas.TryGetValue t |> function | true, target -> Some target | _ -> None) with
                            | Some target when not (references |> List.contains ((sc.Table, f.Column), target.Table)) ->
                                add
                                    SchemaIssueKind.MissingReference
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some f.Column)
                                    $"{sc.Name}.{f.Name} holds a {target.Name} key, but no foreign key references {target.Table}; add `references {target.Table}`"
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

            return List.ofSeq issues
        }
