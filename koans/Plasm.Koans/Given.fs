/// What each koan is given: the tables it starts from and the definitions it
/// uses, so every blank can be answered from the page. Worked out from the
/// koan's code and the shop's source (Shop.fs), never written by hand, so it
/// cannot drift from the course.
module Plasm.Koans.Given

open System
open System.IO
open System.Text.RegularExpressions

/// A definition the learner may need: a type, a function or a schema.
type Definition =
    { Name: string
      /// Where it is defined: "Shop.fs" or "this lesson".
      Source: string
      Code: string list
      /// For a record, its field names (to mark the ones a koan reads).
      Fields: string list
      /// For a union, its case names.
      Cases: string list
      /// For a schema, its table.
      Table: string option
      /// For a record, the record each relationship field points at.
      Assocs: (string * string) list }

type KoanGiven =
    { Tables: string list
      Definitions: (Definition * int list) list }

let private typeStart = Regex(@"^(?:type|and) (\w+)")
let private letTop = Regex(@"^let (\w+)")
let private letInModule = Regex(@"^    let (?:private )?(\w+)")
let private moduleLine = Regex(@"^module (\w+)")
let private fieldPattern = Regex(@"(?:^|\{|;)\s*(\w+)\s*:")

/// The record field names declared on a line of code.
let private fieldsOf (line: string) =
    fieldPattern.Matches line |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
let private caseLine = Regex(@"^\s*\|\s*(\w+)")
let private schemaCall = Regex("schema<(\\w+)> \"(\\w+)\"")
let private assocField = Regex(@"(\w+)\s*:\s*Assoc<(\w+)")

/// Splits source into declarations, each with the doc comment above it.
let parse (source: string) (lines: string[]) : Definition list =
    let mutable currentModule = None

    let starts =
        [ for i in 0 .. lines.Length - 1 do
              let l = lines.[i]
              let m = moduleLine.Match l

              if m.Success then
                  currentModule <- Some m.Groups.[1].Value
                  yield i, None
              elif typeStart.IsMatch l then
                  currentModule <- None
                  yield i, Some(typeStart.Match(l).Groups.[1].Value)
              elif letTop.IsMatch l then
                  currentModule <- None
                  yield i, Some(letTop.Match(l).Groups.[1].Value)
              elif currentModule.IsSome && letInModule.IsMatch l then
                  yield i, Some(currentModule.Value + "." + letInModule.Match(l).Groups.[1].Value) ]

    // A declaration's doc comment belongs to it.
    let firstLine i =
        let mutable j = i
        while j > 0 && lines.[j - 1].TrimStart().StartsWith "///" do
            j <- j - 1
        j

    starts
    |> List.mapi (fun n (i, name) -> n, i, name)
    |> List.choose (fun (n, i, name) ->
        name
        |> Option.map (fun name ->
            let stop = if n + 1 < starts.Length then firstLine (fst starts.[n + 1]) else lines.Length

            let code =
                lines.[firstLine i .. stop - 1]
                |> List.ofArray
                |> List.rev
                |> List.skipWhile String.IsNullOrWhiteSpace
                |> List.rev

            let indent = code |> List.filter (String.IsNullOrWhiteSpace >> not) |> List.map (fun l -> l.Length - l.TrimStart().Length) |> List.min
            let code = code |> List.map (fun l -> if l.Length >= indent then l.Substring indent else l.TrimStart())
            let body = code |> List.filter (fun l -> not (l.TrimStart().StartsWith "///"))
            let isRecord = body.Head.Contains "= {" || (body.Length > 1 && body.[1].TrimStart().StartsWith "{")

            { Name = name
              Source = source
              Code = code
              Fields = if isRecord then body |> List.collect fieldsOf else []
              Cases = if isRecord || not (typeStart.IsMatch body.Head) then [] else body |> List.tail |> List.choose (fun l -> let m = caseLine.Match l in if m.Success then Some m.Groups.[1].Value else None)
              Table = (let m = schemaCall.Match(String.concat "\n" code) in if m.Success then Some m.Groups.[2].Value else None)
              Assocs = assocField.Matches(String.concat "\n" code) |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value) |> List.ofSeq }))

let private mentions (text: string) (name: string) =
    Regex.IsMatch(text, @"(?<![\w.])" + Regex.Escape name + @"(?!\w)")

