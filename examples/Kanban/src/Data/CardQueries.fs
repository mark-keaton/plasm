/// What a GraphQL host reads by projection: the fields a request may filter,
/// order and read cards by (CardFields, CardRead), and CardQueries, which reads
/// only the fields a request chose (Selected<Card>), paged and counted per
/// board. The seam does not cross it, and Seam.cs does not list it: what a
/// request chooses is known only when it comes, where the seam writes one
/// fixed view of each record. Hand-written for that reason: BoardNode, the
/// one view here, is a board as a GraphQL field reads it, its cards apart.
/// A field read as a domain value (a card's stage, comments, labels) is made
/// the seam's view by the host (AddPlasmReadType with SeamMap).
namespace Kanban.Data

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Plasm
open Plasm.CSharp
open Kanban
open Kanban.Server.Db
open Kanban.Server.Workflows

/// A board, for a caller that reads its cards apart (a GraphQL field).
type BoardNode =
    { Id: Guid
      Slug: string
      Name: string
      WipLimit: int }

/// What a program building queries at run time (a GraphQL host) may filter
/// and order cards by: each field once, a quotation over the domain's Card,
/// checked when CardQueries is made. Values are given as text and numbers; a
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
/// card: each field of the seam's card view once, by its name, a quotation
/// over the domain's Card, checked when CardQueries is made. A request reads
/// only the fields it chose.
[<RequireQualifiedAccess>]
module CardRead =
    let id = ReadField.via "id" <@ fun (c: Card) -> c.Id @> (fun (CardId id) -> id)
    let title = ReadField.via "title" <@ fun (c: Card) -> c.Title @> (fun (title: Title) -> title.Value)
    let stage = ReadField.value "stage" <@ fun (c: Card) -> c.Stage @>
    let version = ReadField.value "version" <@ fun (c: Card) -> c.Version @>
    let labels = ReadField.value "labels" <@ fun (c: Card) -> c.Labels @>
    let comments = ReadField.value "comments" <@ fun (c: Card) -> c.Comments @>

    let all: ReadField<Card> list = [ id; title; stage; version; labels; comments ]

    /// The board a card is on: read beside a board's cards, which are
    /// grouped by it, and not a field a caller reads.
    let internal board = ReadField.via "board" <@ fun (c: Card) -> c.Board @> (fun (BoardId b) -> b)

[<Sealed>]
/// Cards as a GraphQL request reads them, over the repository of a Boards
/// (CardQueries.Over), so a host has one repository and one start-up check.
type CardQueries private (repo: Repo) =

    let read (what: string) (result: Result<'T, DbError>) =
        match result with
        | Ok value -> value
        | Error(DbError.Database failure) -> UnavailableException($"{what}: {failure.Message}", failure.Exception) |> raise
        | Error e -> UnavailableException($"{what}: {e}", null) |> raise

    /// The projection over `boards`' repository, its declared fields checked
    /// against the database first: one that would fail throws here.
    static member Over(boards: Boards) : CardQueries =
        boards.Repo.CheckFields CardFields.all
        boards.Repo.CheckFields CardRead.all
        CardQueries(boards.Repo)

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

