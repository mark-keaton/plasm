namespace Plasm.CSharp

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Plasm

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Invalid =
    /// A field as C# names it: each part of its path in camel case ("Title" is "title").
    let private camel (field: string) =
        field.Split '.'
        |> Array.map (fun p -> if p = "" then p else string (Char.ToLowerInvariant p.[0]) + p.Substring 1)
        |> String.concat "."

    /// One field that was not valid, and why: input refused before the database.
    let field (field: string) (message: string) : Invalid =
        { Problems = [| { Field = field; Message = message } |] }

    /// Fields that were not valid, each with why, in the order given.
    let fields (problems: (string * string) seq) : Invalid =
        { Problems = [| for field, message in problems -> { Field = field; Message = message } |] }

    /// What the database refused, field by field (TxFailure.InvalidChanges):
    /// each field in camel case; a duplicate of a unique field "is already taken".
    let ofErrors (errors: FieldError list) : Invalid =
        { Problems =
            [| for e in errors ->
                   { Field = camel e.Field
                     Message =
                       match e.Kind with
                       | FieldErrorKind.UniqueConstraint _ -> "is already taken"
                       | _ -> e.Message } |] }

/// A key's parts as text: a typed id unwrapped, through the shape Plasm reads
/// it with (so natively too), a key of several parts field by field.
module internal Keys =
    let rec private parts (key: obj) : string list =
        match key with
        | null -> [ "" ]
        | :? string as s -> [ s ]
        | :? Guid as g -> [ g.ToString "D" ]
        | :? DateTime as d -> [ d.ToString("O", CultureInfo.InvariantCulture) ]
        | :? DateTimeOffset as d -> [ d.ToString("O", CultureInfo.InvariantCulture) ]
        | :? IFormattable as f -> [ f.ToString(null, CultureInfo.InvariantCulture) ]
        | _ ->
            let t = key.GetType()

            if Shape.isUnion t then
                let u = Shape.union t
                u.Cases.[u.Tag key].Read key |> List.ofArray |> List.collect parts
            elif Shape.isRecord t then
                (Shape.record t).Deconstruct key |> List.ofArray |> List.collect parts
            else
                [ string key ]

    let text (key: obj) : string[] = parts key |> Array.ofList

