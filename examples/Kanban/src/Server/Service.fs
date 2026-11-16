/// The API's implementation: parse the input into domain values, run a
/// workflow, turn the outcome into what the client is told.
///
/// Every failure Plasm can report arrives here as a value, and each one is
/// mapped by a `match` the compiler checks: invalid input, a refusal by the
/// domain, a missing row, a stale copy, a database that cannot be reached.
module Kanban.Server.Service

open System
open System.Data
open System.Threading
open Microsoft.Extensions.Logging
open Plasm
open Kanban
open Kanban.Api
open Kanban.Server.Db
open Kanban.Server.Workflows

/// What the service needs from the outside world. One instance serves every
/// request: a `Repo` holds no connection and no per-request state.
type Deps =
    { Repo: Repo
      Clock: unit -> DateTimeOffset
      NewId: unit -> Guid
      Log: ILogger }

/// Who is making the request, and how that changes. ASP.NET Core's cookie
/// authentication is behind it in the application; a test supplies its own.
type Session =
    { Who: unit -> Person option
      SignIn: Person -> System.Threading.Tasks.Task
      SignOut: unit -> System.Threading.Tasks.Task }

module private Input =

    let field (name: string) (parse: 'a -> Result<'b, string>) (value: 'a) : Result<'b, (string * string) list> =
        parse value |> Result.mapError (fun message -> [ name, message ])

    /// Every value, or the errors of all, each under `name[index]`.
    let each (name: string) (parse: 'a -> Result<'b, string>) (values: 'a seq) : Result<'b list, (string * string) list> =
        let parsed = values |> Seq.mapi (fun i value -> field $"{name}[{i}]" parse value) |> List.ofSeq

        match parsed |> List.collect (function Error e -> e | Ok _ -> []) with
        | [] -> Ok(parsed |> List.choose Result.toOption)
        | errors -> Error errors

    /// Both values, or the errors of both.
    let both (a: Result<'a, 'e list>) (b: Result<'b, 'e list>) : Result<'a * 'b, 'e list> =
        match a, b with
        | Ok x, Ok y -> Ok(x, y)
        | Error e, Ok _
        | Ok _, Error e -> Error e
        | Error e1, Error e2 -> Error(e1 @ e2)

module private View =

    let stage (stage: Stage) : StageView =
        match stage with
        | Backlog -> InBacklog
        | Doing(who, since) -> InProgress(who.Value, since)
        | Done(Shipped, at) -> WasShipped at
        | Done(Dropped reason, at) -> WasDropped(reason, at)

    let comment (comment: Comment) : CommentView =
        { Author = comment.Author.Value
          Body = comment.Body
          At = comment.At }

    let card (card: Card) : CardView =
        let (CardId id) = card.Id

        { Id = id
          Title = card.Title.Value
          Stage = stage card.Stage
          Version = card.Version
          Labels = [| for tag in card.Labels -> tag.Name.Value |]
          Comments = card.Comments |> List.map comment |> Array.ofList }

    let cards (cards: Card list) : CardView[] = cards |> List.map card |> Array.ofList

    let summary (counts: (BoardId * StageKind * int64) list) (row: Board, newest: Card list) : BoardSummary =
        let count kind =
            counts
            |> List.tryPick (fun (board, k, n) -> if board = row.Id && k = kind then Some(int n) else None)
            |> Option.defaultValue 0

        { Slug = row.Slug.Value
          Name = row.Name.Value
          WipLimit = row.WipLimit.Value
          Backlog = count StageKind.Backlog
          Doing = count StageKind.Doing
          Done = count StageKind.Done
          Newest = [| for card in newest -> card.Title.Value |] }

    /// "WipLimit" (the domain's field, which Plasm names) as the client's "wipLimit".
    let fieldError (e: FieldError) =
        let name = if e.Field = "" then "" else string (Char.ToLowerInvariant e.Field.[0]) + e.Field.Substring 1
        // The application's own wording for a value someone else already has.
        let message =
            match e.Kind with
            | FieldErrorKind.UniqueConstraint _ -> "is already taken"
            | _ -> e.Message

        name, message

/// Logs what went wrong and tells the client only that something did.
let private unavailable (deps: Deps) (what: string) (detail: obj) : ApiError =
    deps.Log.LogError("{What}: {Detail}", what, detail)
    Unavailable "The boards cannot be reached right now. Try again in a moment."

let private ofTransactionError (deps: Deps) (error: TransactionError<Refusal>) : ApiError =
    match error.Failure with
    | TxFailure.Rejected(Domain refusal) -> Rejected refusal
    | TxFailure.Rejected(Missing what) -> NotFound what
    | TxFailure.Rejected StaleCopy -> Stale
    // The stored version changed between this transaction's read and its write.
    | TxFailure.Conflict _ -> Stale
    | TxFailure.InvalidChanges(_, errors) -> Invalid(errors |> List.map View.fieldError |> Array.ofList)
    | TxFailure.Failed failure ->
        unavailable deps $"{error.Step.Action} at {IO.Path.GetFileName error.Step.File}:{error.Step.Line} failed after {error.Attempts} attempt(s)" failure
    | TxFailure.Misused message -> unavailable deps "A transaction was misused" message

/// Runs a workflow. Serializable, so rules over several rows (a board's
/// limit) hold when requests overlap: PostgreSQL fails one of two
/// transactions whose decisions depend on each other's writes, and the retry
/// policy runs the whole description again, reads and decision included.
let private transact (deps: Deps) (ct: CancellationToken) (tx: Tx<'T, Refusal>) : Threading.Tasks.Task<Result<'T, ApiError>> =
    task {
        let! outcome =
            deps.Repo.Transact(
                tx,
                isolation = IsolationLevel.Serializable,
                retry = RetryPolicy.OnSerializationFailure 5,
                cancellationToken = ct
            )

        return outcome |> Result.mapError (ofTransactionError deps)
    }

/// Runs a workflow that ends with a card.
let private run (deps: Deps) (ct: CancellationToken) (tx: Tx<Card, Refusal>) : Async<Result<CardView, ApiError>> =
    task {
        let! outcome = transact deps ct tx
        return outcome |> Result.map View.card
    }
    |> Async.AwaitTask

let private refuse (error: ApiError) : Async<Result<'T, ApiError>> = async.Return(Error error)

/// ASP.NET Core Identity's hasher (PBKDF2 with a salt per password).
let private hasher = Microsoft.AspNetCore.Identity.PasswordHasher<Account>()

/// Checked when the name is unknown, so that answer takes as long as a wrong password.
let private nobodysHash = hasher.HashPassword(Unchecked.defaultof<Account>, "no account has this password")

let private passwordFits (hash: string) (password: string) =
    hasher.VerifyHashedPassword(Unchecked.defaultof<Account>, hash, password)
    <> Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed

/// The API for one request, made by whoever `session` says is signed in;
/// `ct` ends its database work when the client goes away.
let create (deps: Deps) (session: Session) (ct: CancellationToken) : IKanbanApi =

    /// A request that changes something: it needs to know in whose name.
    let signedIn (act: Person -> Async<Result<'T, ApiError>>) : Async<Result<'T, ApiError>> =
        match session.Who() with
        | Some person -> act person
        | None -> refuse SignInRequired

    let change (ref: CardRef) (decide: Board -> int -> Card -> Result<Card, DomainError>) =
        signedIn (fun _ -> run deps ct (changeCard (CardId ref.Card) ref.Version decide))

    /// Reads one board, and its cards with the query `cardsOf` makes for it.
    let readBoard (slug: string) (cardsOf: BoardId -> Query<Card>) : Threading.Tasks.Task<Result<BoardView, ApiError>> =
        task {
            match Slug.create slug with
            // No board has a slug that is not one.
            | Error _ -> return Error(NotFound $"board {slug}")
            | Ok slug ->
                match! deps.Repo.One(Queries.boardBySlug slug, cancellationToken = ct) with
                | Error e -> return Error(unavailable deps "Reading a board failed" e)
                | Ok None -> return Error(NotFound $"board {slug.Value}")
                | Ok(Some board) ->
                    match! deps.Repo.All(cardsOf board.Id, cancellationToken = ct) with
                    | Error e -> return Error(unavailable deps "Reading a board's cards failed" e)
                    | Ok cards ->
                        let view: BoardView =
                            { Slug = board.Slug.Value
                              Name = board.Name.Value
                              WipLimit = board.WipLimit.Value
                              Cards = View.cards cards }

                        return Ok view
        }

    { boards =
        fun () ->
            task {
                let! boards = deps.Repo.All(Queries.boardsWithNewest, cancellationToken = ct)
                let! counts = deps.Repo.All(Queries.cardCounts, cancellationToken = ct)

                return
                    match boards, counts with
                    | Ok boards, Ok counts -> Ok(boards |> List.map (View.summary counts) |> Array.ofList)
                    | Error e, _
                    | _, Error e -> Error(unavailable deps "Listing boards failed" e)
            }
            |> Async.AwaitTask

      createBoard =
        fun input ->
            signedIn (fun _ ->
                let parsed =
                    Input.both
                        (Input.both
                            (Input.both (Input.field "slug" Slug.create input.Slug) (Input.field "name" Title.create input.Name))
                            (Input.field "wipLimit" WipLimit.create input.WipLimit))
                        (Input.each "cards" Title.create input.Cards)

                match parsed with
                | Error fields -> refuse (Invalid(Array.ofList fields))
                | Ok(((slug, name), limit), titles) ->
                    let board =
                        { Id = BoardId(deps.NewId())
                          Slug = slug
                          Name = name
                          WipLimit = limit }

                    // A microsecond apart, so the cards keep the order they were given in.
                    let now = deps.Clock()

                    let cards =
                        titles
                        |> List.mapi (fun i title -> Card.create (CardId(deps.NewId())) board title (now.AddTicks(int64 i * 10L)))

                    task {
                        let! outcome = transact deps ct (createBoard board cards)
                        let newest = cards |> List.rev |> List.truncate 3

                        return
                            outcome
                            |> Result.map (fun stored -> View.summary [ board.Id, StageKind.Backlog, int64 cards.Length ] (stored, newest))
                    }
                    |> Async.AwaitTask)

      board = fun slug -> readBoard slug Queries.cardsOn |> Async.AwaitTask

      addCard =
        fun input ->
            signedIn (fun _ ->
                match Slug.create input.Board, Input.field "title" Title.create input.Title with
                | Error _, _ -> refuse (NotFound $"board {input.Board}")
                | Ok _, Error fields -> refuse (Invalid(Array.ofList fields))
                | Ok slug, Ok title ->
                    let id = CardId(deps.NewId())
                    let now = deps.Clock()
                    run deps ct (addCard slug (fun board -> Card.create id board title now)))

      start =
        fun ref ->
            signedIn (fun assignee ->
                let now = deps.Clock()
                change ref (Card.start assignee now))

      ship =
        fun ref ->
            let now = deps.Clock()
            change ref (fun _ _ card -> Card.finish Shipped now card)

      drop =
        fun input ->
            let now = deps.Clock()
            change input.Ref (fun _ _ card -> Card.finish (Dropped input.Reason) now card)

      sendBack = fun ref -> change ref (fun _ _ card -> Card.sendBack card)

      reopen = fun ref -> change ref (fun _ _ card -> Card.reopen card)

      rename =
        fun input ->
            match Input.field "title" Title.create input.Title with
            | Error fields -> refuse (Invalid(Array.ofList fields))
            | Ok title -> change input.Ref (fun _ _ card -> Card.rename title card)

      comment =
        fun input ->
            signedIn (fun author ->
                let id = CommentId(deps.NewId())
                let now = deps.Clock()
                run deps ct (addComment (CardId input.Card) (Card.comment id author input.Body now)))

      label =
        fun input ->
            signedIn (fun _ ->
                match Input.each "labels" Label.create input.Labels with
                | Error fields -> refuse (Invalid(Array.ofList fields))
                | Ok labels ->
                    run deps ct (relabel (CardId input.Ref.Card) input.Ref.Version (deps.NewId >> LabelId) (Card.relabel labels)))

      labelled =
        fun query ->
            match Label.create query.Label with
            | Error message -> refuse (Invalid [| "label", message |])
            | Ok label ->
                task {
                    let! board = readBoard query.Board (fun board -> Queries.cardsOnLabelled board label)
                    return board |> Result.map (fun view -> view.Cards)
                }
                |> Async.AwaitTask

      sendAllBack =
        fun slug ->
            signedIn (fun _ ->
                match Slug.create slug with
                | Error _ -> refuse (NotFound $"board {slug}")
                | Ok slug -> transact deps ct (sendAllBack slug) |> Async.AwaitTask)

      myWork =
        fun () ->
            signedIn (fun person ->
                task {
                    match! deps.Repo.All(Queries.workOf person, cancellationToken = ct) with
                    | Error e -> return Error(unavailable deps "Reading someone's work failed" e)
                    | Ok rows ->
                        return
                            Ok
                                [| for slug, name, CardId card, title, since in rows ->
                                       { Board = slug.Value
                                         BoardName = name.Value
                                         Card = card
                                         Title = title.Value
                                         // A card with an assignee has a start (the table's check).
                                         Since = defaultArg since DateTimeOffset.MinValue } |]
                }
                |> Async.AwaitTask)

      finished =
        fun query ->
            let page = if query.Page >= 1 then Ok query.Page else Error [ "page", "must be at least 1" ]
            let size = if query.Size >= 1 && query.Size <= 50 then Ok query.Size else Error [ "size", "must be between 1 and 50" ]

            match Input.both page size, Slug.create query.Board with
            | Error fields, _ -> refuse (Invalid(Array.ofList fields))
            | Ok _, Error _ -> refuse (NotFound $"board {query.Board}")
            | Ok(page, size), Ok slug ->
                task {
                    match! deps.Repo.One(Queries.boardBySlug slug, cancellationToken = ct) with
                    | Error e -> return Error(unavailable deps "Reading a board failed" e)
                    | Ok None -> return Error(NotFound $"board {slug.Value}")
                    | Ok(Some board) ->
                        let! total = deps.Repo.Count(Queries.doneOn board.Id, cancellationToken = ct)
                        let! rows = deps.Repo.All(Queries.finishedOn board.Id ((page - 1) * size) size, cancellationToken = ct)

                        match total, rows with
                        | Error e, _
                        | _, Error e -> return Error(unavailable deps "Reading done cards failed" e)
                        | Ok total, Ok rows ->
                            return
                                Ok
                                    { Cards = View.cards rows
                                      Page = page
                                      Total = int total
                                      Pages = int ((total + int64 size - 1L) / int64 size) }
                }
                |> Async.AwaitTask

      register =
        fun input ->
            match Input.both (Input.field "name" Person.create input.Name) (Input.field "password" Password.create input.Password) with
            | Error fields -> refuse (Invalid(Array.ofList fields))
            | Ok(name, password) ->
                let account: Account =
                    { Id = AccountId(deps.NewId())
                      Name = name
                      PasswordHash = hasher.HashPassword(Unchecked.defaultof<Account>, password.Value) }

                task {
                    match! transact deps ct (Tx.insert account) with
                    | Ok stored ->
                        do! session.SignIn stored.Name
                        return Ok stored.Name.Value
                    | Error refused -> return Error refused
                }
                |> Async.AwaitTask

      signIn =
        fun input ->
            task {
                // A name no account can have is answered like a wrong password.
                let! account =
                    match Person.create input.Name with
                    | Error _ -> Threading.Tasks.Task.FromResult(Ok None)
                    | Ok name -> deps.Repo.One(Queries.accountNamed name, cancellationToken = ct)

                let password = if isNull input.Password then "" else input.Password

                match account with
                | Error e -> return Error(unavailable deps "Signing in failed" e)
                | Ok(Some account) when passwordFits account.PasswordHash password ->
                    do! session.SignIn account.Name
                    return Ok account.Name.Value
                | Ok(Some _) -> return Error WrongNameOrPassword
                | Ok None ->
                    passwordFits nobodysHash password |> ignore
                    return Error WrongNameOrPassword
            }
            |> Async.AwaitTask

      signOut =
        fun () ->
            task {
                do! session.SignOut()
                return Ok()
            }
            |> Async.AwaitTask

      whoAmI =
        fun () ->
            match session.Who() with
            | Some person -> async.Return(Ok person.Value)
            | None -> refuse SignInRequired }
