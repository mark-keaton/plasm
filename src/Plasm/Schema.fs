namespace Plasm

open System
open System.Reflection
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

/// What an owned or linked list is, internally: a list of the owner's own
/// rows (HasMany), one owned row (HasOne), or links through a join table.
[<RequireQualifiedAccess>]
type internal AssocKind =
    | HasMany
    | HasOne
    | ManyToMany

module internal Paths =

    /// Extracts the property from `fun x -> x.Prop`.
    let tryProperty (e: Expr) =
        match e with
        | Lambda(v, PropertyGet(Some(Var v2), prop, [])) when v = v2 -> Some prop
        | _ -> None

    let property (what: string) (e: Expr) =
        match tryProperty e with
        | Some p -> p
        | None -> Err.config $"{what} must be a direct property access such as (fun x -> x.Field); got {e}"

    let snakeCase (name: string) =
        let sb = Text.StringBuilder()

        name
        |> Seq.iteri (fun i c ->
            if Char.IsUpper c then
                if i > 0 && not (Char.IsUpper name.[i - 1]) then
                    sb.Append('_') |> ignore
                elif i > 0 && i + 1 < name.Length && Char.IsLower name.[i + 1] then
                    sb.Append('_') |> ignore

                sb.Append(Char.ToLowerInvariant c) |> ignore
            else
                sb.Append(c) |> ignore)

        sb.ToString()

/// A many-to-many join table: its name and the columns referencing the owner's
/// and the target's primary keys. A side whose key has several parts has a
/// column per part, `<prefix>_<part>` (composite-links.feature; Joins).
type internal JoinTable =
    { Table: string
      OwnerColumn: string
      TargetColumn: string
      /// The owner's side's columns are named from this when its key has
      /// several parts: the JoinTable attribute's OwnerColumn, else the
      /// owner's name in snake case.
      OwnerPrefix: string
      TargetPrefix: string }

type internal AssocDef =
    { Property: PropertyInfo
      Kind: AssocKind
      /// Null for ManyToMany.
      ForeignKey: PropertyInfo
      Through: JoinTable option
      /// An owned list (`owns`): a plain list, always loaded and written with
      /// its owner (owned-collections.feature).
      Owned: bool
      /// A linked list (`links`): a plain list of shared records, always loaded
      /// with its owner; writing the owner writes only join-table rows.
      Linked: bool
      /// For an owned or linked list: the order its items are read in (default: by key).
      Order: Expr option }

/// What a record type says about how it is stored, worked out from the type
/// (Stored.fs): its table, key, columns and relationships.
type internal SchemaDef =
        { Type: Type
          Table: string
          Key: (PropertyInfo * bool) option
          Columns: Map<string, string>
          /// Every name [<Column>] gives a field, in order: several for a field
          /// of a key of several parts (composite-keys.feature).
          ColumnNames: Map<string, string list>
          Version: PropertyInfo option
          Generated: Set<string>
          /// Fields whose values are unique in the table (constraints.feature).
          Unique: Set<string>
          /// The fields a unique field is unique within ([<Unique(Within = ...)>],
          /// unique-within.feature), by field name.
          UniqueWithin: Map<string, string list>
          /// Fields of a key type that refer to no row ([<NotReference>]).
          NotReference: Set<string>
          Assocs: AssocDef list }

/// The stored records of type `'T`, as a query source: `tableOf<'T>`.
/// What is stored, and where, is read from `'T` itself.
type Table<'T> internal (def: SchemaDef) =
    member internal _.Def = def
    /// The table the records are stored in.
    member _.Name = def.Table
    member _.RecordType = typeof<'T>

    interface ISchema with
        member _.Definition = def

/// A record type's definition, for building a Model.
and internal ISchema =
    abstract Definition: SchemaDef

// ---------------------------------------------------------------------------
// Resolved metadata (produced by Model.build)
// ---------------------------------------------------------------------------