/// Reads a read's or a transaction's result as what C# is given, the
/// records as they are: the seam gives C# their views (and a view can be made
/// in F# with Found.map, Changed.map and the others). A transaction stops in
/// the domain's terms with Stop: Stop.Missing is the outcome's Missing,
/// Stop.Refused its refusal. Plasm's failures are read here: a stale copy is
/// Stale, the database's refusal of fields Invalid, and a database that fails
/// throws UnavailableException.
[<RequireQualifiedAccess>]
module Outcome =
    let private unavailable (what: string) (error: DbError) : 'a =
        match error with
        | DbError.Database failure -> raise (UnavailableException($"{what}: {failure.Message}", failure.Exception))
        | other -> raise (UnavailableException($"{what}: {other}", null))

    /// A failure no outcome has a case for: the database's, or a transaction
    /// that did what its outcome does not expect.
    let private failed (error: TransactionError<'R>) (unexpected: string) : 'a =
        match error.Failure with
        | TxFailure.Failed e -> unavailable $"{error.Step.Action} failed" e
        | TxFailure.Misused message -> raise (UnavailableException($"{error.Step.Action} was misused: {message}", null))
        | _ -> raise (UnavailableException($"{error.Step.Action}: {unexpected}", null))

    let private stale (entity: string) (key: obj) : Stale = { What = entity; Key = Keys.text key }

    /// A read of at most one record (repo.One): the record, or Missing, saying `what`.
    let found (what: string) (result: Result<'S option, DbError>) : Found<'S> =
        match result with
        | Ok(Some s) -> Found s
        | Ok None -> Found { What = what }
        | Error e -> unavailable $"reading {what}" e

    /// A read of many (repo.All): the records, in the query's order, an empty
    /// array when there are none. A read the database fails throws
    /// UnavailableException, "reading `what`", carrying the provider's exception.
    let all (what: string) (result: Result<'S list, DbError>) : 'S[] =
        match result with
        | Ok records -> Array.ofList records
        | Error e -> unavailable $"reading {what}" e

    /// An add (repo.Transact): the record as stored, or Invalid when the
    /// database refuses its fields. An add stopped by the rules, or stale,
    /// throws: an add that can be refused is a request answered, or a change.
    let added (result: Result<'S, TransactionError<'R>>) : Added<'S> =
        match result with
        | Ok s -> Added s
        | Error e ->
            match e.Failure with
            | TxFailure.InvalidChanges(_, errors) -> Added(Invalid.ofErrors errors)
            | _ -> failed e "an add was refused, which Added has no case for"

    /// A request answered (repo.Transact): the answer, Missing for
    /// Stop.Missing, the input's problems for Stop.Refused, or Invalid when the
    /// database refuses its fields.
    let answered (result: Result<'S, TransactionError<Stop<Invalid>>>) : Answered<'S> =
        match result with
        | Ok s -> Answered s
        | Error e ->
            match e.Failure with
            | TxFailure.Rejected(Stop.Missing what) -> Answered { What = what }
            | TxFailure.Rejected(Stop.Refused invalid) -> Answered invalid
            | TxFailure.InvalidChanges(_, errors) -> Answered(Invalid.ofErrors errors)
            | _ -> failed e "a stale copy, which Answered has no case for"

    /// A change (repo.Transact): the record as stored, Missing for
    /// Stop.Missing, the domain's refusal for Stop.Refused, Stale for a copy
    /// older than the stored record, or Invalid when the database refuses its
    /// fields.
    let changed (result: Result<'S, TransactionError<Stop<'R>>>) : Changed<'S, 'R> =
        match result with
        | Ok s -> Changed<'S, 'R>(s)
        | Error e ->
            match e.Failure with
            | TxFailure.Rejected(Stop.Missing what) -> Changed<'S, 'R>({ What = what }: Missing)
            | TxFailure.Rejected(Stop.Refused refusal) -> Changed<'S, 'R>(refusal)
            | TxFailure.Conflict(entity, key) -> Changed<'S, 'R>(stale entity key)
            | TxFailure.InvalidChanges(_, errors) -> Changed<'S, 'R>(Invalid.ofErrors errors)
            | _ -> failed e "an outcome Changed has no case for"

/// A Found's record made into something else, a view of it, its Missing kept.
[<RequireQualifiedAccess>]
module Found =
    let map (view: 'S -> 'T) (found: Found<'S>) : Found<'T> =
        match found.Value with
        | :? Missing as m -> Found m
        | s -> Found(view (s :?> 'S))

/// An Added's record made into something else, its Invalid kept.
[<RequireQualifiedAccess>]
module Added =
    let map (view: 'S -> 'T) (added: Added<'S>) : Added<'T> =
        match added.Value with
        | :? Invalid as i -> Added i
        | s -> Added(view (s :?> 'S))

/// An Answered's answer made into something else, its Missing and Invalid kept.
[<RequireQualifiedAccess>]
module Answered =
    let map (view: 'S -> 'T) (answered: Answered<'S>) : Answered<'T> =
        match answered.Value with
        | :? Missing as m -> Answered m
        | :? Invalid as i -> Answered i
        | s -> Answered(view (s :?> 'S))

/// A change's record made into something else, and its refusal into Refused,
/// for a host that takes the outcome without the seam.
[<RequireQualifiedAccess>]
module Changed =
    let map (view: 'S -> 'T) (changed: Changed<'S, 'R>) : Changed<'T, 'R> =
        match changed.Value with
        | :? Missing as m -> Changed<'T, 'R>(m)
        | :? Stale as s -> Changed<'T, 'R>(s)
        | :? Invalid as i -> Changed<'T, 'R>(i)
        | :? 'R as r -> Changed<'T, 'R>(r)
        | s -> Changed<'T, 'R>(view (s :?> 'S))

    /// The refusal as Refused, its code and its words as `refused` says them:
    /// what the seam does by itself, the code the refusal case's name.
    let refused (refused: 'R -> Refused) (changed: Changed<'T, 'R>) : Changed<'T> =
        match changed.Value with
        | :? Missing as m -> Changed<'T>(m)
        | :? Stale as s -> Changed<'T>(s)
        | :? Invalid as i -> Changed<'T>(i)
        | :? 'R as r -> Changed<'T>(refused r)
        | t -> Changed<'T>(t :?> 'T)

/// A change, the shape every change has: the record a query finds, as the
/// caller saw it (its version), changed as the rule decides, and written.
[<AutoOpen>]
module Change =
    /// The record `query` finds as the caller saw it (`seen`, its version),
    /// changed as `decide` says, and updated: Stop.Missing `what` when it is
    /// not there, Stop.Refused when the rule refuses, a conflict when the copy
    /// seen is older than the stored one.
    let asSeen (what: string) (query: Query<'T>) (seen: int) (decide: 'T -> Result<'T, 'R>) : Tx<'T, Stop<'R>> =
        transaction {
            let! record = Tx.oneAsSeen (query, seen) |> Tx.orReject (Stop.Missing what)
            let! changed = decide record |> Tx.ofResult Stop.Refused
            return! Tx.update (record, changed)
        }

    type Repo with
        /// A change in one call: the record `query` finds as the caller saw
        /// it (`seen`), changed as `decide` says, and written, as a
        /// Changed<'T, 'R>: the record as stored, Missing `what`, the rule's
        /// refusal, Stale, or Invalid (Change.asSeen, Outcome.changed).
        member repo.Change(what: string, query: Query<'T>, seen: int, decide: 'T -> Result<'T, 'R>, ?cancellationToken: CancellationToken) : Task<Changed<'T, 'R>> =
            task {
                let! result = repo.Transact(asSeen what query seen decide, cancellationToken = defaultArg cancellationToken CancellationToken.None)
                return Outcome.changed result
            }
