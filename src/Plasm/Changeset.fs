namespace Plasm

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open Microsoft.FSharp.Quotations

[<RequireQualifiedAccess>]
type internal ChangeAction =
    | Insert
    | Update
    | Delete

/// A proposed change to one field.
[<RequireQualifiedAccess>]
type internal FieldChange =
    /// The field is set to this value (for option fields, a `Some`).
    | Assigned of obj
    /// An optional field is explicitly set to `None`.
    | Cleared

/// A child write that replaces every child the owner has (a delete).
type internal ReplaceScopeDef = | AllChildren

type internal ChildWrite =
    { Field: string
      Replace: ReplaceScopeDef option
      Children: CsState list
      /// For an owned list: the owner's whole new list, each item marked
      /// written (its stored value replaces it, in Children's order) or kept.
      Final: (obj * bool) list option }

and internal LinkMode = | PutLinks

and internal LinkWrite =
    { Field: string
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
                    c.AllErrors |> List.map (fun e -> { e with Field = $"{w.Field}[{i}].{e.Field}" })))

        s.Errors @ childErrors

/// An immutable description of proposed changes to a `'T`, with validation
/// errors. Constructing, validating and inspecting it never touches the database.
[<Sealed>]
type internal Changeset<'T>(state: CsState) =
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
        let error (e: FieldError) =
            match e.Kind with
            | FieldErrorKind.UniqueConstraint name
            | FieldErrorKind.ForeignKeyConstraint name
            | FieldErrorKind.CheckConstraint name -> $"{e.Field} {e.Message} ({name})"
            | _ -> $"{e.Field} {e.Message}"

        let errors =
            if this.IsValid then "" else $""", errors=[{this.Errors |> List.map error |> String.concat "; "}]"""

        $"Changeset<{typeof<'T>.Name}>(action={Reflection.Shown.value (box state.Action)}, changes={Reflection.Shown.value (box (this.Changes |> List.map fst))}, valid={Reflection.Shown.value (box this.IsValid)}{errors})"

module internal Cs =
    let emptyState t data action =
        { Type = t
          Data = data
          Key = None
          ExpectedVersion = None
          Action = action
          Changes = Map.empty
          Errors = []
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

/// Functions for building changesets. All are pure.
type internal Changeset =

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
                s <- { s with Changes = s.Changes.Add(p.Name, after.[i]) }

        Changeset<'T>(s)

    /// A changeset that deletes `value` (respecting its version, if versioned).
    static member delete(value: 'T) : Changeset<'T> =
        Changeset<'T>(Cs.emptyState typeof<'T> (Some(box value)) ChangeAction.Delete)

    /// The version an update expects, for a copy that came back from a client.
    static member expectVersion(version: int) =
        fun (cs: Changeset<'T>) -> Cs.map cs (fun s -> { s with ExpectedVersion = Some version })

    // -- Constraints ----------------------------------------------------------
    // A unique, foreign-key or column check constraint with PostgreSQL's default
    // name (<table>_<column>_key, _fkey, _check) comes back as its field's
    // error with nothing declared here (constraints.feature).
