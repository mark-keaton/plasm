/// What the seam is made of: how each F# type crosses to C#, and the C# the
/// tool writes from it.
namespace Plasm.Seam

open System.Collections.Generic

/// A mistake the developer can fix: printed as an MSBuild error with its code
/// (SEAMnnn) and, when known, the place it was written; the build stops.
exception SeamError of Code: string * Text: string * Origin: string option

/// The generated contract would change, and the build is not to change it
/// (PlasmSeamDrift=fail): the lock file, and each change, a line.
exception ContractChanged of Lock: string * Changes: string list

module Seam =
    let fail code text = raise (SeamError(code, text, None))
    let failAt code origin text = raise (SeamError(code, text, origin))

/// How one type crosses the seam.
type Cross =
    { /// The C# type the host sees, without a nullable mark.
      Cs: string
      /// The domain type, as C# spells it.
      Domain: string
      Nullable: bool
      ValueType: bool
      /// The host sees the domain type itself.
      Identity: bool
      /// A domain expression as a view expression.
      Out: string -> string
      /// A view expression as a domain expression; None when the type is only ever returned.
      In: (string -> string) option
      /// Why In is None.
      NoIn: string option }

    member c.CsFull = if c.Nullable then c.Cs + "?" else c.Cs

type Field = { Name: string; Member: string; Cross: Cross }

type RecordView =
    { Name: string
      Domain: string
      Display: string
      Fields: List<Field>
      /// The F# record made from its view, each of its fields in order, once
      /// a parameter takes it (and C# can give every field).
      mutable Into: string list option }

type CaseView =
    { Name: string
      /// The case's F# name.
      Case: string
      Kind: string
      /// The domain's tag for the case, empty for a union of one case.
      DomainCase: string
      /// The nested class a case with fields is, to cast to.
      Cast: string option
      Fields: List<Field> }

type UnionView =
    { Name: string
      Domain: string
      Display: string
      Cases: List<CaseView> }

/// One closed outcome, such as ChangeResult<RepairJob>, and its view ChangeResult<JobView>.
type OutcomeView =
    { Cs: string
      Domain: string
      Arms: (string * Cross) list }

/// An outcome type as declared, such as ChangeResult<T>: each case's C# type, None for T.
type OutcomeShape =
    { Name: string
      Cs: string
      Cases: (string option * string) list }

type Returns =
    | Nothing
    | Value
    | Task
    | TaskOf
    | ValueTaskOf

type Member =
    { Static: bool
      Property: bool
      Name: string
      TypeParameters: string list
      Constraints: string
      Parameters: (string * Cross) list
      Returns: Returns
      Result: Cross option }

/// A data type's Store, found in the data project: the C# that reaches it
/// (Starter.Data.BookingStore.store), and whether its database is a file.
type StoreView = { Access: string; Display: string; File: bool }

type DataClass =
    { Name: string
      Domain: string
      Display: string
      Members: Member list
      Store: StoreView option }

/// A domain refusal (the `'R` of a ChangeResult<'T, 'R>), as C# is given it: a
/// Refused whose Code is its case's name, known at build, and whose Message
/// is its own.
type RefusalView =
    { Domain: string
      Display: string
      /// Each case's name, by tag.
      Codes: string list }

type SeamModel =
    { Namespace: string
      Records: List<RecordView>
      Unions: List<UnionView>
      Outcomes: List<OutcomeView>
      Shapes: List<OutcomeShape>
      Data: List<DataClass>
      Refusals: List<RefusalView>
      /// Each F# doc comment's summary, by its documentation id (T:Shop.Order).
      Docs: Dictionary<string, string>
      /// Every type the JSON context lists, as C# spells it.
      Json: List<string>
      /// The host's own types it lists too, as Seam.cs writes them.
      HostJson: List<string> }
