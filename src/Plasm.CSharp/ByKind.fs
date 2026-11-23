namespace Plasm.CSharp

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization

/// Which case of a union-shaped type a JSON object is, by its "kind": the
/// classifier System.Text.Json's source generator needs to take a union whose
/// every case is an object (else SYSLIB1227). Each case writes its kind
/// first, as a constant property; a project names its classifier once:
///
///     type ConditionByKind() =
///         inherit ByKind(typeof<ConditionView>, [ "bookable", typeof<Bookable>; "withdrawn", typeof<Withdrawn> ])
///
///     and [<Struct; Union; JsonUnion(TypeClassifier = typeof<ConditionByKind>)>] ConditionView = ...
[<AbstractClass>]
type ByKind(union: Type, cases: (string * Type) seq) =
    inherit JsonTypeClassifierFactory()
    let byKind = Dictionary<string, Type>(dict cases)

    override _.CanClassify(context) = context.DeclaringType = union

    override _.CreateJsonClassifier(_, _) =
        JsonTypeClassifier(fun reader ->
            let mutable r = reader
            let mutable found: Type | null = null

            if r.TokenType = JsonTokenType.StartObject then
                let depth = r.CurrentDepth

                while isNull found && r.Read() && r.CurrentDepth > depth do
                    if r.TokenType = JsonTokenType.PropertyName && r.ValueTextEquals "kind" then
                        r.Read() |> ignore

                        match r.GetString() with
                        | null -> ()
                        | kind ->
                            match byKind.TryGetValue kind with
                            | true, t -> found <- t
                            | _ -> ()
                    elif r.TokenType = JsonTokenType.StartObject || r.TokenType = JsonTokenType.StartArray then
                        r.Skip()

            found)