/// Times as both providers store them: an instant, in UTC (utc-only.feature).
/// A DateTime whose Kind is not Utc, or a DateTimeOffset whose offset is not
/// zero, is refused before it is sent: PostgreSQL's driver would send such a
/// DateTime as a timestamp without a time zone, which the server reads in its
/// own, and refuses such a DateTimeOffset by throwing.
module internal Times =
    let private one (v: obj) =
        match v with
        | :? DateTime as d when d.Kind <> DateTimeKind.Utc ->
            Some
                $"only UTC times are stored, and {d:o} has Kind {d.Kind}: give it Kind Utc (DateTime.SpecifyKind for a time that is UTC, ToUniversalTime for a local one)"
        | :? DateTimeOffset as d when d.Offset <> TimeSpan.Zero ->
            Some $"only UTC times are stored, and {d:o} has the offset {d.Offset}: give it the offset zero (ToUniversalTime)"
        | _ -> None

    /// Why a provider value (or an element of a list of them) cannot be stored.
    let refusal (v: obj) : string option =
        match v with
        | :? Array as a when not (v :? byte[]) -> a |> Seq.cast<obj> |> Seq.tryPick one
        | v -> one v

/// How a [<Json>] field's value is a document (json-columns.feature).
type internal JsonShape =
    /// A scalar, stored as its column would be: its provider value as a JSON
    /// string, number or boolean.
    | JScalar of providerType: Type * mapping: ScalarMapping option
    /// A record: an object keyed by its fields in snake case.
    | JRecord of recordType: Type * keys: JsonKey list * make: (obj[] -> obj) * read: (obj -> obj[])
    /// A list: an array.
    | JList of element: JsonShape * elementType: Type

and internal JsonKey =
    { Field: string
      Key: string
      /// An option: None is a missing key.
      Optional: bool
      Shape: JsonShape
      ReadOption: obj -> obj option
      WrapSome: obj -> obj }

