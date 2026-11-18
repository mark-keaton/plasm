/// What a C# host calls: one type, `KanbanData`, over the queries and
/// workflows beside it (Db.fs, Workflows.fs). Its methods take plain arguments and a
/// CancellationToken and return a Task of plain types, so C# writes no query,
/// composes no transaction and matches no F# union:
///
/// - a read that can miss returns a Found: the value, or Missing;
/// - a write that can be refused returns an outcome;
///
/// each a struct of C#'s union shape whose cases are records of their own, so
/// C# switches over it and the compiler names a case it does not handle.
/// Nothing returned is null, an F# option or an F# Result;
/// - a database that cannot be reached throws `KanbanUnavailableException`.
namespace Kanban.Data

open System
open System.Data
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Npgsql
open Plasm
open Plasm.Postgres
open Kanban
open Kanban.Server.Db
open Kanban.Server.Workflows

type CommentView =
    { Author: string
      Body: string
      At: DateTimeOffset }

/// A card's stage, one record per case, each with only the facts its stage
/// has. JSON is written as the case alone, so each says its kind.
/// A class, not a record: a backlog card's stage has no facts, and a record
/// needs a field.
[<Sealed>]
type InBacklog() =
    [<JsonPropertyOrder(-1)>]
    member _.Kind = "backlog"

type InProgress =
    { Assignee: string
      Since: DateTimeOffset }

    [<JsonPropertyOrder(-1)>]
    member _.Kind = "doing"

type WasShipped =
    { At: DateTimeOffset }

    [<JsonPropertyOrder(-1)>]
    member _.Kind = "shipped"

type WasDropped =
    { Reason: string
      At: DateTimeOffset }

    [<JsonPropertyOrder(-1)>]
    member _.Kind = "dropped"

/// Which case of StageView a JSON object is, by its "kind". System.Text.Json's
/// source generator accepts a union-shaped type only if it could read the
/// JSON back, which a C# host never does; deciding by kind asks nothing of
/// the case records.
type StageByKind() =
    inherit JsonTypeClassifierFactory()

    override _.CanClassify(context) = context.DeclaringType = typeof<StageView>

    override _.CreateJsonClassifier(_, _) =
        JsonTypeClassifier(fun reader ->
            // A copy: the classifier only looks ahead.
            let mutable r = reader
            let mutable found: Type = null

            if r.TokenType = JsonTokenType.StartObject then
                let depth = r.CurrentDepth

                while isNull found && r.Read() && r.CurrentDepth > depth do
                    if r.TokenType = JsonTokenType.PropertyName && r.ValueTextEquals "kind" then
                        r.Read() |> ignore

                        found <-
                            match r.GetString() with
                            | "backlog" -> typeof<InBacklog>
                            | "doing" -> typeof<InProgress>
                            | "shipped" -> typeof<WasShipped>
                            | "dropped" -> typeof<WasDropped>
                            | _ -> null
                    elif r.TokenType = JsonTokenType.StartObject || r.TokenType = JsonTokenType.StartArray then
                        r.Skip()

            found)

and [<Struct; Union; JsonUnion(TypeClassifier = typeof<StageByKind>)>] StageView =
    val private value: obj
    new(backlog: InBacklog) = { value = box backlog }
    new(doing: InProgress) = { value = box doing }
    new(shipped: WasShipped) = { value = box shipped }
    new(dropped: WasDropped) = { value = box dropped }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

type CardView =
    { Id: Guid
      Title: string
      Stage: StageView
      Version: int
      Labels: string[]
      Comments: CommentView[] }

type BoardView =
    { Slug: string
      Name: string
      WipLimit: int
      Cards: CardView[] }

type BoardSummary =
    { Slug: string
      Name: string
      WipLimit: int
      Backlog: int
      Doing: int
      Done: int
      /// The titles of the three cards created last, newest first.
      Newest: string[] }

/// What is missing, as "board alpha" or "card".
type Missing = { What: string }

