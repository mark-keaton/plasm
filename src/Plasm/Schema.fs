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
/// and the target's primary keys.
type internal JoinTable =
    { Table: string
      OwnerColumn: string
      TargetColumn: string }

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

/// Schema definition state (opaque).
type SchemaDef =
    internal
        { Type: Type
          Table: string
          Key: (PropertyInfo * bool) option
          Columns: Map<string, string>
          Version: PropertyInfo option
          Generated: Set<string>
          /// Fields whose values are unique in the table (constraints.feature).
          Unique: Set<string>
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

type internal FieldMap =
    { Name: string
      Index: int
      Column: string
      FieldType: Type
      /// Domain scalar type with any option removed.
      ScalarType: Type
      IsOption: bool
      Generated: bool
      /// Its values are unique in the table (`unique` in the schema).
      Unique: bool
      /// The record type whose key this field holds: a typed id that is the
      /// key type of exactly one other schema (inferred by Model.build).
      References: Type option
      Mapping: ScalarMapping option
      ProviderType: Type
      /// Some(x) for option fields (precomputed; identity otherwise).
      WrapSome: obj -> obj
      /// Payload of an option field value (precomputed).
      ReadOption: obj -> obj option
      /// For a union with fields: how it is stored across columns (Column is
      /// then its case-name column). None for every other field.
      Union: UnionLayout option }

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

    /// The columns the field is stored in: one, or a union's several.
    member f.Parts: ColumnPart list =
        match f.Union with
        | None ->
            [ { Name = f.Column
                ProviderType = f.ProviderType
                Mapping = f.Mapping } ]
        | Some u -> u.Columns

    /// The field's value as one provider value (or DBNull) per column of Parts.
    member f.ToColumns(value: obj) : obj list =
        match f.Union with
        | None -> [ f.ToDb value ]
        | Some u -> u.ToColumns value

    /// The field's value from one provider value (or NULL) per column of Parts.
    member f.FromColumns(values: obj list) : Result<obj, string> =
        match f.Union with
        | None -> f.FromDb values.Head
        | Some u -> u.FromColumns values

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
    { Case: Microsoft.FSharp.Reflection.UnionCaseInfo
      /// The case's name as stored: snake case.
      Stored: string
      Parts: UnionPart list
      Make: obj[] -> obj
      Read: obj -> obj[] }

/// A case field: a scalar (a FieldMap whose Column is its column), or a
/// nested union with fields.
and internal UnionPart =
    | Leaf of FieldMap
    | Nested of PropertyInfo * UnionLayout

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
      Shape: Reflection.RecordShape
      FieldCount: int }

    member s.Field(name: string) = s.Fields |> Array.find (fun f -> f.Name = name)
    member s.TryField(name: string) = s.Fields |> Array.tryFind (fun f -> f.Name = name)
    member s.TryAssoc(name: string) = s.Assocs |> Array.tryFind (fun a -> a.Name = name)