/// Plasm's own mapping between values and documents: the same in every
/// application, and strict when reading (json-columns.feature).
module internal JsonDoc =
    open System.Globalization
    open System.Text.Json
    open System.Text.Json.Nodes

    let private dateTime = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    let private dateTimeOffset = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"

    /// The provider types a document can hold.
    let supports (t: Type) =
        [ typeof<string>; typeof<bool>; typeof<int16>; typeof<int>; typeof<int64>; typeof<decimal>; typeof<float>
          typeof<Guid>; typeof<DateTime>; typeof<DateTimeOffset>; typeof<DateOnly> ]
        |> List.contains t

    let private scalarNode (v: obj) : JsonNode =
        match v with
        | :? string as x -> JsonValue.Create x
        | :? bool as x -> JsonValue.Create x
        | :? int16 as x -> JsonValue.Create x
        | :? int as x -> JsonValue.Create x
        | :? int64 as x -> JsonValue.Create x
        | :? decimal as x -> JsonValue.Create x
        | :? float as x -> JsonValue.Create x
        | :? Guid as x -> JsonValue.Create(x.ToString())
        | :? DateTime as x -> JsonValue.Create(x.ToUniversalTime().ToString(dateTime, CultureInfo.InvariantCulture))
        | :? DateTimeOffset as x -> JsonValue.Create(x.ToString(dateTimeOffset, CultureInfo.InvariantCulture))
        | :? DateOnly as x -> JsonValue.Create(x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        | other -> failwith $"a document cannot hold a {other.GetType().Name}"

    /// The value as a document.
    let rec write (shape: JsonShape) (value: obj) : JsonNode =
        match shape with
        | JScalar(_, mapping) ->
            scalarNode (
                match mapping with
                | Some m -> m.ToProvider value
                | None -> value
            )
        | JList(element, elementType) ->
            let a = JsonArray()

            for x in Reflection.listItems elementType value do
                a.Add(write element x)

            a
        | JRecord(_, keys, _, read) ->
            let o = JsonObject()
            let values = read value

            keys
            |> List.iteri (fun i k ->
                if k.Optional then
                    match k.ReadOption values.[i] with
                    | Some x -> o.[k.Key] <- write k.Shape x
                    | None -> ()
                else
                    o.[k.Key] <- write k.Shape values.[i])

            o

    /// The first time in a document that is not UTC (Times), with its path
    /// inside the document (`.checklist[1].due`): refused as a column's is.
    let rec refusal (shape: JsonShape) (path: string) (value: obj) : (string * string) option =
        match shape with
        | JScalar(_, mapping) ->
            let p =
                match mapping with
                | Some m -> m.ToProvider value
                | None -> value

            Times.refusal p |> Option.map (fun why -> path, why)
        | JList(element, elementType) ->
            Reflection.listItems elementType value
            |> Seq.indexed
            |> Seq.tryPick (fun (i, x) -> refusal element $"{path}[{i}]" x)
        | JRecord(_, keys, _, read) ->
            let values = read value

            keys
            |> List.indexed
            |> List.tryPick (fun (i, k) ->
                if k.Optional then
                    k.ReadOption values.[i] |> Option.bind (refusal k.Shape $"{path}.{k.Key}")
                else
                    refusal k.Shape $"{path}.{k.Key}" values.[i])

    let private describe (node: JsonNode) =
        if isNull node then "null"
        else
            let text = node.ToJsonString()
            if text.Length > 40 then text.Substring(0, 40) + "..." else text

    let private readScalar (t: Type) (path: string) (node: JsonNode) : Result<obj, string> =
        let wrong what = Error $"{path}: expected {what}, found {describe node}"

        match node with
        | :? JsonValue as v ->
            let kind = v.GetValueKind()
            let text () = v.GetValue<string>()

            let parsed (what: string) (f: unit -> obj) =
                if kind <> JsonValueKind.String then wrong what
                else
                    try Ok(f ()) with _ -> wrong what

            let number (what: string) (f: unit -> obj) =
                if kind <> JsonValueKind.Number then wrong what
                else
                    try Ok(f ()) with _ -> wrong what

            if t = typeof<string> then (if kind = JsonValueKind.String then Ok(box (text ())) else wrong "a string")
            elif t = typeof<bool> then
                match kind with
                | JsonValueKind.True -> Ok(box true)
                | JsonValueKind.False -> Ok(box false)
                | _ -> wrong "true or false"
            elif t = typeof<int16> then number "a whole number" (fun () -> box (v.GetValue<int16>()))
            elif t = typeof<int> then number "a whole number" (fun () -> box (v.GetValue<int>()))
            elif t = typeof<int64> then number "a whole number" (fun () -> box (v.GetValue<int64>()))
            elif t = typeof<decimal> then number "a number" (fun () -> box (v.GetValue<decimal>()))
            elif t = typeof<float> then number "a number" (fun () -> box (v.GetValue<float>()))
            elif t = typeof<Guid> then parsed "a GUID" (fun () -> box (Guid.Parse(text ())))
            elif t = typeof<DateTime> then
                parsed "an ISO 8601 date and time" (fun () ->
                    box (DateTime.Parse(text (), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal)))
            elif t = typeof<DateTimeOffset> then
                parsed "an ISO 8601 date and time with an offset" (fun () -> box (DateTimeOffset.Parse(text (), CultureInfo.InvariantCulture)))
            elif t = typeof<DateOnly> then
                parsed "an ISO 8601 date" (fun () -> box (DateOnly.ParseExact(text (), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            else
                Error $"{path}: a document cannot hold a {t.Name}"
        | _ -> wrong $"a {t.Name}"

    let private firstError (results: Result<obj, string> list) =
        results |> List.tryPick (function Error e -> Some e | Ok _ -> None)

    /// The document as a value, or an error naming the path where it does not fit.
    let rec read (shape: JsonShape) (path: string) (node: JsonNode) : Result<obj, string> =
        if isNull node then
            Error $"{path} is null, but it is not optional"
        else
            match shape with
            | JScalar(t, mapping) ->
                readScalar t path node
                |> Result.bind (fun v ->
                    match mapping with
                    | Some m -> m.FromProvider v |> Result.mapError (fun e -> $"{path}: {e}")
                    | None -> Ok v)
            | JList(element, elementType) ->
                match node with
                | :? JsonArray as a ->
                    let items = a |> Seq.mapi (fun i n -> read element $"{path}[{i}]" n) |> List.ofSeq

                    match firstError items with
                    | Some e -> Error e
                    | None -> Ok(Reflection.makeList elementType (items |> List.map (function Ok x -> x | Error _ -> null)))
                | _ -> Error $"{path}: expected an array, found {describe node}"
            | JRecord(t, keys, make, _) ->
                match node with
                | :? JsonObject as o ->
                    let known = keys |> List.map (fun k -> k.Key) |> set

                    match o |> Seq.tryFind (fun kv -> not (known.Contains kv.Key)) with
                    | Some kv ->
                        Error
                            $"{path}.{kv.Key}, a key {t.Name} does not have: the document holds more than the record (remove the key from the stored documents first)"
                    | None ->
                        let values =
                            keys
                            |> List.map (fun k ->
                                let at = $"{path}.{k.Key}"

                                let mutable n: JsonNode = null

                                if o.TryGetPropertyValue(k.Key, &n) && not (isNull n) then
                                    read k.Shape at n |> Result.map (fun v -> if k.Optional then k.WrapSome v else v)
                                elif k.Optional then
                                    Ok null
                                else
                                    Error $"{at} is missing")

                        match firstError values with
                        | Some e -> Error e
                        | None -> Ok(make (values |> List.map (function Ok x -> x | Error _ -> null) |> Array.ofList))
                | _ -> Error $"{path}: expected an object, found {describe node}"

type internal FieldMap =
    { Name: string
      Index: int
      Column: string
      FieldType: Type
      /// Domain scalar type with any option removed.
      ScalarType: Type
      IsOption: bool
      Generated: bool
      /// Its values are unique in the table ([<Unique>] on the record type's field).
      Unique: bool
      /// The columns its uniqueness also spans, before its own: unique within
      /// them (unique-within.feature); empty for a field unique on its own.
      Within: string list
      /// The record type whose key this field holds: a typed id that is the
      /// key type of exactly one other record type (inferred by Model.build).
      References: Type option
      Mapping: ScalarMapping option
      ProviderType: Type
      /// Some(x) for option fields (precomputed; identity otherwise).
      WrapSome: obj -> obj
      /// Payload of an option field value (precomputed).
      ReadOption: obj -> obj option
      /// For a union with fields: how it is stored across columns (Column is
      /// then its case-name column). None for every other field.
      Union: UnionLayout option
      /// For a value (a record with no table, value-records.feature): its
      /// fields, stored in the owner's row (Column is then only their prefix).
      Value: ValueLayout option
      /// For a [<Json>] field: its document's shape (json-columns.feature).
      Json: JsonShape option
      /// For a list of scalars, stored as one array column
      /// (array-columns.feature): the elements' domain type. Mapping then
      /// turns the list into an array of the elements' provider values.
      Array: Type option }

    /// Field value (possibly an option) to a provider value or DBNull.
    member f.ToDb(value: obj) : obj =
        let inner = if f.IsOption then f.ReadOption value else Some value

        match inner with
        | None -> box DBNull.Value
        | Some v when isNull v -> box DBNull.Value
        | Some v ->
            match f.Mapping with
            | Some m -> m.ToProvider v
            | None -> v

    /// A domain scalar value (never an option) to a provider value.
    member f.ScalarToDb(value: obj) : obj =
        match f.Mapping with
        | Some m -> m.ToProvider value
        | None -> value

    /// Provider value (or DBNull) to a field value.
    member f.FromDb(value: obj) : Result<obj, string> =
        if isNull value || value = box DBNull.Value then
            if f.IsOption then
                Ok null
            else
                Error "NULL stored for a non-optional field"
        else
            let scalar =
                match f.Mapping with
                | Some m -> m.FromProvider value
                | None -> Ok value

            if f.IsOption then
                match scalar with
                | Ok v -> Ok(f.WrapSome v)
                | e -> e
            else
                scalar

    /// The columns the field is stored in: one, or a union's or a value's several.
    member f.Parts: ColumnPart list =
        match f.Union, f.Value with
        | Some u, _ -> u.Columns
        | None, Some v -> v.Columns
        | None, None ->
            [ { Name = f.Column
                ProviderType = f.ProviderType
                Mapping = f.Mapping } ]

    /// Stored in more than one column (a union with fields, or a value).
    member f.Composite = f.Union.IsSome || f.Value.IsSome

    /// The columns of its uniqueness, in the constraint's order: those it is
    /// unique within, then its own; empty when it is not unique.
    member f.UniqueColumns: string list = if f.Unique then f.Within @ [ f.Column ] else []

    /// The columns inside a union or a value field that hold another
    /// record's key, once per column, each with the path that names it
    /// (`Status.borrower`, `Home.City`): each is a reference
    /// (union-references.feature).
    member f.NestedReferences: (string * FieldMap) list =
        let rec within (path: string) (g: FieldMap) =
            match g.Value, g.Union with
            | Some v, _ -> v.Fields |> List.collect (fun s -> within $"{path}.{s.Name}" s)
            | None, Some u -> u.Leaves |> List.collect (fun l -> within $"{path}.{l.Name}" l)
            | None, None -> if g.References.IsSome then [ path, g ] else []

        if f.Composite then
            within f.Name f |> List.distinctBy (fun (_, l) -> l.Column)
        else
            []

    /// Whether a column of Parts must hold a value (NOT NULL).
    member f.Requires(column: string) : bool =
        match f.Union, f.Value with
        | Some _, _ -> column = f.Column
        | None, Some v -> not f.IsOption && v.Requires column
        | None, None -> not f.IsOption

    /// The field's value as one provider value (or DBNull) per column of Parts.
    member f.ToColumns(value: obj) : obj list =
        match f.Union, f.Value with
        | Some u, _ -> u.ToColumns value
        | None, Some v ->
            let inner = if f.IsOption then f.ReadOption value elif isNull value then None else Some value

            match inner with
            | None -> v.Columns |> List.map (fun _ -> box DBNull.Value)
            | Some x -> List.zip v.Fields (List.ofArray (v.Read x)) |> List.collect (fun (sf, sv) -> sf.ToColumns sv)
        | None, None -> [ f.ToDb value ]

    /// The field's value from one provider value (or NULL) per column of Parts.
    member f.FromColumns(values: obj list) : Result<obj, string> =
        match f.Union, f.Value with
        | Some u, _ -> u.FromColumns values
        | None, Some v ->
            let isNull' (x: obj) = isNull x || x = box DBNull.Value

            if f.IsOption && values |> List.forall isNull' then
                Ok null
            else
                let rec split (fields: FieldMap list) (rest: obj list) =
                    match fields with
                    | [] -> []
                    | sf :: more ->
                        let n = sf.Parts.Length
                        (sf, List.take n rest) :: split more (List.skip n rest)

                let read =
                    split v.Fields values
                    |> List.map (fun (sf, chunk) -> sf.FromColumns chunk |> Result.mapError (fun m -> $"{sf.Name}: {m}"))

                match read |> List.tryPick (function Error m -> Some m | _ -> None) with
                | Some m -> Error(if f.IsOption then $"stored partly ({m}); an absent {f.Name} has every column NULL" else m)
                | None ->
                    let record = v.Make(read |> List.map (function Ok x -> x | Error _ -> null) |> Array.ofList)
                    Ok(if f.IsOption then f.WrapSome record else record)
        | None, None -> f.FromDb values.Head

/// A value (value-records.feature): a record with no table, stored in its
/// owner's row. Each field is a FieldMap whose Column is `<prefix>_<field>`; a
/// nested value is a field with its own layout.
and internal ValueLayout =
    { Type: Type
      Fields: FieldMap list
      Make: obj[] -> obj
      Read: obj -> obj[] }

    member v.Columns: ColumnPart list = v.Fields |> List.collect (fun f -> f.Parts)

    member v.Sub(name: string) = v.Fields |> List.tryFind (fun f -> f.Name = name)

    member v.Requires(column: string) =
        v.Fields |> List.exists (fun f -> f.Parts |> List.exists (fun p -> p.Name = column) && f.Requires column)

/// One stored column of a field.
and internal ColumnPart =
    { Name: string
      ProviderType: Type
      /// The scalar mapping of the values stored in it (for casts).
      Mapping: ScalarMapping option }

/// How a union with fields is stored (union-fields.feature): its case's
/// name in the field's own column (snake case), and each case field in a column
/// `<field column>_<case field>` (a nested union likewise, from its own column).
/// A column belongs to the cases that have a field of that name; every other
/// case stores NULL in it.
and internal UnionLayout =
    { Type: Type
      TagColumn: string
      Cases: UnionCaseLayout[]
      /// Every column: the case-name column first, then the case fields'.
      Columns: ColumnPart list
      Tag: obj -> int }

    member u.Write(value: obj, into: Collections.Generic.Dictionary<string, obj>) =
        let c = u.Cases.[u.Tag value]
        into.[u.TagColumn] <- box c.Stored
        let fields = c.Read value

        c.Parts
        |> List.iteri (fun i p ->
            match p with
            | Leaf f -> into.[f.Column] <- f.ToDb fields.[i]
            | Nested(_, n) -> n.Write(fields.[i], into))

    member u.ToColumns(value: obj) : obj list =
        let values = Collections.Generic.Dictionary<string, obj>()
        u.Write(value, values)

        u.Columns
        |> List.map (fun c ->
            match values.TryGetValue c.Name with
            | true, v -> v
            | _ -> box DBNull.Value)

    /// Every case field stored in a column of its own, a nested union's too.
    member u.Leaves: FieldMap list =
        u.Cases
        |> Array.toList
        |> List.collect (fun c ->
            c.Parts
            |> List.collect (function
                | Leaf f -> [ f ]
                | Nested(_, n) -> n.Leaves))

    /// The layout with `change` made to every case field in a column.
    member u.MapLeaves(change: FieldMap -> FieldMap) : UnionLayout =
        { u with
            Cases =
                u.Cases
                |> Array.map (fun c ->
                    { c with
                        Parts =
                            c.Parts
                            |> List.map (function
                                | Leaf f -> Leaf(change f)
                                | Nested(name, n) -> Nested(name, n.MapLeaves change)) }) }

    /// The columns a case stores values in (its case-name column excluded).
    member u.ColumnsOf(c: UnionCaseLayout) : string list =
        c.Parts
        |> List.collect (function
            | Leaf f -> [ f.Column ]
            | Nested(_, n) -> n.Columns |> List.map (fun p -> p.Name))

    member u.Read(values: Collections.Generic.IDictionary<string, obj>) : Result<obj, string> =
        let isNull' (v: obj) = isNull v || v = box DBNull.Value

        match values.[u.TagColumn] with
        | raw when isNull' raw -> Error $"NULL stored in {u.TagColumn}, which holds the case of {u.Type.Name}"
        | raw ->
            match u.Cases |> Array.tryFind (fun c -> c.Stored = string raw) with
            | None -> Error $"'{raw}' is not a case of {u.Type.Name}"
            | Some c ->
                let own = set (u.ColumnsOf c)

                let stray =
                    u.Columns
                    |> List.tail
                    |> List.filter (fun p -> not (own.Contains p.Name) && not (isNull' values.[p.Name]))

                match stray with
                | p :: _ -> Error $"{p.Name} holds a value, but the case {c.Case.Name} of {u.Type.Name} has no such field"
                | [] ->
                    let fields =
                        c.Parts
                        |> List.map (function
                            | Leaf f -> f.FromDb values.[f.Column] |> Result.mapError (fun m -> $"{c.Case.Name}.{f.Name} ({f.Column}): {m}")
                            | Nested(_, n) -> n.Read values)

                    match fields |> List.tryPick (function Error m -> Some m | _ -> None) with
                    | Some m -> Error m
                    | None -> Ok(c.Make(fields |> List.map (function Ok v -> v | Error _ -> null) |> Array.ofList))

    member u.FromColumns(values: obj list) : Result<obj, string> =
        u.Read(dict (List.zip (u.Columns |> List.map (fun p -> p.Name)) values))

and internal UnionCaseLayout =
    { Case: UnionCaseShape
      /// The case's name as stored: snake case.
      Stored: string
      Parts: UnionPart list
      Make: obj[] -> obj
      Read: obj -> obj[] }

/// A case field: a scalar (a FieldMap whose Column is its column), or a
/// nested union with fields.
and internal UnionPart =
    | Leaf of FieldMap
    /// The case field's name, and its union.
    | Nested of string * UnionLayout

/// A join table's columns for each side (composite-links.feature): the side's
/// one column for a key of one part, else one per part of its key, in the
/// key's order, `<prefix>_<part>`, each with the part it holds.
module internal Joins =
    let private side (column: string) (prefix: string) (key: FieldMap) : (string * ColumnPart) list =
        if key.Composite then
            key.Parts |> List.map (fun p -> $"{prefix}_{p.Name}", p)
        else
            [ column, key.Parts.Head ]

    let owners (j: JoinTable) (ownerKey: FieldMap) = side j.OwnerColumn j.OwnerPrefix ownerKey
    let targets (j: JoinTable) (targetKey: FieldMap) = side j.TargetColumn j.TargetPrefix targetKey

type internal Cardinality =
    | Many
    | OptionalOne
    | RequiredOne

type internal AssocMap =
    { Name: string
      Index: int
      Kind: AssocKind
      Cardinality: Cardinality
      Owner: Type
      Target: Type
      FieldType: Type
      /// Foreign key field name: on the target for HasMany/HasOne, on the owner
      /// for BelongsTo; empty for ManyToMany.
      ForeignKey: string
      Through: JoinTable option
      Ctors: Reflection.AssocCtors
      /// Builds the loaded payload's list (Many) from boxed targets.
      MakeList: obj seq -> obj
      /// Wraps a target in Some (OptionalOne).
      WrapSome: obj -> obj
      /// An owned list: a plain list, always loaded and written with its owner.
      Owned: bool
      /// A linked list: a plain list of shared records, always loaded; only
      /// join-table rows are written through it.
      Linked: bool
      /// For an owned list: the order its items are read in (None: by key).
      Order: Expr option }

type internal SchemaInfo =
    { Type: Type
      Name: string
      Table: string
      Fields: FieldMap[]
      Key: FieldMap
      KeyGenerated: bool
      Version: FieldMap option
      Assocs: AssocMap[]
      Shape: RecordShape
      FieldCount: int }

    member s.Field(name: string) = s.Fields |> Array.find (fun f -> f.Name = name)
    member s.TryField(name: string) = s.Fields |> Array.tryFind (fun f -> f.Name = name)
    member s.TryAssoc(name: string) = s.Assocs |> Array.tryFind (fun a -> a.Name = name)
