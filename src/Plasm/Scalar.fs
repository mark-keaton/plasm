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

        { DomainType = t
          ProviderType = typeof<string>
          ToProvider = fun v -> box toText.[v]
          FromProvider =
            fun v ->
                match fromText.TryGetValue(unbox<string> v) with
                | true, c -> Ok c
                | _ -> Error $"'{v}' is not a known {t.Name}"
          Description = $"{t.Name} as text enum" }
