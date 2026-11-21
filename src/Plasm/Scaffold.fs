namespace Plasm

open System
open System.Threading.Tasks

/// F# source for a record that says how one table stores it, generated from
/// the database catalogue by `repo.Scaffold`. Displays as the code itself.
[<StructuredFormatDisplay("{Display}")>]
type Scaffold =
    { Table: string
      RecordName: string
      /// The record type, ready to paste or evaluate.
      Code: string }

    /// The code on its own lines, for F# Interactive.
    member s.Display = "\n" + s.Code

    override s.ToString() = s.Code

/// Why `repo.Scaffold` produced no code.
[<RequireQualifiedAccess>]
type ScaffoldError =
    /// No table of that name in the current schema.
    | TableNotFound of table: string
    /// The table exists, but none of its columns has an F# type in this
    /// dialect, so there is no record to generate. Lists (column, type).
    | NoMappableColumns of table: string * columns: (string * string) list
    /// Reading the catalogue failed.
    | Failed of DbError

module internal Scaffolding =

    type private Column =
        { Name: string
          DataType: string
          Nullable: bool
          Identity: bool
          PrimaryKey: bool
          HasDefault: bool }

    /// F# types in preference order (the first whose column types include the
    /// column's type is used), with their F# spelling.
    let private candidates =
        [ typeof<int16>, "int16"
          typeof<int>, "int"
          typeof<int64>, "int64"
          typeof<string>, "string"
          typeof<bool>, "bool"
          typeof<decimal>, "decimal"
          typeof<float>, "float"
          typeof<float32>, "float32"
          typeof<Guid>, "System.Guid"
          typeof<DateTime>, "System.DateTime"
          typeof<DateTimeOffset>, "System.DateTimeOffset"
          typeof<DateOnly>, "System.DateOnly"
          typeof<TimeOnly>, "System.TimeOnly"
          typeof<TimeSpan>, "System.TimeSpan"
          typeof<byte[]>, "byte[]" ]

    let private keywords =
        set
            [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate"; "do"; "done"; "downcast"
              "downto"; "elif"; "else"; "end"; "exception"; "extern"; "false"; "finally"; "fixed"; "for"; "fun"; "function"
              "global"; "if"; "in"; "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member"; "module"
              "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or"; "override"; "private"; "public"; "rec"
              "return"; "select"; "sig"; "static"; "struct"; "then"; "to"; "true"; "try"; "type"; "upcast"; "use"; "val"
              "void"; "when"; "while"; "with"; "yield"; "const" ]

    /// PascalCase from any column or table name: words are runs of letters and digits.
    let private pascal (name: string) =
        let words = Text.RegularExpressions.Regex.Split(name, "[^A-Za-z0-9]+") |> Array.filter (fun w -> w <> "")

        match words with
        | [||] -> name
        | ws -> ws |> Array.map (fun w -> string (Char.ToUpperInvariant w.[0]) + w.Substring 1) |> String.concat ""

    /// How a name is written in F# source: as is when it is a plain
    /// identifier, otherwise in double backticks.
    let private source (name: string) =
        let plain = Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")
        if plain && not (keywords.Contains name) then name else $"``{name}``"

    let private singular (word: string) =
        if word.EndsWith "ies" then word.Substring(0, word.Length - 3) + "y"
        elif word.EndsWith "sses" then word.Substring(0, word.Length - 2)
        elif word.EndsWith "s" && not (word.EndsWith "ss") then word.Substring(0, word.Length - 1)
        else word

    let private generate (dialect: ISqlDialect) (table: string) (columns: Column list) =
        let fsharpType (c: Column) =
            candidates |> List.tryFind (fun (t, _) -> List.contains c.DataType (dialect.CatalogueTypes t)) |> Option.map snd

        let mapped, unsupported = columns |> List.partition (fun c -> (fsharpType c).IsSome)

        if mapped.IsEmpty then
            Error(ScaffoldError.NoMappableColumns(table, columns |> List.map (fun c -> c.Name, c.DataType)))
        else

        let recordName = pascal (singular table)
        let keys = mapped |> List.filter (fun c -> c.PrimaryKey)

        // The key is the field named Id: the primary key column, or, without a
        // single-column primary key, the first column for the reader to review.
        let keyColumn =
            match keys with
            | [ k ] -> k
            | _ -> List.head mapped

        // (property name, how it is written, F# type, column)
        let fields =
            mapped
            |> List.map (fun c ->
                let t = (fsharpType c).Value
                let name = if c = keyColumn then "Id" else pascal c.Name
                name, source name, (if c.Nullable then t + " option" else t), c)

        let comments =
            unsupported
            |> List.map (fun c ->
                let consequence =
                    if not c.Nullable && not c.HasDefault then
                        " It is NOT NULL without a default, so inserts will fail until it has one."
                    else
                        ""

                $"// {c.Name}: {c.DataType} has no F# type in this dialect, so it is left out.{consequence}")

        let integer (c: Column) =
            match fsharpType c with
            | Some("int16" | "int" | "int64") -> true
            | _ -> false

        let notes =
            [ if keys.Length <> 1 then
                  $"// {keyColumn.Name} is the key only because {table} has no single-column primary key: choose the key, and name its field Id"
              for name, _, _, c in fields do
                  if c.HasDefault && not c.Identity && not c.PrimaryKey then
                      $"// {c.Name} has a default: mark {name} [<Generated>] to let the database set it" ]

        let literal (text: string) = "\"" + text.Replace("\"", "\\\"") + "\""

        let attributes (name: string) (c: Column) =
            [ if Paths.snakeCase name <> c.Name then
                  "Column " + literal c.Name
              if c = keyColumn && integer c && not c.Identity then
                  "Assigned" ]

        let record =
            fields
            |> List.mapi (fun i (name, src, t, c) ->
                let attrs =
                    match attributes name c with
                    | [] -> ""
                    | xs -> "[<" + String.concat "; " xs + ">] "

                (if i = 0 then "    { " else "      ") + $"{attrs}{src}: {t}")
            |> String.concat "\n"

        let code =
            String.concat
                "\n"
                [ yield! comments
                  yield! notes
                  "[<Table " + literal table + ">]"
                  $"type {source recordName} ="
                  record + " }" ]

        Ok
            { Table = table
              RecordName = recordName
              Code = code }

    let run (table: string) (s: Execution.Session) : Task<Result<Scaffold, ScaffoldError>> =
        task {
            let dialect = s.Provider.Dialect

            let cmd: Sql.Command =
                { Sql = dialect.Catalogue.TableColumns(dialect.ParameterName 0)
                  Parameters = [ box table ] }

            let! columns =
                Execution.query s cmd (fun r ->
                    { Name = r.GetString 0
                      DataType = r.GetString 1
                      Nullable = r.GetBoolean 2
                      Identity = r.GetBoolean 3
                      PrimaryKey = r.GetBoolean 4
                      HasDefault = r.GetBoolean 5 })

            let columns = List.ofSeq columns

            if columns.IsEmpty then
                return Error(ScaffoldError.TableNotFound table)
            else
                return generate dialect table columns
        }
