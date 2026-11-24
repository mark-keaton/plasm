/// What a C# host is given (docs/csharp.md): the cases it switches over, each
/// a record of its own, and the unions of them, each a struct of C#'s union
/// shape ([<Union>], one constructor per case, IUnion.Value), so the C#
/// compiler names a case a switch does not handle. Nothing here is null, an
/// F# option or an F# Result.
namespace Plasm.CSharp

open System
open System.Runtime.CompilerServices

/// The caller's copy is older than the stored record, or the record is gone
/// since it was read: the record type, and its key's parts as text in key
/// order (one part for a typed id, a part per field for a key of several).
type Stale = { What: string; Key: string[] }

/// The rules refused: the reason's name, and why in the rules' own words.
type Refused = { Code: string; Message: string }

/// A field of the input that was not valid, named as C# names it (camel case), and why.
type FieldProblem = { Field: string; Message: string }

/// Input that was not valid, each field with why.
type Invalid = { Problems: FieldProblem[] }

/// What is missing ("equipment"). (Declared after Stale, so that
/// `{ What = "equipment" }` is a Missing.)
type Missing = { What: string }

/// A read that can miss: the value, or what is missing.
[<Struct; Union>]
type Found<'T when 'T: not null> =
    val private value: obj
    new(found: 'T) = { value = found :> obj }
    new(missing: Missing) = { value = missing :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// An add: the record as stored, or the input that was not valid.
[<Struct; Union>]
type Added<'T when 'T: not null> =
    val private value: obj
    new(added: 'T) = { value = added :> obj }
    new(invalid: Invalid) = { value = invalid :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// A request about something that must be there, with input to check (a card
/// added to a board, a page of a board): the answer, Missing, or Invalid.
[<Struct; Union>]
type Answered<'T when 'T: not null> =
    val private value: obj
    new(answer: 'T) = { value = answer :> obj }
    new(missing: Missing) = { value = missing :> obj }
    new(invalid: Invalid) = { value = invalid :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// A change: the record as stored, or why not. Every change can be Invalid,
/// since the database may refuse what it writes field by field.
[<Struct; Union>]
type Changed<'T when 'T: not null> =
    val private value: obj
    new(changed: 'T) = { value = changed :> obj }
    new(missing: Missing) = { value = missing :> obj }
    new(refused: Refused) = { value = refused :> obj }
    new(stale: Stale) = { value = stale :> obj }
    new(invalid: Invalid) = { value = invalid :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// A change whose rules refuse in the domain's own words, as an F# data
/// layer answers it: the record as stored, Missing, the domain's refusal
/// (`'R`, a union), Stale, or Invalid. The seam gives C# a Changed of the
/// record's view, the refusal a Refused whose Code is its case's name and
/// whose Message is the refusal's own `Message`.
[<Struct; Union>]
type Changed<'T, 'R when 'T: not null and 'R: not null> =
    val private value: obj
    new(changed: 'T) = { value = changed :> obj }
    new(missing: Missing) = { value = missing :> obj }
    new(refused: 'R) = { value = refused :> obj }
    new(stale: Stale) = { value = stale :> obj }
    new(invalid: Invalid) = { value = invalid :> obj }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// The database could not be reached or failed: what was being done, and the
/// provider's exception, when there is one. ASP.NET Core answers it with a 500.
/// Outcome's functions throw it; a data layer need not make one.
type UnavailableException
    /// `message`, what was being done and why it failed ("reading notes:
    /// ..."); `inner`, the provider's exception, or null when the failure is
    /// Plasm's own (a transaction misused, an outcome with no case for it).
    (message: string, inner: exn | null) =
    inherit Exception(message, inner)
