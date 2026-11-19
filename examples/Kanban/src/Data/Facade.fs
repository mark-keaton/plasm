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
open System.Collections.Generic
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

/// A board, for a caller that reads its cards apart (a GraphQL field).
type BoardNode =
    { Id: Guid
      Slug: string
      Name: string
      WipLimit: int }

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

/// Sending every card in progress on a board back: how many went back.
type SentBack = { Cards: int }

/// Sending a board's cards back: how many, or the board is missing.
[<Struct; Union>]
type SendAllBackOutcome =
    val private value: obj
    new(sent: SentBack) = { value = box sent }
    new(missing: Missing) = { value = box missing }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// A card someone is working on, with its board.
type WorkView =
    { Board: string
      BoardName: string
      Card: Guid
      Title: string
      Since: DateTimeOffset }

/// One page of a board's finished cards, newest first, numbered from 1.
type DonePage =
    { Cards: CardView[]
      Page: int
      Total: int
      Pages: int }

/// A page of finished cards, or why not.
[<Struct; Union>]
type DonePageOutcome =
    val private value: obj
    new(page: DonePage) = { value = box page }
    new(missing: Missing) = { value = box missing }
    new(invalid: Invalid) = { value = box invalid }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// An account, as a host signing someone in reads it: the name, and the
/// hash the host made of the password when the account was registered.
type AccountView = { Name: string; PasswordHash: string }

/// Registering an account: the account, or why not.
[<Struct; Union>]
type RegisterOutcome =
    val private value: obj
    new(account: AccountView) = { value = box account }
    new(invalid: Invalid) = { value = box invalid }
    member this.Value = this.value

    interface IUnion with
        member this.Value = this.value

/// The database could not be reached or failed; the inner exception, when
/// there is one, is the provider's.
type KanbanUnavailableException(message: string, inner: exn) =
    inherit Exception(message, inner)

module private View =

    let id (CardId id) = id
    let title (title: Title) = title.Value

    let stage (stage: Stage) =
        match stage with
        | Backlog -> StageView(InBacklog())
        | Doing(who, since) -> StageView { Assignee = who.Value; Since = since }
        | Done(Shipped, at) -> StageView { WasShipped.At = at }
        | Done(Dropped reason, at) -> StageView { Reason = reason; At = at }

    let labels (labels: Tag list) = [| for tag in labels -> tag.Name.Value |]
    let comments (comments: Comment list) = [| for c in comments -> { Author = c.Author.Value; Body = c.Body; At = c.At } |]

    let card (card: Card) : CardView =
        { Id = id card.Id
          Title = title card.Title
          Stage = stage card.Stage
          Version = card.Version
          Labels = labels card.Labels
          Comments = comments card.Comments }

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

/// What a program building queries at run time (a GraphQL host) may filter
/// and order cards by: each field once, a quotation over the domain's Card,
/// checked when KanbanData opens. Values are given as text and numbers; a
/// title searched as its text (Title.Value), a stage as backlog, doing or done.
[<RequireQualifiedAccess>]
module CardFields =

    let private stage (text: string) =
        match text with
        | "backlog" -> Ok StageKind.Backlog
        | "doing" -> Ok StageKind.Doing
        | "done" -> Ok StageKind.Done
        | other -> Error $"no stage is \"{other}\": backlog, doing or done"

    /// Oldest first: the order of a page whose request names none.
    let created = QueryField.ordered "created" <@ fun (c: Card) -> c.Created @>

    /// What a board's cards are paged per (BuildQuery.pageEach): the board.
    let board = <@ fun (c: Card) -> c.Board @>

    let all: QueryField<Card> list =
        [ QueryField.text "title" <@ fun (c: Card) -> c.Title.Value @>
          created
          QueryField.ordered "version" <@ fun (c: Card) -> c.Version @>
          QueryField.equalVia "stage" <@ fun (c: Card) -> kind c @> stage "backlog" ]

