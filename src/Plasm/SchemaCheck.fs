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
                    "SELECT table_name, column_name, data_type, is_nullable, column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER' "
                    + "FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ANY("
                    + dialect.ParameterName 0
                    + ")"
                  Parameters = [ box tables ] }

            let! rows =
                Execution.query s cmd (fun r ->
                    r.GetString 0,
                    { Name = r.GetString 1
                      DataType = r.GetString 2
                      Nullable = (r.GetString 3 = "YES")
                      HasDefault = r.GetBoolean 4 })

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
                            typeCheck sc.Name (Some f.Name) sc.Table c f.ProviderType

                            if c.Nullable && not f.IsOption then
                                add
                                    SchemaIssueKind.NullableColumn
                                    sc.Name
                                    (Some f.Name)
                                    sc.Table
                                    (Some c.Name)
                                    $"column is nullable but {sc.Name}.{f.Name} is not optional; a NULL would fail to load. Make the field an option, or the column NOT NULL"

                    unwritable sc.Name sc.Table columns (sc.Fields |> Array.toList |> List.map (fun f -> f.Column))

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
