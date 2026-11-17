/// What each koan is given: the tables it starts from and the definitions it
/// uses, so every blank can be answered from the page. Worked out from the
/// koan's code and the shop's source (Shop.fs), never written by hand, so it
/// cannot drift from the course.
module Plasm.Koans.Given

open System
open System.IO
open System.Text.RegularExpressions

/// A definition the learner may need: a type or a function.
type Definition =
    { Name: string
      /// Where it is defined: "Shop.fs" or "this lesson".
      Source: string
      Code: string list
      /// For a record, its field names (to mark the ones a koan reads).
      Fields: string list
      /// For a union, its case names.
      Cases: string list
      /// For a stored record, its table ([<Table "...">]).
      Table: string option
      /// For a record, the record each list or option field holds (what an
      /// aggregate owns or links to, read with it).
      Holds: (string * string) list }

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
let private tableAttribute = Regex("\\[<Table \"(\\w+)\">\\]")
let private heldField = Regex(@"(\w+)\s*:\s*(\w+) (?:list|option)\b")

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

    // A declaration's doc comment and attributes belong to it.
    let firstLine i =
        let mutable j = i
        while j > 0 && (lines.[j - 1].TrimStart().StartsWith "///" || lines.[j - 1].TrimStart().StartsWith "[<") do
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
            let body = code |> List.filter (fun l -> not (l.TrimStart().StartsWith "///" || l.TrimStart().StartsWith "[<"))
            let isRecord = body.Head.Contains "= {" || (body.Length > 1 && body.[1].TrimStart().StartsWith "{")

            { Name = name
              Source = source
              Code = code
              Fields = if isRecord then body |> List.collect fieldsOf else []
              Cases = if isRecord || not (typeStart.IsMatch body.Head) then [] else body |> List.tail |> List.choose (fun l -> let m = caseLine.Match l in if m.Success then Some m.Groups.[1].Value else None)
              Table = (let m = tableAttribute.Match(String.concat "\n" code) in if m.Success then Some m.Groups.[1].Value else None)
              Holds = heldField.Matches(String.concat "\n" code) |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value) |> List.ofSeq }))

let private mentions (text: string) (name: string) =
    Regex.IsMatch(text, @"(?<![\w.])" + Regex.Escape name + @"(?!\w)")

/// The words inside the string literals of some code (where SQL lives).
let private stringWords (code: string) =
    Regex.Matches(code, "\"([^\"]*)\"") |> Seq.collect (fun m -> Regex.Matches(m.Groups.[1].Value, @"\w+") |> Seq.map (fun w -> w.Value)) |> Set.ofSeq

/// The tables of the koans database, in the order they are created.
let tables: string list =
    Regex.Matches(KoansData.schema, @"create table (\w+)") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

/// Join tables, by the record whose linked list names them ([<JoinTable "article_tags">]).
let private joinTables (d: Definition) =
    Regex.Matches(String.concat "\n" d.Code, "\\[<JoinTable\\(?\"(\\w+)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

let private memberLine = Regex(@"^\s*member \w+\.(\w+)")

/// The lines of a definition that a koan reads: a record's fields
/// (o.CustomerId) and a type's members (k.Repo).
let private marks (body: string) (d: Definition) =
    let reads name = Regex.IsMatch(body, @"\." + name + @"\b")

    d.Code
    |> List.indexed
    |> List.filter (fun (_, l) ->
        (not d.Fields.IsEmpty && fieldsOf l |> List.exists reads)
        || (let m = memberLine.Match l in m.Success && reads m.Groups.[1].Value))
    |> List.map fst

/// Works out a koan's Given from its code, the shop's definitions and its
/// lesson's helpers.
let forKoan (shop: Definition list) (helpers: Definition list) (body: string) : KoanGiven =
    let byName = shop |> List.map (fun d -> d.Name, d) |> Map.ofList
    let records = shop |> List.filter (fun d -> not d.Fields.IsEmpty)

    // The stored record of a name, with its table.
    let storedRecord (record: string) =
        byName.TryFind record |> Option.bind (fun d -> d.Table |> Option.map (fun t -> d, t))

    let recordOfTable table =
        records |> List.tryPick (fun d -> if d.Table = Some table then Some d.Name else None)

    // The records the koan reads as a query source (tableOf<Order>).
    let sources =
        records |> List.filter (fun d -> d.Table.IsSome && body.Contains $"tableOf<{d.Name}>")

    let words = stringWords body

    // What a record holds (its owned or linked records), read whenever it is.
    let heldBy (record: string) =
        byName.TryFind record |> Option.map (fun r -> r.Holds |> List.choose (snd >> storedRecord)) |> Option.defaultValue []

    // Records reached through a field the koan reads (o.Lines, a.Tags):
    // (the owner, which names any join table, and the held table).
    let viaFields =
        records
        |> List.collect (fun r -> r.Holds |> List.map (fun h -> r, h))
        |> List.filter (fun (_, (field, _)) -> Regex.IsMatch(body, @"\." + field + @"\b"))
        |> List.choose (fun (owner, (_, target)) -> storedRecord target |> Option.map (fun (_, t) -> owner, t))

    let used =
        set [
            for d in sources do
                yield d.Table.Value
                yield! joinTables d

                for _, t in heldBy d.Name do
                    yield t
            for t in tables do
                if words.Contains t then yield t
            for owner, t in viaFields do
                yield t
                yield! joinTables owner ]

    let usedTables = tables |> List.filter used.Contains

    // Definitions: what the code names, then the records of the tables it uses.
    let named =
        shop
        |> List.filter (fun d ->
            match d.Name with
            | n when n.StartsWith "Db." || n.StartsWith "New." -> mentions body n
            | n -> mentions body n || (d.Cases |> List.exists (mentions body)))

    let tableRecords = usedTables |> List.choose recordOfTable |> List.choose byName.TryFind

    let chosen =
        let types = named |> List.filter (fun d -> not (d.Name.Contains "."))
        let dbs = named |> List.filter (fun d -> d.Name.Contains ".")
        types @ tableRecords @ dbs @ (helpers |> List.filter (fun h -> mentions body h.Name))
        |> List.distinctBy (fun d -> d.Source, d.Name)

    { Tables = usedTables
      Definitions = chosen |> List.map (fun d -> d, marks body d) }

/// The test helpers a koan names from the koans' own code (Koans.fs), such as
/// ok, err and sqlSent.
let fromKoans (koans: Definition list) (body: string) =
    koans |> List.filter (fun d -> mentions body d.Name) |> List.map (fun d -> d, marks body d)

/// `repo`, which every koan is given: not defined in any file the learner sees
/// (it is the koan's parameter), so its Given says what it is and how an app
/// makes one.
let repo: Definition =
    { Name = "repo"
      Source = "every koan"
      Code =
        [ "/// Every koan is given repo: a Plasm Repo over the koans database,"
          "/// inside a sandbox, so whatever the koan changes is undone when it ends."
          "/// In your app you start one yourself, from your model and a provider;"
          "/// it checks every query in the assembly and the database as it starts:"
          "let! repo = Repo.Start(Db.model, Postgres.connect connectionString, queriesIn = typeof<Order>.Assembly)" ]
      Fields = []
      Cases = []
      Table = None
      Holds = [] }

/// The Given entry for repo, when a koan uses it.
let forRepo (body: string) =
    if mentions body "repo" then [ repo, [] ] else []

