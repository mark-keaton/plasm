namespace Plasm

open System
open Microsoft.FSharp.Reflection

/// How a domain scalar type is stored as a provider-native value.
///
/// Mappings are explicit: a type without a built-in or configured mapping is a
/// configuration error, never an implicit lossy conversion.
type ScalarMapping =
    internal
        { DomainType: Type
          ProviderType: Type
          ToProvider: obj -> obj
          FromProvider: obj -> Result<obj, string>
          /// For a type stored as text from a finite table (Scalar.enum): the
          /// stored names in F#'s comparison order (a union's declaration order),
          /// so that SQL orders and compares the values as F# does.
          Ordered: string list option
          /// For a Scalar.enum mapping: each case's name and stored name, in F#'s
          /// order; empty otherwise.
          Cases: (string * string) list
          /// The database type the column has, when it is not the provider
          /// type's own: a PostgreSQL enum type, as its name's parts
          /// (`["order_status"]` or `["sales"; "order_status"]`). Values are sent
          /// as text cast to it and read back as text.
          StoreType: string list option
          Description: string }

    member this.Domain = this.DomainType
    member this.Provider = this.ProviderType

[<RequireQualifiedAccess>]
module Scalar =

    /// Provider-native types supported without configuration.
    let builtIn: Set<string> =
        [ typeof<int16>
          typeof<int>
          typeof<int64>
          typeof<string>
          typeof<bool>
          typeof<decimal>
          typeof<float>
          typeof<float32>
          typeof<Guid>
          typeof<DateTime>
          typeof<DateTimeOffset>
          typeof<DateOnly>
          typeof<TimeOnly>
          typeof<TimeSpan>
          typeof<byte[]> ]
        |> List.map (fun t -> t.FullName)
        |> Set.ofList

    let internal isBuiltIn (t: Type) = builtIn.Contains t.FullName

    let private checkProvider<'P> () =
        if not (isBuiltIn typeof<'P>) then
            raise (
                PlasmConfigurationException
                    $"Scalar mappings must target a built-in provider type; {typeof<'P>.Name} is not one."
            )

    /// Maps a wrapper type (typically a single-case union such as
    /// `type OrderId = OrderId of int64`) to a provider type with a total
    /// constructor.
    let wrap<'Domain, 'Provider> (create: 'Provider -> 'Domain) (unwrap: 'Domain -> 'Provider) : ScalarMapping =
        checkProvider<'Provider> ()

        { DomainType = typeof<'Domain>
          ProviderType = typeof<'Provider>
          ToProvider = fun v -> box (unwrap (unbox<'Domain> v))
          FromProvider = fun v -> Ok(box (create (unbox<'Provider> v)))
          Ordered = None
          Cases = []
          StoreType = None
          Description = $"{typeof<'Domain>.Name} as {typeof<'Provider>.Name}" }

    /// Maps a type whose constructor validates its input. A stored value the
    /// constructor rejects produces a structured materialization error; the
    /// constructor is never bypassed.
    let tryWrap<'Domain, 'Provider>
        (create: 'Provider -> Result<'Domain, string>)
        (unwrap: 'Domain -> 'Provider)
        : ScalarMapping =
        checkProvider<'Provider> ()

        { DomainType = typeof<'Domain>
          ProviderType = typeof<'Provider>
          ToProvider = fun v -> box (unwrap (unbox<'Domain> v))
          FromProvider = fun v -> create (unbox<'Provider> v) |> Result.map box
          Ordered = None
          Cases = []
          StoreType = None
          Description = $"{typeof<'Domain>.Name} as validated {typeof<'Provider>.Name}" }

    /// Maps a fieldless union (or any type with a finite set of values) to text
    /// using an explicit table. Every case must be listed; unknown stored text is a
    /// materialization error.
    let enum<'Domain when 'Domain: equality> (cases: ('Domain * string) list) : ScalarMapping =
        let t = typeof<'Domain>

        if FSharpType.IsUnion(t, true) then
            let declared = FSharpType.GetUnionCases(t, true)

            if declared |> Array.exists (fun c -> c.GetFields().Length > 0) then
                raise (PlasmConfigurationException $"Scalar.enum requires a fieldless union; {t.Name} has cases with fields.")

            if declared.Length <> (cases |> List.map fst |> List.distinct |> List.length) then
                raise (PlasmConfigurationException $"Scalar.enum for {t.Name} must list every case exactly once.")

        let names = cases |> List.map snd

        if List.length (List.distinct names) <> List.length names then
            raise (PlasmConfigurationException $"Scalar.enum for {t.Name} has duplicate stored names.")

        let toText = dict (cases |> List.map (fun (c, n) -> box c, n))
        let fromText = dict (cases |> List.map (fun (c, n) -> n, box c))

        // F#'s order of the values (for a union, its declaration order, unless it
        // defines its own comparison). None when the type has no comparison.
        let sorted =
            try
                cases
                |> List.sortWith (fun (a, _) (b, _) -> LanguagePrimitives.GenericComparer.Compare(box a, box b))
                |> Some
            with _ ->
                None

        let caseName (c: 'Domain) =
            if FSharpType.IsUnion(t, true) then
                (fst (FSharpValue.GetUnionFields(box c, t, true))).Name
            else
                string c

        { DomainType = t
          ProviderType = typeof<string>
          ToProvider = fun v -> box toText.[v]
          FromProvider =
            fun v ->
                match fromText.TryGetValue(unbox<string> v) with
                | true, c -> Ok c
                | _ -> Error $"'{v}' is not a known {t.Name}"
          Ordered = sorted |> Option.map (List.map snd)
          Cases = sorted |> Option.defaultValue cases |> List.map (fun (c, n) -> caseName c, n)
          StoreType = None
          Description = $"{t.Name} as text enum" }

    let private identifier = Text.RegularExpressions.Regex "^[A-Za-z_][A-Za-z0-9_]*$"

    /// Stores a `Scalar.enum` mapping as a PostgreSQL enum type instead of text:
    /// `Scalar.enum [ ... ] |> Scalar.asPgEnum "order_status"` (or
    /// `"sales.order_status"`). Values are sent as text cast to the type and read
    /// back as text, so the driver needs no enum mapping. The type's labels must
    /// be the stored names in the cases' order, which the schema check verifies;
    /// the database then orders and compares the values as F# does.
    let asPgEnum (typeName: string) (mapping: ScalarMapping) : ScalarMapping =
        let parts = if isNull typeName then [] else typeName.Split '.' |> List.ofArray

        if parts.IsEmpty || parts.Length > 2 || not (parts |> List.forall identifier.IsMatch) then
            raise (
                PlasmConfigurationException
                    $"Scalar.asPgEnum: \"{typeName}\" is not an enum type name; use letters, digits and underscores, optionally after a schema and a dot."
            )

        if mapping.Cases.IsEmpty then
            raise (PlasmConfigurationException $"Scalar.asPgEnum applies to a Scalar.enum mapping; {mapping.Description} is not one.")

        { mapping with
            StoreType = Some parts
            // The enum type orders by its labels, which are in the cases' order.
            Ordered = None
            Description = $"{mapping.DomainType.Name} as enum type {typeName}" }
