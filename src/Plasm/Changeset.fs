namespace Plasm

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open Microsoft.FSharp.Quotations

[<RequireQualifiedAccess>]
type ChangeAction =
    | Insert
    | Update
    | Delete

/// A proposed change to one field.
[<RequireQualifiedAccess>]
type FieldChange =
    /// The field is set to this value (for option fields, a `Some`).
    | Assigned of obj
    /// An optional field is explicitly set to `None`.
    | Cleared

type internal ConstraintMap =
    { Name: string
      Field: string
      Kind: string -> FieldErrorKind
      Message: string }

type internal ReplaceScopeDef =
    | AllChildren
    | MatchingChildren of QueryDef

type internal ChildWrite =
    { Assoc: string
      Replace: ReplaceScopeDef option
      Children: CsState list }

and internal LinkMode =
    | PutLinks
    | AddLinks
    | RemoveLinks

and internal LinkWrite =
    { Assoc: string
      Mode: LinkMode
      Targets: obj list }

and internal CsState =
    { Type: Type
      Data: obj option
      /// For key-only changesets built from partial data.
      Key: (string * obj) option
      ExpectedVersion: int option
      Action: ChangeAction
      Changes: Map<string, obj>
      Errors: FieldError list
      Constraints: ConstraintMap list
      Children: ChildWrite list
      /// Many-to-many link changes (join-table rows only).
      Links: LinkWrite list }

    member s.FieldIndex(name: string) =
        (Reflection.recordShape s.Type).Fields |> Array.findIndex (fun p -> p.Name = name)

    /// Current value of a field: the change if any, else the original data.
    member s.Current(name: string) : obj option =
        match s.Changes.TryFind name with
        | Some v -> Some v
        | None ->
            s.Data
            |> Option.map (fun d -> (Reflection.recordShape s.Type).Deconstruct d |> fun vs -> vs.[s.FieldIndex name])

    member s.Proposed: obj option =
        s.Data
        |> Option.map (fun d ->
            let shape = Reflection.recordShape s.Type
            let values = shape.Deconstruct d

            for KeyValue(k, v) in s.Changes do
                values.[s.FieldIndex k] <- v

            shape.Construct values)

    member s.AllErrors: FieldError list =
        let childErrors =
            s.Children
            |> List.collect (fun w ->
                w.Children
                |> List.indexed
                |> List.collect (fun (i, c) ->
                    c.AllErrors |> List.map (fun e -> { e with Field = $"{w.Assoc}[{i}].{e.Field}" })))

        s.Errors @ childErrors

/// Scope of an authoritative child replacement. Children outside the scope are
/// never affected, so replacing with a filtered collection must name its filter.
[<Sealed>]
type ReplaceScope<'C> internal (def: ReplaceScopeDef) =
    member internal _.Def = def

