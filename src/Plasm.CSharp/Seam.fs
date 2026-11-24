/// What a C# host writes in Seam.cs to tell the seam (the step Plasm.CSharp's
/// build targets run before the host's C# is compiled) how its F# data
/// project is seen from C#. The seam reads them from the source; the C#
/// compiler checks each typeof and nameof in them, and leaves the attributes
/// out of the built assembly.
namespace Plasm.CSharp

open System
open System.Diagnostics

/// An F# data type: the seam writes a C# class with the same members, taking
/// plain arguments and answering views.
[<Sealed; AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true); Conditional("PLASM_SEAM_CONFIG")>]
type SeamDataAttribute(dataType: Type) =
    inherit Attribute()
    /// The F# data type.
    member _.DataType = dataType
    /// The C# class's name; the F# type's when not given.
    member val Name: string | null = null with get, set

/// How an F# record or union is seen from C#.
[<Sealed; AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true); Conditional("PLASM_SEAM_CONFIG")>]
type SeamViewAttribute(viewOf: Type) =
    inherit Attribute()
    /// The F# record or union.
    member _.ViewOf = viewOf
    /// The view's name; the F# type's with View after it when not given.
    member val Name: string | null = null with get, set
    /// Record fields the view leaves out.
    member val Omit: string[] = [||] with get, set
    /// Members of the F# record (properties that are not fields) the view carries too.
    member val Add: string[] = [||] with get, set

/// How one case of an F# union is seen from C#.
[<Sealed; AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true); Conditional("PLASM_SEAM_CONFIG")>]
type SeamCaseAttribute(union: Type, case: string) =
    inherit Attribute()
    /// The F# union.
    member _.Union = union
    /// The case's F# name.
    member _.Case = case
    /// The case type's name; the F# case's when not given.
    member val Name: string | null = null with get, set
    /// The JSON "kind"; the name in camel case when not given.
    member val Kind: string | null = null with get, set

/// The host's own types (its request bodies) that the seam's JSON context
/// lists beside the views.
[<Sealed; AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true); Conditional("PLASM_SEAM_CONFIG")>]
type SeamJsonAttribute([<ParamArray>] types: Type[]) =
    inherit Attribute()
    /// The types listed.
    member _.Types = types
