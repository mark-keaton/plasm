/// What a C# host calls through the seam: one type, Boards. Its members take
/// and return the domain's own types (Card, Board, the requests below); an
/// outcome that can go more than one way is one of Plasm.CSharp's (FindResult,
/// AddResult, RequestResult, ChangeResult of the card and DomainError), and a
/// read of many is an array. Each host's Seam.cs has the seam write the C# it
/// calls: a view of each record and union, and a C# Boards over this one,
/// whose outcomes are of those views, each refusal a Refused named by its
/// DomainError case. No view, and no mapping to one, is written here.
/// BoardStore, at the end, says where the data is. A database that cannot be
/// reached throws Plasm.CSharp's UnavailableException.
namespace Kanban.Data

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Plasm
open Npgsql
open Plasm.Postgres
open Plasm.CSharp
open Kanban
open Kanban.Server.Db
open Kanban.Server.Workflows

/// What a host is asked, as its requests' bodies say it: the seam gives C# a
/// record of the same fields for each, reads its JSON, and makes this one
/// from it, so an endpoint passes its body whole.
type NewBoard = { Slug: string; Name: string; WipLimit: int }

type NewCard = { Title: string }

type Start = { Version: int; Assignee: string }

type Rename = { Version: int; Title: string }

/// A board with its cards, oldest first, each with its comments and labels.
type BoardWithCards =
    { Slug: Slug
      Name: Title
      WipLimit: WipLimit
      Cards: Card list }

[<Sealed>]
/// What C# calls: the operations, each over the repository and the clock
/// BoardStore makes it with.
type Boards(repo: Repo, time: TimeProvider) =

    let unavailable (what: string) (detail: obj) (inner: exn | null) =
        UnavailableException($"{what}: {detail}", inner) |> raise

    let read (what: string) (result: Result<'T, DbError>) =
        match result with
        | Ok value -> value
        | Error(DbError.Database failure) -> unavailable what failure.Message failure.Exception
        | Error e -> unavailable what e null

    /// Runs a workflow as the F# server does: serializable, run again on a
    /// serialization failure (a board's limit is read with its cards).
    let transact (tx: Tx<'T, Stop<'R>>) (ct: CancellationToken) =
        repo.Transact(tx, ct, isolation = IsolationLevel.Serializable, retry = RetryPolicy.OnSerializationFailure 5)

    let field (name: string) (parse: 'a -> Result<'b, string>) (value: 'a) =
        parse value |> Result.mapError (fun message -> [ name, message ])

    let problems (r: Result<_, (string * string) list>) =
        match r with
        | Error e -> e
        | Ok _ -> []

    let change (card: CardId) (version: int) decide ct : Task<ChangeResult<Card, DomainError>> =
        task {
            let! result = transact (changeCard card version decide) ct
            return Outcome.changed result
        }

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

    /// Every board, by name, with how many cards it has in each stage and
    /// the titles of its newest three.
    member _.BoardsAsync(cancellationToken: CancellationToken) : Task<BoardSummary[]> =
        task {
            let! boards = repo.All(Queries.boardsWithCards, cancellationToken = cancellationToken)
            let! counts = repo.All(Queries.cardCounts, cancellationToken = cancellationToken)
            let counts = read "Counting cards failed" counts
            return read "Listing boards failed" boards |> List.map (summary counts) |> Array.ofList
        }

    /// A board and its cards, oldest first, each with its comments and
    /// labels; Missing when no board has the slug.
    member _.BoardAsync(slug: string, cancellationToken: CancellationToken) : Task<FindResult<BoardWithCards>> =
        task {
            let missing = FindResult<BoardWithCards> { What = $"board {slug}" }

            match Slug.create slug with
            | Error _ -> return missing
            | Ok slug ->
                let! found = repo.One(Queries.boardBySlug slug, cancellationToken = cancellationToken)

                match read "Reading a board failed" found with
                | None -> return missing
                | Some board ->
                    let! cards = repo.All(Queries.cardsOn board.Id, cancellationToken = cancellationToken)

                    return
                        FindResult
                            { Slug = board.Slug
                              Name = board.Name
                              WipLimit = board.WipLimit
                              Cards = read "Reading a board's cards failed" cards }
        }

    /// A board, if its slug, name and limit are ones (Slug, Title, WipLimit)
    /// and no board has the slug: Invalid, field by field, when not.
    member _.CreateBoardAsync(request: NewBoard, cancellationToken: CancellationToken) : Task<AddResult<BoardSummary>> =
        task {
            match field "slug" Slug.create request.Slug, field "name" Title.create request.Name, field "wipLimit" WipLimit.create request.WipLimit with
            | Ok slug, Ok name, Ok limit ->
                let board: Board = { Id = BoardId(Guid.CreateVersion7()); Slug = slug; Name = name; WipLimit = limit }
                let! result = transact (createBoard board []) cancellationToken
                return Outcome.added result |> AddResult.map (fun stored -> summary [] { Board = stored; Newest = []; Blocked = [] })
            | s, n, l -> return AddResult(Invalid.fields (problems s @ problems n @ problems l))
        }

    /// A card in the board's backlog: Missing when no board has the slug,
    /// Invalid when the title is not one.
    member _.AddCardAsync(board: string, request: NewCard, cancellationToken: CancellationToken) : Task<RequestResult<Card>> =
        task {
            match Slug.create board, field "title" Title.create request.Title with
            | Error _, _ -> return RequestResult { What = $"board {board}" }
            | Ok _, Error problems -> return RequestResult(Invalid.fields problems)
            | Ok slug, Ok title ->
                let id = CardId(Guid.CreateVersion7())
                let now = time.GetUtcNow()
                // Adding a card stops only when the board is not there.
                let! result = transact (addCard slug (fun b -> Card.create id b title now)) cancellationToken
                return Outcome.answered result
        }

    /// The assignee starts work on the card, if the board's limit allows it.
    member _.StartAsync(card: CardId, request: Start, cancellationToken: CancellationToken) : Task<ChangeResult<Card, DomainError>> =
        match field "assignee" Person.create request.Assignee with
        | Error problems -> Task.FromResult(ChangeResult<Card, DomainError>(Invalid.fields problems))
        | Ok who -> change card request.Version (Card.start who (time.GetUtcNow())) cancellationToken

    member _.RenameAsync(card: CardId, request: Rename, cancellationToken: CancellationToken) : Task<ChangeResult<Card, DomainError>> =
        match field "title" Title.create request.Title with
        | Error problems -> Task.FromResult(ChangeResult<Card, DomainError>(Invalid.fields problems))
        | Ok title -> change card request.Version (fun _ _ c -> Card.rename title c) cancellationToken

/// Where Boards' data is: PostgreSQL, at the connection string a host gives,
/// over Npgsql's slim data source with what Kanban's tables need (arrays) and
/// TLS: the smaller native binary a Lambda function's cold start wants.
/// The seam gives C# all the rest from it: Boards opened (Plasm checking
/// every query and the tables first), its DDL, a test's sandbox, and AddBoards
/// for ASP.NET.
module BoardStore =
    let store: Store<Boards> =
        { Model = model
          Provider =
            fun connectionString ->
                PostgresProvider(NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().EnableTransportSecurity().Build()) :> IProvider
          // A provider that reaches no database, for printing the DDL.
          Printing = fun () -> Postgres.connect "Host=localhost" :> IProvider
          Queries = typeof<BoardLine>.Assembly
          Make = fun repo time -> Boards(repo, time) }
