namespace Plasm.CSharp

open System
open System.Globalization
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

/// Reads a read's or a transaction's result as what C# is given. Each project
/// maps its own reason for stopping a transaction (TxFailure.Rejected) to the
/// case it means; Plasm's failures are read here: a stale copy is Stale, the
/// database's refusal of fields Invalid, and a database that fails throws
/// UnavailableException.
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

    /// A read of at most one record (repo.One): its view, or Missing, saying `what`.
    let found (what: string) (view: 'S -> 'T) (result: Result<'S option, DbError>) : Found<'T> =
        match result with
        | Ok(Some s) -> Found(view s)
        | Ok None -> Found { What = what }
        | Error e -> unavailable $"reading {what}" e

    /// An add (repo.Transact): the record's view, or Invalid when the database
    /// refuses its fields. An add stopped by the rules, or stale, throws: an
    /// add that can be refused is a request answered, or a change.
    let added (view: 'S -> 'T) (result: Result<'S, TransactionError<'R>>) : Added<'T> =
        match result with
        | Ok s -> Added(view s)
        | Error e ->
            match e.Failure with
            | TxFailure.InvalidChanges(_, errors) -> Added(Invalid.ofErrors errors)
            | _ -> failed e "an add was refused, which Added has no case for"

    /// A request answered (repo.Transact): the view, what `rejected` makes of
    /// the transaction's own reason for stopping (Missing or Invalid), or
    /// Invalid when the database refuses its fields.
    let answered (view: 'S -> 'T) (rejected: 'R -> Answered<'T>) (result: Result<'S, TransactionError<'R>>) : Answered<'T> =
        match result with
        | Ok s -> Answered(view s)
        | Error e ->
            match e.Failure with
            | TxFailure.Rejected reason -> rejected reason
            | TxFailure.InvalidChanges(_, errors) -> Answered(Invalid.ofErrors errors)
            | _ -> failed e "a stale copy, which Answered has no case for"

    /// A change (repo.Transact): the record's view as stored, what `rejected`
    /// makes of the transaction's own reason for stopping (Missing or
    /// Refused), Stale for a copy older than the stored record, or Invalid
    /// when the database refuses its fields.
    let changed (view: 'S -> 'T) (rejected: 'R -> Changed<'T>) (result: Result<'S, TransactionError<'R>>) : Changed<'T> =
        match result with
        | Ok s -> Changed(view s)
        | Error e ->
            match e.Failure with
            | TxFailure.Rejected reason -> rejected reason
            | TxFailure.Conflict(entity, key) -> Changed(stale entity key)
            | TxFailure.InvalidChanges(_, errors) -> Changed(Invalid.ofErrors errors)
            | _ -> failed e "an outcome Changed has no case for"