/// The words inside the string literals of some code (where SQL lives).
let private stringWords (code: string) =
    Regex.Matches(code, "\"([^\"]*)\"") |> Seq.collect (fun m -> Regex.Matches(m.Groups.[1].Value, @"\w+") |> Seq.map (fun w -> w.Value)) |> Set.ofSeq

/// The tables of the koans database, in the order they are created.
let tables: string list =
    Regex.Matches(KoansData.schema, @"create table (\w+)") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

/// Join tables, by the schema that names them (manyToMany ... "article_tags").
let private joinTables (d: Definition) =
    Regex.Matches(String.concat "\n" d.Code, "manyToMany \\([^)]*\\) \\([^)]*\\) \"(\\w+)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

/// Works out a koan's Given from its code, the shop's definitions and its
/// lesson's helpers.
let forKoan (shop: Definition list) (helpers: Definition list) (body: string) : KoanGiven =
    let byName = shop |> List.map (fun d -> d.Name, d) |> Map.ofList
    let records = shop |> List.filter (fun d -> not d.Fields.IsEmpty)

    // A schema's table, following a schema defined through a helper (Db.orders = ordersWith ...).
    let rec tableOf (d: Definition) =
        match d.Table with
        | Some t -> Some t
        | None ->
            shop
            |> List.tryFind (fun o -> o.Name <> d.Name && o.Name.StartsWith "Db." && mentions (String.concat "\n" d.Code) (o.Name.Substring 3))
            |> Option.bind tableOf

    let recordOfTable table =
        shop
        |> List.tryPick (fun d -> if d.Table = Some table then Some(schemaCall.Match(String.concat "\n" d.Code).Groups.[1].Value) else None)

    let schemas = shop |> List.filter (fun d -> d.Name.StartsWith "Db.")
    let usedSchemas = schemas |> List.filter (fun d -> mentions body d.Name)

    let words = stringWords body

    let viaRelationships =
        records
        |> List.collect (fun r -> r.Assocs)
        |> List.filter (fun (field, _) -> Regex.IsMatch(body, @"\." + field + @"\b"))
        |> List.choose (fun (_, target) -> shop |> List.tryPick (fun d -> if d.Name.StartsWith "Db." && d.Table.IsSome && (schemaCall.Match(String.concat "\n" d.Code).Groups.[1].Value = target) then Some(d, d.Table.Value) else None))

    let used =
        set [
            for d in usedSchemas do
                match tableOf d with
                | Some t -> yield t
                | None -> ()
                yield! joinTables d
            for t in tables do
                if words.Contains t then yield t
            for d, t in viaRelationships do
                yield t
                yield! joinTables d ]

    let usedTables = tables |> List.filter used.Contains

    // Definitions: what the code names, then the records of the tables it uses.
    let named =
        shop
        |> List.filter (fun d ->
            match d.Name with
            | n when n.StartsWith "Db." || n.StartsWith "New." -> mentions body n
            | n -> mentions body n || (d.Cases |> List.exists (mentions body)))

    let tableRecords = usedTables |> List.choose recordOfTable |> List.choose byName.TryFind

    // A schema defined through a private helper shows the helper too.
    let helpersOf (d: Definition) =
        schemas |> List.filter (fun o -> o.Name <> d.Name && d.Table.IsNone && mentions (String.concat "\n" d.Code) (o.Name.Substring 3))

    let chosen =
        let types = named |> List.filter (fun d -> not (d.Name.Contains "."))
        let dbs = named |> List.filter (fun d -> d.Name.Contains ".")
        types @ tableRecords @ dbs @ (dbs |> List.collect helpersOf) @ (helpers |> List.filter (fun h -> mentions body h.Name))
        |> List.distinctBy (fun d -> d.Source, d.Name)

    // The fields of a record that the koan reads (o.CustomerId), by line.
    let marks (d: Definition) =
        if d.Fields.IsEmpty then
            []
        else
            d.Code
            |> List.indexed
            |> List.filter (fun (_, l) -> fieldsOf l |> List.exists (fun f -> Regex.IsMatch(body, @"\." + f + @"\b")))
            |> List.map fst

    { Tables = usedTables
      Definitions = chosen |> List.map (fun d -> d, marks d) }