/// A read that can miss: the value, or what is missing.
[<Struct; Union>]
type Found<'T> =
    val private value: obj
    new(found: 'T) = { value = box found }
    new(missing: Missing) = { value = box missing }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// The domain refused: the DomainError case's name, and the domain's own words.
type Refused = { Code: string; Message: string }

/// The caller's copy of the card is older than the stored one.
type Stale = { Card: Guid }

type FieldProblem = { Field: string; Message: string }

/// Input the domain cannot make values of, each field with why.
type Invalid = { Problems: FieldProblem[] }

/// Adding a card: the card, or why not.
[<Struct; Union>]
type AddCardOutcome =
    val private value: obj
    new(card: CardView) = { value = box card }
    new(missing: Missing) = { value = box missing }
    new(invalid: Invalid) = { value = box invalid }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// Changing a card: the card as stored, or why not.
[<Struct; Union>]
type ChangeOutcome =
    val private value: obj
    new(card: CardView) = { value = box card }
    new(missing: Missing) = { value = box missing }
    new(refused: Refused) = { value = box refused }
    new(stale: Stale) = { value = box stale }
    new(invalid: Invalid) = { value = box invalid }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// Creating a board: its summary, or why not.
[<Struct; Union>]
type CreateBoardOutcome =
    val private value: obj
    new(board: BoardSummary) = { value = box board }
    new(invalid: Invalid) = { value = box invalid }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// The database could not be reached or failed; the inner exception, when
/// there is one, is the provider's.
type KanbanUnavailableException(message: string, inner: exn) =
    inherit Exception(message, inner)

module private View =

    let card (card: Card) : CardView =
        let (CardId id) = card.Id

        let stage =
            match card.Stage with
            | Backlog -> StageView(InBacklog())
            | Doing(who, since) -> StageView { Assignee = who.Value; Since = since }
            | Done(Shipped, at) -> StageView { WasShipped.At = at }
            | Done(Dropped reason, at) -> StageView { Reason = reason; At = at }

        { Id = id
          Title = card.Title.Value
          Stage = stage
          Version = card.Version
          Labels = [| for tag in card.Labels -> tag.Name.Value |]
          Comments = [| for c in card.Comments -> { Author = c.Author.Value; Body = c.Body; At = c.At } |] }

    let summary (counts: (BoardId * StageKind * int64) list) (line: BoardLine) : BoardSummary =
        let count kind =
            counts
            |> List.tryPick (fun (board, k, n) -> if board = line.Board.Id && k = kind then Some(int n) else None)
            |> Option.defaultValue 0

        { Slug = line.Board.Slug.Value
          Name = line.Board.Name.Value
          WipLimit = line.Board.WipLimit.Value
          Backlog = count StageKind.Backlog
          Doing = count StageKind.Doing
          Done = count StageKind.Done
          Newest = [| for title in line.Newest -> title.Value |] }

    let invalid (problems: (string * string) list) =
        { Problems = [| for field, message in problems -> { Field = field; Message = message } |] }

    /// A field Plasm names ("WipLimit") as the caller's ("wipLimit").
    let fieldError (e: FieldError) =
        let name = if e.Field = "" then "" else string (Char.ToLowerInvariant e.Field.[0]) + e.Field.Substring 1

        let message =
            match e.Kind with
            | FieldErrorKind.UniqueConstraint _ -> "is already taken"
            | _ -> e.Message

        name, message

    /// The DomainError case's name.
    let code (error: DomainError) =
        match error with
        | WipLimitReached _ -> "WipLimitReached"
        | AlreadyInProgress _ -> "AlreadyInProgress"
        | NotInProgress -> "NotInProgress"
        | CardClosed -> "CardClosed"
        | NotDone -> "NotDone"
        | DropNeedsReason -> "DropNeedsReason"
        | EmptyComment -> "EmptyComment"
        | TooManyLabels _ -> "TooManyLabels"
        | EmptyItem -> "EmptyItem"
        | NoSuchItem _ -> "NoSuchItem"
        | ItemsLeft _ -> "ItemsLeft"
        | BlockNeedsReason -> "BlockNeedsReason"
        | AlreadyBlocked _ -> "AlreadyBlocked"
        | NotBlocked -> "NotBlocked"
        | CardBlocked _ -> "CardBlocked"

/// Why a write stopped, before it is told to C# as one write's outcome.
type private Stopped =
    | Missing of string
    | ByDomain of DomainError
    | StaleCopy of Guid
    | Fields of (string * string) list

[<Sealed>]
type KanbanData private (repo: Repo, clock: unit -> DateTimeOffset, newId: unit -> Guid) =

    let unavailable (what: string) (detail: obj) (inner: exn) =
        KanbanUnavailableException($"{what}: {detail}", inner) |> raise

    let failed (what: string) (error: DbError) =
        match error with
        | DbError.Database failure -> unavailable what failure.Message failure.Exception
        | e -> unavailable what e null

    let read (what: string) (result: Result<'T, DbError>) =
        match result with
        | Ok value -> value
        | Error e -> failed what e

    /// Runs a workflow as the F# server does: serializable, run again on a
    /// serialization failure.
    let transact (card: Guid) (tx: Tx<'T, Refusal>) (ct: CancellationToken) : Task<Result<'T, Stopped>> =
        task {
            let! outcome = repo.Transact(tx, IsolationLevel.Serializable, RetryPolicy.OnSerializationFailure 5, ct)

            return
                outcome
                |> Result.mapError (fun error ->
                    match error.Failure with
                    | TxFailure.Rejected(Domain refusal) -> ByDomain refusal
                    | TxFailure.Rejected(Refusal.Missing what) -> Missing what
                    | TxFailure.Rejected Refusal.StaleCopy
                    | TxFailure.Conflict _ -> Stopped.StaleCopy card
                    | TxFailure.InvalidChanges(_, errors) -> Fields(errors |> List.map View.fieldError)
                    | TxFailure.Failed failure -> failed $"{error.Step.Action} failed" failure
                    | TxFailure.Misused message -> unavailable "A transaction was misused" message null)
        }

    let field (name: string) (parse: 'a -> Result<'b, string>) (value: 'a) =
        parse value |> Result.mapError (fun message -> [ name, message ])

    let change (card: Guid) (version: int) (decide: Board -> int -> Card -> Result<Card, DomainError>) ct : Task<ChangeOutcome> =
        task {
            match! transact card (changeCard (CardId card) version decide) ct with
            | Ok stored -> return ChangeOutcome(View.card stored)
            | Error(Missing what) -> return ChangeOutcome { What = what }
            | Error(ByDomain error) -> return ChangeOutcome { Code = View.code error; Message = DomainError.describe error }
            | Error(Stopped.StaleCopy id) -> return ChangeOutcome { Card = id }
            | Error(Fields problems) -> return ChangeOutcome(View.invalid problems)
        }

    /// Starts the repository over `dataSource`, which checks every query and
    /// the tables first: a query or table that would fail raises
    /// PlasmStartupException here, before anything is served.
    static member OpenAsync(dataSource: NpgsqlDataSource, cancellationToken: CancellationToken) : Task<KanbanData> =
        task {
            let! repo =
                Repo.Start(model, PostgresProvider dataSource, typeof<Refusal>.Assembly, RepoOptions.Default, cancellationToken)

            return KanbanData(repo, (fun () -> DateTimeOffset.UtcNow), Guid.CreateVersion7)
        }

    /// Every board with its card counts and newest titles, by name.
    member _.BoardsAsync(cancellationToken: CancellationToken) : Task<BoardSummary[]> =
        task {
            let! boards = repo.All(Queries.boardsWithCards, cancellationToken = cancellationToken)
            let! counts = repo.All(Queries.cardCounts, cancellationToken = cancellationToken)
            let counts = read "Counting cards failed" counts
            return read "Listing boards failed" boards |> List.map (View.summary counts) |> Array.ofList
        }

    /// A board and its cards, oldest first, each with its comments and
    /// labels; Missing when no board has the slug.
    member _.BoardAsync(slug: string, cancellationToken: CancellationToken) : Task<Found<BoardView>> =
        task {
            let missing = Found<BoardView> { What = $"board {slug}" }

            match Slug.create slug with
            | Error _ -> return missing
            | Ok slug ->
                let! found = repo.One(Queries.boardBySlug slug, cancellationToken = cancellationToken)

                match read "Reading a board failed" found with
                | None -> return missing
                | Some board ->
                    let! cards = repo.All(Queries.cardsOn board.Id, cancellationToken = cancellationToken)

                    return
                        Found
                            { Slug = board.Slug.Value
                              Name = board.Name.Value
                              WipLimit = board.WipLimit.Value
                              Cards = read "Reading a board's cards failed" cards |> List.map View.card |> Array.ofList }
        }

    member _.CreateBoardAsync(slug: string, name: string, wipLimit: int, cancellationToken: CancellationToken) : Task<CreateBoardOutcome> =
        task {
            let parsed =
                match field "slug" Slug.create slug, field "name" Title.create name, field "wipLimit" WipLimit.create wipLimit with
                | Ok s, Ok n, Ok l -> Ok(s, n, l)
                | s, n, l ->
                    let problems (r: Result<_, (string * string) list>) = match r with Error e -> e | Ok _ -> []
                    Error(problems s @ problems n @ problems l)

            match parsed with
            | Error problems -> return CreateBoardOutcome(View.invalid problems)
            | Ok(slug, name, limit) ->
                let board = { Id = BoardId(newId ()); Slug = slug; Name = name; WipLimit = limit }

                match! transact Guid.Empty (createBoard board []) cancellationToken with
                | Ok stored -> return CreateBoardOutcome(View.summary [] { Board = stored; Newest = []; Blocked = [] })
                | Error(Fields problems) -> return CreateBoardOutcome(View.invalid problems)
                | Error other -> return unavailable "Creating a board stopped" other null
        }

    member _.AddCardAsync(board: string, title: string, cancellationToken: CancellationToken) : Task<AddCardOutcome> =
        task {
            match Slug.create board, field "title" Title.create title with
            | Error _, _ -> return AddCardOutcome { What = $"board {board}" }
            | Ok _, Error problems -> return AddCardOutcome(View.invalid problems)
            | Ok slug, Ok title ->
                let id = CardId(newId ())
                let now = clock ()

                match! transact Guid.Empty (addCard slug (fun b -> Card.create id b title now)) cancellationToken with
                | Ok card -> return AddCardOutcome(View.card card)
                | Error(Missing what) -> return AddCardOutcome { What = what }
                | Error(Fields problems) -> return AddCardOutcome(View.invalid problems)
                | Error other -> return unavailable "Adding a card stopped" other null
        }

    /// `assignee` starts work on the card, if the board's limit allows it.
    member _.StartAsync(card: Guid, version: int, assignee: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        match field "assignee" Person.create assignee with
        | Error problems -> Task.FromResult(ChangeOutcome(View.invalid problems))
        | Ok who -> change card version (Card.start who (clock ())) cancellationToken

    member _.RenameAsync(card: Guid, version: int, title: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        match field "title" Title.create title with
        | Error problems -> Task.FromResult(ChangeOutcome(View.invalid problems))
        | Ok title -> change card version (fun _ _ c -> Card.rename title c) cancellationToken