[<RequireQualifiedAccess>]
module ReplaceScope =
    /// Every existing child of the parent is subject to the replacement.
    let allChildren<'C> : ReplaceScope<'C> = ReplaceScope<'C>(AllChildren)

    /// Only existing children matching `query`'s filters are subject to the
    /// replacement; others are left untouched.
    let matching (query: Query<'C>) : ReplaceScope<'C> =
        ReplaceScope<'C>(MatchingChildren query.Def)

/// An immutable description of proposed changes to a `'T`, with validation
/// errors. Constructing, validating and inspecting it never touches the database.
[<Sealed>]
type Changeset<'T> internal (state: CsState) =
    member internal _.State = state

    member _.Action = state.Action

    /// The original value, when known (absent for key-only changesets).
    member _.Data: 'T option = state.Data |> Option.map unbox<'T>

    /// The original value with changes applied, when the original is known.
    member _.Proposed: 'T option = state.Proposed |> Option.map unbox<'T>

    /// Field changes, in declaration order.
    member _.Changes: (string * FieldChange) list =
        let shape = Reflection.recordShape state.Type

        shape.Fields
        |> Array.choose (fun p ->
            state.Changes.TryFind p.Name
            |> Option.map (fun v ->
                if Reflection.isOption p.PropertyType && isNull v then
                    p.Name, FieldChange.Cleared
                else
                    p.Name, FieldChange.Assigned v))
        |> Array.toList

    /// Errors on this changeset and its children (child fields are reported
    /// as e.g. "Lines[1].Quantity").
    member _.Errors: FieldError list = state.AllErrors

    member this.IsValid = this.Errors.IsEmpty

    override this.ToString() =
        $"Changeset<{typeof<'T>.Name}>(action=%A{state.Action}, changes=%A{this.Changes |> List.map fst}, valid={this.IsValid})"

module internal Cs =
    let emptyState t data action =
        { Type = t
          Data = data
          Key = None
          ExpectedVersion = None
          Action = action
          Changes = Map.empty
          Errors = []
          Constraints = []
          Children = []
          Links = [] }

    let inline map (cs: Changeset<'T>) f : Changeset<'T> = Changeset<'T>(f cs.State)

    let field (e: Expr) = (Paths.property "changeset field" e).Name

    let fieldEquals (a: obj) (b: obj) = Object.Equals(a, b)

    /// Records a change, dropping it when it equals the original (Ecto semantics).
    let put (s: CsState) (name: string) (value: obj) =
        let original =
            s.Data
            |> Option.map (fun d -> (Reflection.recordShape s.Type).Deconstruct d |> fun vs -> vs.[s.FieldIndex name])

        match original with
        | Some o when fieldEquals o value -> { s with Changes = s.Changes.Remove name }
        | _ -> { s with Changes = s.Changes.Add(name, value) }

    let addError (s: CsState) name kind msg =
        { s with
            Errors =
                s.Errors
                @ [ { Field = name
                      Message = msg
                      Kind = kind } ] }

    /// The value a validation should inspect: always the proposed value for
    /// inserts; only changed values for updates (Ecto semantics).
    let validationTarget (s: CsState) name =
        match s.Action with
        | ChangeAction.Insert -> s.Current name
        | _ -> s.Changes.TryFind name

    let unwrap (t: Type) (v: obj) =
        if Reflection.isOption t then
            Reflection.getOptionValue t v
        elif isNull v then
            None
        else
            Some v

    let isBlank (t: Type) (v: obj) =
        match unwrap t v with
        | None -> true
        | Some(:? string as s) -> String.IsNullOrWhiteSpace s
        | Some _ -> false

    let parsers: IDictionary<Type, string -> obj option> =
        let inv = CultureInfo.InvariantCulture

        let tryP (f: string -> bool * 'a) =
            fun s ->
                match f s with
                | true, v -> Some(box v)
                | _ -> None

        dict
            [ typeof<string>, (fun s -> Some(box s))
              typeof<int16>, tryP (fun s -> Int16.TryParse(s, NumberStyles.Integer, inv))
              typeof<int>, tryP (fun s -> Int32.TryParse(s, NumberStyles.Integer, inv))
              typeof<int64>, tryP (fun s -> Int64.TryParse(s, NumberStyles.Integer, inv))
              typeof<decimal>, tryP (fun s -> Decimal.TryParse(s, NumberStyles.Number, inv))
              typeof<float>, tryP (fun s -> Double.TryParse(s, NumberStyles.Float, inv))
              typeof<bool>, tryP (fun s -> Boolean.TryParse s)
              typeof<Guid>, tryP (fun s -> Guid.TryParse s)
              typeof<DateTime>, tryP (fun s -> DateTime.TryParse(s, inv, DateTimeStyles.RoundtripKind))
              typeof<DateTimeOffset>, tryP (fun s -> DateTimeOffset.TryParse(s, inv, DateTimeStyles.RoundtripKind))
              typeof<DateOnly>, tryP (fun s -> DateOnly.TryParse(s, inv))
              typeof<TimeOnly>, tryP (fun s -> TimeOnly.TryParse(s, inv)) ]

    let lookup (input: IReadOnlyDictionary<string, string>) (name: string) =
        let candidates =
            [ name
              Char.ToLowerInvariant(name.[0]).ToString() + name.Substring 1
              Paths.snakeCase name ]

        candidates
        |> List.tryPick (fun k ->
            match input.TryGetValue k with
            | true, v -> Some v
            | _ -> None)

    let castWith (s: CsState) (name: string) (fieldType: Type) (raw: string) (parse: string -> Result<obj, string>) =
        if Reflection.isOption fieldType && String.IsNullOrWhiteSpace raw then
            put s name null
        else
            match parse raw with
            | Ok v ->
                let v =
                    if Reflection.isOption fieldType && not (Reflection.isOption (v.GetType())) then
                        Reflection.makeSome fieldType v
                    else
                        v

                put s name v
            | Error msg -> addError s name FieldErrorKind.CastFailed msg

/// Functions for building and validating changesets. All are pure.
type Changeset =

    /// A changeset that inserts `value`. Fields the schema marks as generated
    /// are ignored and returned from the database.
    static member insert(value: 'T) : Changeset<'T> =
        Changeset<'T>(Cs.emptyState typeof<'T> (Some(box value)) ChangeAction.Insert)

    /// An empty update changeset for an existing value.
    static member update(original: 'T) : Changeset<'T> =
        Changeset<'T>(Cs.emptyState typeof<'T> (Some(box original)) ChangeAction.Update)

    /// An update changeset whose changes are the fields that `edit` alters:
    /// `order |> Changeset.edit (fun o -> { o with Status = Paid })`.
    /// Fields `edit` leaves alone are not changes; an option set to None is Cleared.
    static member edit (edit: 'T -> 'T) (original: 'T) : Changeset<'T> =
        let shape = Reflection.recordShape typeof<'T>
        let before = shape.Deconstruct(box original)
        let after = shape.Deconstruct(box (edit original))
        let mutable s = Cs.emptyState typeof<'T> (Some(box original)) ChangeAction.Update

        for i, p in Array.indexed shape.Fields do
            if not (Cs.fieldEquals before.[i] after.[i]) then
                if Reflection.isAssoc p.PropertyType then
                    s <-
                        Cs.addError
                            s
                            p.Name
                            (FieldErrorKind.Custom "relationship")
                            "relationship changes must use Changeset.putChildren or Changeset.replaceChildren"
                else
                    s <- { s with Changes = s.Changes.Add(p.Name, after.[i]) }

        Changeset<'T>(s)

    /// An update addressed only by primary key, for when the full record is not
    /// in hand (e.g. after a projection). Only fields set explicitly are written;
    /// no other field can become a default or NULL.
    static member forKey([<ReflectedDefinition>] key: Expr<'T -> 'K>, value: 'K) : Changeset<'T> =
        Changeset<'T>(
            { Cs.emptyState typeof<'T> None ChangeAction.Update with
                Key = Some(Cs.field key, box value) }
        )

    /// A changeset that deletes `value` (respecting its version, if versioned).
    static member delete(value: 'T) : Changeset<'T> =
        Changeset<'T>(Cs.emptyState typeof<'T> (Some(box value)) ChangeAction.Delete)

    /// For key-only changesets of versioned records: the version the update expects.
    static member expectVersion(version: int) =
        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> { s with ExpectedVersion = Some version })

    /// Proposes a value for a field.
    static member set([<ReflectedDefinition>] field: Expr<'T -> 'F>, value: 'F) =
        let name = Cs.field field
        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> Cs.put s name (box value))

    /// Proposes clearing an optional field (distinct from leaving it unchanged).
    static member clear([<ReflectedDefinition>] field: Expr<'T -> 'F option>) =
        let name = Cs.field field
        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> Cs.put s name null)

    /// Accepts one field from external input (keys may be the field name,
    /// camelCase or snake_case). Fields not cast are ignored, so identifiers and
    /// versions cannot be changed through this path unless explicitly cast.
    /// Blank input clears an optional field.
    static member cast(input: IReadOnlyDictionary<string, string>, [<ReflectedDefinition>] field: Expr<'T -> 'F>) =
        let name = Cs.field field
        let fieldType = typeof<'F>

        let scalar =
            if Reflection.isOption fieldType then
                Reflection.elementType fieldType
            else
                fieldType

        let parser =
            match Cs.parsers.TryGetValue scalar with
            | true, p -> p
            | _ ->
                raise (
                    PlasmConfigurationException
                        $"Changeset.cast has no parser for {Reflection.typeName scalar} ({name}); use Changeset.castWith"
                )

        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                match Cs.lookup input name with
                | None -> s
                | Some raw ->
                    Cs.castWith s name fieldType raw (fun r ->
                        match parser r with
                        | Some v -> Ok v
                        | None -> Error "is invalid"))

    /// Like cast, with an explicit parser for domain types.
    static member castWith
        (input: IReadOnlyDictionary<string, string>, [<ReflectedDefinition>] field: Expr<'T -> 'F>, parse: string -> Result<'F, string>)
        =
        let name = Cs.field field

        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                match Cs.lookup input name with
                | None -> s
                | Some raw -> Cs.castWith s name typeof<'F> raw (fun r -> parse r |> Result.map box))

    /// The proposed change to a field, if any.
    static member tryGetChange([<ReflectedDefinition>] field: Expr<'T -> 'F>) =
        let name = Cs.field field
        fun (cs: Changeset<'T>) -> cs.State.Changes.TryFind name |> Option.map unbox<'F>

    /// The field's value after changes (change, else original), when known.
    static member getField([<ReflectedDefinition>] field: Expr<'T -> 'F>) =
        let name = Cs.field field
        fun (cs: Changeset<'T>) -> cs.State.Current name |> Option.map unbox<'F>

    // -- Validation (pure) ----------------------------------------------------

    /// The field must have a non-blank value (checked against the proposed
    /// value whether or not it changed).
    static member validateRequired([<ReflectedDefinition>] field: Expr<'T -> 'F>) =
        let name = Cs.field field

        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                let target =
                    match s.Data with
                    | Some _ -> s.Current name
                    | None -> s.Changes.TryFind name

                match target with
                | Some v when not (Cs.isBlank typeof<'F> v) -> s
                | None when s.Data.IsNone -> s
                | _ -> Cs.addError s name FieldErrorKind.Required "can't be blank")

    static member private validateValue<'T, 'F>(name, check: 'F -> (FieldErrorKind * string) option) =
        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                // Absent values (None / null) are left to validateRequired.
                match Cs.validationTarget s name with
                | Some v when (Cs.unwrap typeof<'F> v).IsSome ->
                    match check (unbox<'F> v) with
                    | Some(k, m) -> Cs.addError s name k m
                    | None -> s
                | _ -> s)

    /// String length bounds (inclusive).
    static member validateLength([<ReflectedDefinition>] field: Expr<'T -> string>, ?min: int, ?max: int) =
        Changeset.validateValue<'T, string> (
            Cs.field field,
            fun v ->
                match min, max with
                | Some m, _ when v.Length < m -> Some(FieldErrorKind.Length, $"should be at least {m} character(s)")
                | _, Some m when v.Length > m -> Some(FieldErrorKind.Length, $"should be at most {m} character(s)")
                | _ -> None
        )

    /// String length bounds for an optional field (None is not checked).
    /// A separate name rather than an overload, so the field lambda's record type
    /// is inferred from the piped changeset.
    static member validateOptionalLength([<ReflectedDefinition>] field: Expr<'T -> string option>, ?min: int, ?max: int) =
        Changeset.validateValue<'T, string option> (
            Cs.field field,
            fun v ->
                match v, min, max with
                | Some s, Some m, _ when s.Length < m -> Some(FieldErrorKind.Length, $"should be at least {m} character(s)")
                | Some s, _, Some m when s.Length > m -> Some(FieldErrorKind.Length, $"should be at most {m} character(s)")
                | _ -> None
        )

    /// Inclusive numeric (or any comparable) bounds.
    static member validateRange([<ReflectedDefinition>] field: Expr<'T -> 'F>, ?min: 'F, ?max: 'F) =
        Changeset.validateValue<'T, 'F> (
            Cs.field field,
            fun v ->
                match min, max with
                | Some m, _ when compare v m < 0 -> Some(FieldErrorKind.Range, $"must be greater than or equal to {m}")
                | _, Some m when compare v m > 0 -> Some(FieldErrorKind.Range, $"must be less than or equal to {m}")
                | _ -> None
        )

    static member validateInclusion([<ReflectedDefinition>] field: Expr<'T -> 'F>, allowed: 'F list) =
        Changeset.validateValue<'T, 'F> (
            Cs.field field,
            fun v ->
                if List.contains v allowed then
                    None
                else
                    Some(FieldErrorKind.Inclusion, "is invalid")
        )

    static member validateFormat([<ReflectedDefinition>] field: Expr<'T -> string>, pattern: string) =
        let rx = Regex(pattern, RegexOptions.CultureInvariant)

        Changeset.validateValue<'T, string> (
            Cs.field field,
            fun v -> if rx.IsMatch v then None else Some(FieldErrorKind.Format, "has invalid format")
        )

    /// FieldErrorKind.Custom validation: return Some message to reject the proposed value.
    static member validate([<ReflectedDefinition>] field: Expr<'T -> 'F>, check: 'F -> string option) =
        Changeset.validateValue<'T, 'F> (Cs.field field, fun v -> check v |> Option.map (fun m -> FieldErrorKind.Custom "validate", m))

    static member addError([<ReflectedDefinition>] field: Expr<'T -> 'F>, message: string) =
        let name = Cs.field field
        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> Cs.addError s name (FieldErrorKind.Custom "error") message)

    // -- Constraints ----------------------------------------------------------

    /// Translates a violation of the named unique constraint into a field error.
    /// Violations of constraints not declared remain database failures.
    static member uniqueConstraint([<ReflectedDefinition>] field: Expr<'T -> 'F>, constraintName: string, ?message: string) =
        let c =
            { Name = constraintName
              Field = Cs.field field
              Kind = FieldErrorKind.UniqueConstraint
              Message = defaultArg message "has already been taken" }

        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> { s with Constraints = s.Constraints @ [ c ] })

    static member foreignKeyConstraint([<ReflectedDefinition>] field: Expr<'T -> 'F>, constraintName: string, ?message: string) =
        let c =
            { Name = constraintName
              Field = Cs.field field
              Kind = FieldErrorKind.ForeignKeyConstraint
              Message = defaultArg message "does not exist" }

        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> { s with Constraints = s.Constraints @ [ c ] })

    static member checkConstraint([<ReflectedDefinition>] field: Expr<'T -> 'F>, constraintName: string, ?message: string) =
        let c =
            { Name = constraintName
              Field = Cs.field field
              Kind = FieldErrorKind.CheckConstraint
              Message = defaultArg message "is invalid" }

        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> { s with Constraints = s.Constraints @ [ c ] })

    // -- Associations ---------------------------------------------------------

    /// Writes the given children with the parent: changesets built with
    /// `Changeset.insert` are inserted (their foreign key is set to the parent);
    /// update changesets update the existing child with that identity. Children
    /// not listed are left untouched.
    static member putChildren([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, children: Changeset<'C> list) =
        let name = (Paths.property "putChildren" assoc).Name

        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                { s with
                    Children =
                        s.Children
                        @ [ { Assoc = name
                              Replace = None
                              Children = children |> List.map (fun c -> c.State) } ] })

    static member private Link(name, mode, targets: 'C list) =
        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                { s with
                    Links =
                        s.Links
                        @ [ { Assoc = name
                              Mode = mode
                              Targets = List.map box targets } ] })

    /// Makes `targets` the complete set of records linked through a
    /// many-to-many relationship: missing join-table rows are inserted and
    /// others deleted. The targets must already be stored; they are not
    /// written. The returned record has the relationship loaded with them.
    static member putLinks([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, targets: 'C list) =
        Changeset.Link((Paths.property "putLinks" assoc).Name, PutLinks, targets)

    /// Links stored records through a many-to-many relationship, keeping
    /// existing links. The returned record's relationship is NotLoaded, since
    /// the full set was not read.
    static member addLinks([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, targets: 'C list) =
        Changeset.Link((Paths.property "addLinks" assoc).Name, AddLinks, targets)

    /// Removes links to the given records (the records themselves are kept).
    /// The returned record's relationship is NotLoaded.
    static member removeLinks([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, targets: 'C list) =
        Changeset.Link((Paths.property "removeLinks" assoc).Name, RemoveLinks, targets)

    /// Makes `children` the complete set of children within `scope`. Existing
    /// children in scope that are not listed are handled by the relationship's
    /// OnReplace policy; the default policy rejects the write.
    static member replaceChildren
        ([<ReflectedDefinition>] assoc: Expr<'T -> Assoc<'C list>>, scope: ReplaceScope<'C>, children: Changeset<'C> list)
        =
        let name = (Paths.property "replaceChildren" assoc).Name

        fun (cs: Changeset<'T>) ->
            Cs.map cs (fun s ->
                { s with
                    Children =
                        s.Children
                        @ [ { Assoc = name
                              Replace = Some scope.Def
                              Children = children |> List.map (fun c -> c.State) } ] })