/// What a program building queries at run time (a GraphQL host) may read of a
/// card: each field of CardView once, by its name, a quotation over the
/// domain's Card and the conversion to CardView's value (View), checked when
/// KanbanData opens. A request reads only the fields it chose.
[<RequireQualifiedAccess>]
module CardRead =
    let id = ReadField.via "id" <@ fun (c: Card) -> c.Id @> View.id
    let title = ReadField.via "title" <@ fun (c: Card) -> c.Title @> View.title
    let stage = ReadField.via "stage" <@ fun (c: Card) -> c.Stage @> View.stage
    let version = ReadField.value "version" <@ fun (c: Card) -> c.Version @>
    let labels = ReadField.via "labels" <@ fun (c: Card) -> c.Labels @> View.labels
    let comments = ReadField.via "comments" <@ fun (c: Card) -> c.Comments @> View.comments

    let all: ReadField<Card> list = [ id; title; stage; version; labels; comments ]

    /// The board a card is on: read beside a board's cards, which are
    /// grouped by it, and not a field a caller reads.
    let internal board = ReadField.via "board" <@ fun (c: Card) -> c.Board @> (fun (BoardId b) -> b)

/// Whether opening checks every query, the tables and the declared fields
/// against the database. Run is the default; Skip is for a host whose tests
/// open it with Run over the same migrations, and that cannot spend the check
/// at each cold start (a Lambda function: docs/aot.md, "Kanban's C# function").
type StartupChecks =
    | Run = 0
    | Skip = 1

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

    /// A workflow that changes one card, run, and told as a ChangeOutcome.
    let changed (card: Guid) (tx: Tx<Card, Refusal>) ct : Task<ChangeOutcome> =
        task {
            match! transact card tx ct with
            | Ok stored -> return ChangeOutcome(View.card stored)
            | Error(Missing what) -> return ChangeOutcome { What = what }
            | Error(ByDomain error) -> return ChangeOutcome { Code = View.code error; Message = DomainError.describe error }
            | Error(Stopped.StaleCopy id) -> return ChangeOutcome { Card = id }
            | Error(Fields problems) -> return ChangeOutcome(View.invalid problems)
        }

    let change (card: Guid) (version: int) (decide: Board -> int -> Card -> Result<Card, DomainError>) ct : Task<ChangeOutcome> =
        changed card (changeCard (CardId card) version decide) ct

    static member private Open(dataSource: NpgsqlDataSource, onSql: Action<string> option, time: TimeProvider, checks: StartupChecks, cancellationToken: CancellationToken) : Task<KanbanData> =
        task {
            let checking =
                match checks with
                | StartupChecks.Run -> RepoOptions.Default
                | StartupChecks.Skip -> RepoOptions.Unchecked
                | other -> raise (ArgumentOutOfRangeException(nameof checks, other, "is neither Run nor Skip"))

            let options =
                match onSql with
                | None -> checking
                | Some onSql -> { checking with OnCommand = Some(fun e -> onSql.Invoke e.Sql) }

            let! repo = Repo.Start(model, PostgresProvider dataSource, typeof<Refusal>.Assembly, options, cancellationToken)

            if checks = StartupChecks.Run then
                repo.CheckFields CardFields.all
                repo.CheckFields CardRead.all

            return KanbanData(repo, time.GetUtcNow, Guid.CreateVersion7)
        }

    /// Starts the repository over `dataSource`, which checks every query and
    /// the tables first: a query or table that would fail raises
    /// PlasmStartupException here, before anything is served. Every change
    /// takes its time from `time`, the application's one clock.
    static member OpenAsync(dataSource: NpgsqlDataSource, time: TimeProvider, cancellationToken: CancellationToken) : Task<KanbanData> =
        KanbanData.Open(dataSource, None, time, StartupChecks.Run, cancellationToken)

    /// As OpenAsync, `onSql` given the SQL of each command sent (a log).
    static member OpenAsync(dataSource: NpgsqlDataSource, onSql: Action<string>, time: TimeProvider, cancellationToken: CancellationToken) : Task<KanbanData> =
        KanbanData.Open(dataSource, Some onSql, time, StartupChecks.Run, cancellationToken)

    /// As OpenAsync, on the system's clock.
    static member OpenAsync(dataSource: NpgsqlDataSource, cancellationToken: CancellationToken) : Task<KanbanData> =
        KanbanData.Open(dataSource, None, TimeProvider.System, StartupChecks.Run, cancellationToken)

    /// As OpenAsync, on the system's clock, checking at start only when
    /// `checks` is Run.
    static member OpenAsync(dataSource: NpgsqlDataSource, checks: StartupChecks, cancellationToken: CancellationToken) : Task<KanbanData> =
        KanbanData.Open(dataSource, None, TimeProvider.System, checks, cancellationToken)

    /// As OpenAsync, on the system's clock, `onSql` given the SQL of each command sent.
    static member OpenAsync(dataSource: NpgsqlDataSource, onSql: Action<string>, cancellationToken: CancellationToken) : Task<KanbanData> =
        KanbanData.Open(dataSource, Some onSql, TimeProvider.System, StartupChecks.Run, cancellationToken)

    /// Every card, as `refine` makes the query (a GraphQL request's where and
    /// order, built from CardFields.all), reading only the `fields` chosen
    /// (CardRead): a list a card owns is read only when chosen.
    member _.CardsAsync(fields: ReadField<Card> seq, refine: Func<Query<Card>, Query<Card>>, cancellationToken: CancellationToken) : Task<Selected<Card>[]> =
        task {
            let! cards = repo.All(refine.Invoke(Query.from tableOf<Card>) |> BuildQuery.select (List.ofSeq fields), cancellationToken = cancellationToken)
            return read "Reading cards failed" cards |> Array.ofList
        }

    /// Every board, by name.
    member _.BoardNodesAsync(cancellationToken: CancellationToken) : Task<BoardNode[]> =
        task {
            let! boards = repo.All(plasm { for b in tableOf<Board> do orderBy b.Name }, cancellationToken = cancellationToken)

            return
                read "Listing boards failed" boards
                |> List.map (fun b ->
                    let (BoardId id) = b.Id
                    { Id = id; Slug = b.Slug.Value; Name = b.Name.Value; WipLimit = b.WipLimit.Value })
                |> Array.ofList
        }

    /// The cards of every board in `boards`, as `refine` makes the query, in
    /// one query for them all, reading the `fields` chosen; each board's in
    /// the query's order.
    member _.CardsOfBoardsAsync(boards: Guid seq, fields: ReadField<Card> seq, refine: Func<Query<Card>, Query<Card>>, cancellationToken: CancellationToken) : Task<IReadOnlyDictionary<Guid, Selected<Card>[]>> =
        task {
            let ids = [ for id in boards -> BoardId id ]
            let query = plasm { for c in tableOf<Card> do where (List.contains c.Board ids) }
            let! cards = repo.All(refine.Invoke query |> BuildQuery.select (CardRead.board :: List.ofSeq fields), cancellationToken = cancellationToken)
            let byBoard = Dictionary<Guid, ResizeArray<Selected<Card>>>()

            for id in boards do
                byBoard.[id] <- ResizeArray()

            for c in read "Reading boards' cards failed" cards do
                byBoard.[c.Get<Guid> CardRead.board].Add c

            return Dictionary(byBoard |> Seq.map (fun kv -> KeyValuePair(kv.Key, kv.Value.ToArray()))) :> IReadOnlyDictionary<_, _>
        }

    /// How many of each board's cards the query `refine` makes reads, in one
    /// command for every board (a page's total per board); `refine` gives a
    /// where alone, since the count is grouped by board.
    member _.CountCardsOfBoardsAsync(boards: Guid seq, refine: Func<Query<Card>, Query<Card>>, cancellationToken: CancellationToken) : Task<IReadOnlyDictionary<Guid, int>> =
        task {
            let ids = [ for id in boards -> BoardId id ]
            let query = refine.Invoke(plasm { for c in tableOf<Card> do where (List.contains c.Board ids) })

            let! counts =
                repo.All(
                    plasm {
                        for c in query do
                            groupBy c.Board
                            select (c.Board, Agg.count ())
                    },
                    cancellationToken = cancellationToken
                )

            let byBoard = Dictionary<Guid, int>()

            for id in boards do
                byBoard.[id] <- 0

            for BoardId board, n in read "Counting boards' cards failed" counts do
                byBoard.[board] <- int n

            return byBoard :> IReadOnlyDictionary<_, _>
        }

    /// How many cards the query `refine` makes reads.
    member _.CountCardsAsync(refine: Func<Query<Card>, Query<Card>>, cancellationToken: CancellationToken) : Task<int> =
        task {
            let! n = repo.Count(refine.Invoke(Query.from tableOf<Card>), cancellationToken = cancellationToken)
            return int (read "Counting cards failed" n)
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

    /// A card with its comments and labels; Missing when no card has the id.
    member _.CardAsync(card: Guid, cancellationToken: CancellationToken) : Task<Found<CardView>> =
        task {
            let! found = repo.One(Queries.cardById (CardId card), cancellationToken = cancellationToken)

            match read "Reading a card failed" found with
            | None -> return Found<CardView> { What = "card" }
            | Some card -> return Found(View.card card)
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
                let board: Board = { Id = BoardId(newId ()); Slug = slug; Name = name; WipLimit = limit }

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

    /// Registers `name` with `password`, if the domain accepts both (Person,
    /// Password) and no account has the name; `hash` is the host's, given the
    /// password, and only what it returns is stored.
    member _.RegisterAsync(name: string, password: string, hash: Func<string, string>, cancellationToken: CancellationToken) : Task<RegisterOutcome> =
        task {
            match field "name" Person.create name, field "password" Password.create password with
            | Ok name, Ok password ->
                let account: Account =
                    { Id = AccountId(newId ())
                      Name = name
                      PasswordHash = hash.Invoke password.Value }

                match! transact Guid.Empty (Tx.insert account) cancellationToken with
                | Ok stored -> return RegisterOutcome { Name = stored.Name.Value; PasswordHash = stored.PasswordHash }
                | Error(Fields problems) -> return RegisterOutcome(View.invalid problems)
                | Error other -> return unavailable "Registering stopped" other null
            | n, p ->
                let problems (r: Result<_, (string * string) list>) = match r with Error e -> e | Ok _ -> []
                return RegisterOutcome(View.invalid (problems n @ problems p))
        }

    /// The account named `name`; Missing when there is none, or when no
    /// account could have the name.
    member _.AccountAsync(name: string, cancellationToken: CancellationToken) : Task<Found<AccountView>> =
        task {
            let missing = Found<AccountView> { What = "account" }

            match Person.create name with
            | Error _ -> return missing
            | Ok name ->
                let! found = repo.One(Queries.accountNamed name, cancellationToken = cancellationToken)

                match read "Reading an account failed" found with
                | None -> return missing
                | Some account -> return Found { Name = account.Name.Value; PasswordHash = account.PasswordHash }
        }

    member _.ShipAsync(card: Guid, version: int, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.finish Shipped (clock ()) c) cancellationToken

    /// Drops the card; the domain refuses a drop without a reason.
    member _.DropAsync(card: Guid, version: int, reason: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.finish (Dropped reason) (clock ()) c) cancellationToken

    member _.SendBackAsync(card: Guid, version: int, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.sendBack c) cancellationToken

    member _.ReopenAsync(card: Guid, version: int, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.reopen c) cancellationToken

    member _.BlockAsync(card: Guid, version: int, reason: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.block reason (clock ()) c) cancellationToken

    member _.UnblockAsync(card: Guid, version: int, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.unblock c) cancellationToken

    member _.AddItemAsync(card: Guid, version: int, text: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.addItem text c) cancellationToken

    member _.TickAsync(card: Guid, version: int, text: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        change card version (fun _ _ c -> Card.tick text c) cancellationToken

    /// `author` comments on the card. A comment needs no version: it is added
    /// to the card as it is stored.
    member _.CommentAsync(card: Guid, author: string, body: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        match field "author" Person.create author with
        | Error problems -> Task.FromResult(ChangeOutcome(View.invalid problems))
        | Ok who -> changed card (addComment (CardId card) (Card.comment (CommentId(newId ())) who body (clock ()))) cancellationToken

    /// Makes `labels` the card's whole set of labels; each must be a label,
    /// and a problem is named by its place (labels[1]).
    member _.RelabelAsync(card: Guid, version: int, labels: string seq, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        let parsed = labels |> Seq.mapi (fun i text -> field $"labels[{i}]" Label.create text) |> List.ofSeq

        match parsed |> List.collect (function Error e -> e | Ok _ -> []) with
        | [] ->
            let labels = parsed |> List.choose Result.toOption
            changed card (relabel (CardId card) version (newId >> LabelId) (Card.relabel labels)) cancellationToken
        | problems -> Task.FromResult(ChangeOutcome(View.invalid problems))

    /// Every card in progress on the board back to the backlog, in one
    /// bulk update (Workflows.sendAllBack).
    member _.SendAllBackAsync(board: string, cancellationToken: CancellationToken) : Task<SendAllBackOutcome> =
        task {
            match Slug.create board with
            | Error _ -> return SendAllBackOutcome { What = $"board {board}" }
            | Ok slug ->
                match! transact Guid.Empty (sendAllBack slug) cancellationToken with
                | Ok n -> return SendAllBackOutcome { SentBack.Cards = n }
                | Error(Missing what) -> return SendAllBackOutcome { What = what }
                | Error other -> return unavailable "Sending cards back stopped" other null
        }

    /// Sends every card in progress for longer than the domain allows, now,
    /// back to the backlog, on every board: how many (docs/kanban-host.md).
    member _.SendStaleBackAsync(cancellationToken: CancellationToken) : Task<SentBack> =
        task {
            match! transact Guid.Empty (sendStaleBack (clock ())) cancellationToken with
            | Ok n -> return { SentBack.Cards = n }
            | Error other -> return unavailable "Sending stale cards back stopped" other null
        }

    /// The cards `person` is working on, oldest start first.
    member _.WorkOfAsync(person: string, cancellationToken: CancellationToken) : Task<WorkView[]> =
        task {
            match Person.create person with
            | Error _ -> return [||]
            | Ok who ->
                let! rows = repo.All(Queries.workOf who, cancellationToken = cancellationToken)

                return
                    [| for row in read "Reading someone's work failed" rows ->
                           let (CardId card) = row.Card

                           { Board = row.Slug.Value
                             BoardName = row.Board.Value
                             Card = card
                             Title = row.Title.Value
                             // A card with an assignee has a start (the table's check).
                             Since = defaultArg row.Since DateTimeOffset.MinValue } |]
        }

    /// Page `page` of the board's finished cards, `size` to a page (1 to 50).
    member _.DonePageAsync(board: string, page: int, size: int, cancellationToken: CancellationToken) : Task<DonePageOutcome> =
        task {
            let problems =
                [ if page < 1 then "page", "must be at least 1"
                  if size < 1 || size > 50 then "size", "must be between 1 and 50" ]

            match problems, Slug.create board with
            | _ :: _, _ -> return DonePageOutcome(View.invalid problems)
            | [], Error _ -> return DonePageOutcome { What = $"board {board}" }
            | [], Ok slug ->
                let! found = repo.One(Queries.boardBySlug slug, cancellationToken = cancellationToken)

                match read "Reading a board failed" found with
                | None -> return DonePageOutcome { What = $"board {slug.Value}" }
                | Some b ->
                    let! total = repo.Count(Queries.doneOn b.Id, cancellationToken = cancellationToken)
                    let! rows = repo.All(Queries.finishedOn b.Id ((page - 1) * size) size, cancellationToken = cancellationToken)
                    let total = read "Counting done cards failed" total

                    return
                        DonePageOutcome
                            { Cards = read "Reading done cards failed" rows |> List.map View.card |> Array.ofList
                              Page = page
                              Total = int total
                              Pages = int ((total + int64 size - 1L) / int64 size) }
        }

    member _.RenameAsync(card: Guid, version: int, title: string, cancellationToken: CancellationToken) : Task<ChangeOutcome> =
        match field "title" Title.create title with
        | Error problems -> Task.FromResult(ChangeOutcome(View.invalid problems))
        | Ok title -> change card version (fun _ _ c -> Card.rename title c) cancellationToken
