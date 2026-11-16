/// What the application does, as values: queries and transactions that
/// describe database work and perform none. `Service.fs` runs them.
///
/// Every change to a card has the same shape: read what the decision needs,
/// let the domain decide, write what it decided. The decision is a plain
/// function from `Kanban.Card`; nothing in the domain knows it runs inside a
/// transaction, and what is read and written is the domain's own record.
module Kanban.Server.Workflows

open Plasm
open Kanban
open Kanban.Server.Db

/// Why a transaction stopped without a database failure.
type Refusal =
    | Domain of DomainError
    | Missing of what: string
    /// The caller's copy of the card is older than the stored one.
    | StaleCopy

/// Built and planned once; a call only binds its arguments. Every one is a
/// value, so the application can check them all before it serves anything.
module Queries =

    /// Every board with the titles of the three cards created last on it,
    /// newest first, and the titles of its blocked cards, the longest blocked
    /// first. The cards are another aggregate, so the query asks for what it
    /// needs of them, twice. `referring<Card> b` is the cards that hold this
    /// board's id: a Card has one field of type BoardId, so nothing more
    /// needs saying. The query's type says what it read:
    /// (Board * Title list * Title list). Each `including` is one more
    /// command for all the boards.
    let boardsWithCards =
        plasm {
            for b in tableOf<Board> do
                orderBy b.Name

                including (
                    plasm {
                        for c in referring<Card> b do
                            orderByDescending c.Created
                            take 3
                            select c.Title
                    }
                )

                including (
                    plasm {
                        for c in referring<Card> b do
                            where c.Blocked.IsSome
                            orderBy (c.Blocked |> Option.map (fun blocker -> blocker.Since))
                            select c.Title
                    }
                )
        }

    /// (board, stage, number of cards), one row per stage a board has cards in.
    let cardCounts =
        plasm {
            for c in tableOf<Card> do
                groupBy (c.Board, kind c)
                select (c.Board, kind c, Agg.count ())
        }

    let boardBySlug =
        Query.prepare (fun (slug: Arg<Slug>) -> plasm { for b in tableOf<Board> do where (b.Slug = slug.Value) })

    let boardById =
        Query.prepare (fun (id: Arg<BoardId>) -> plasm { for b in tableOf<Board> do where (b.Id = id.Value) })

    /// A card, and with it its comments and labels, as every query of cards.
    let cardById =
        Query.prepare (fun (id: Arg<CardId>) -> plasm { for c in tableOf<Card> do where (c.Id = id.Value) })

    /// A board's cards, oldest first: three commands (cards, comments,
    /// labels), however many cards there are.
    let cardsOn =
        Query.prepare (fun (board: Arg<BoardId>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value)
                    orderBy c.Created
            })

    /// A board's cards that carry a label.
    let cardsOnLabelled =
        Query.prepare2 (fun (board: Arg<BoardId>) (label: Arg<Label>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && (c.Labels |> List.exists (fun l -> l.Name = label.Value)))
                    orderBy c.Created
            })

    let inProgressOn =
        Query.prepare (fun (board: Arg<BoardId>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && c.Stage.IsDoing)
            })

    let doneOn =
        Query.prepare (fun (board: Arg<BoardId>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && c.Stage.IsDone)
            })

    /// One page of a board's done cards, newest first. Plasm adds the key to
    /// the ordering, so cards finished at the same moment keep one order from
    /// page to page.
    let finishedOn =
        Query.prepare3 (fun (board: Arg<BoardId>) (before: Arg<int>) (size: Arg<int>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && c.Stage.IsDone)
                    orderByDescending c.FinishedAt
                    skip before.Value
                    take size.Value
            })

    /// A board's cards with something left to do, oldest first. The checklist
    /// is one JSON document per card; the condition over its items runs in the
    /// database.
    let unfinishedOn =
        Query.prepare (fun (board: Arg<BoardId>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && (c.Checklist |> List.exists (fun item -> not item.Done)))
                    orderBy c.Created
            })

    /// A board's blocked cards, the longest blocked first. `Blocked` is a
    /// value in the card's row, so its parts are columns to filter and order by.
    let blockedOn =
        Query.prepare (fun (board: Arg<BoardId>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Board = board.Value && c.Blocked.IsSome)
                    orderBy (c.Blocked |> Option.map (fun blocker -> blocker.Since))
            })

    /// Every blocked card, the longest blocked first, each with the board it
    /// is on. A card holds its board's id, so there is at most one board:
    /// the result is (Card * Board option), not a list to take the head of.
    let blockedEverywhere =
        plasm {
            for c in tableOf<Card> do
                where c.Blocked.IsSome
                orderBy (c.Blocked |> Option.map (fun blocker -> blocker.Since))
                includingOne (referredBy<Board> c)
        }

    /// What someone is working on, each card joined to its board: one command.
    let workOf =
        Query.prepare (fun (person: Arg<Person>) ->
            plasm {
                for c in tableOf<Card> do
                    join b in tableOf<Board> on (c.Board = b.Id)
                    where (c.Assignee = Some person.Value)
                    orderBy c.Since
                    select (b.Slug, b.Name, c.Id, c.Title, c.Since)
            })

    let accountNamed =
        Query.prepare (fun (name: Arg<Person>) -> plasm { for a in tableOf<Account> do where (a.Name = name.Value) })

    /// The stored labels among `names`.
    let labelsNamed =
        Query.prepare (fun (names: Arg<Label list>) ->
            plasm {
                for l in tableOf<Tag> do
                    where (List.contains l.Name names.Value)
            })

/// The one row a query finds, or the refusal that `what` is missing.
let private one (what: string) (query: Query<'T>) : Tx<'T, Refusal> =
    Tx.one query |> Tx.orReject (Missing what)

/// The card, if the caller's copy (`expected`) is the stored one.
let private currentCard (id: CardId) (expected: int) : Tx<Card, Refusal> =
    transaction {
        let! card = one "card" (Queries.cardById id)

        if card.Version <> expected then
            return! Tx.fail StaleCopy
        else
            return card
    }

/// A board and the cards it starts with: all of them, or none.
let createBoard (board: Board) (first: Card list) : Tx<Board, Refusal> =
    transaction {
        let! stored = Tx.insert board
        let! _ = first |> Tx.traverse (fun card -> Tx.insert card)
        return stored
    }

/// Adds the card `create` makes for the board.
let addCard (slug: Slug) (create: Board -> Card) : Tx<Card, Refusal> =
    transaction {
        let! board = one $"board {slug.Value}" (Queries.boardBySlug slug)
        return! Tx.insert (create board)
    }

/// Changes a card: `decide` is given the card's board, how many of the
/// board's cards are in progress, and the card.
///
/// `expected` is the version the caller last saw. The update itself checks the
/// stored version again, so a change that slips in between the read and the
/// write is refused too.
let changeCard (id: CardId) (expected: int) (decide: Board -> int -> Card -> Result<Card, DomainError>) : Tx<Card, Refusal> =
    transaction {
        let! card = currentCard id expected
        let! board = one "board" (Queries.boardById card.Board)
        let! inProgress = Tx.count (Queries.inProgressOn card.Board)
        let! changed = decide board (int inProgress) card |> Tx.ofResult Domain
        return! Tx.update (card, changed)
    }

/// Makes the labels `decide` allows the card's complete set of labels.
///
/// A label is a shared row: a bulk insert that skips the ones already stored
/// makes sure each exists once, however many requests ask for it at the same
/// moment. The card's own change is then only its list of labels, which
/// Plasm writes as rows of card_labels.
let relabel (id: CardId) (expected: int) (newId: unit -> LabelId) (decide: Card -> Result<Label list, DomainError>) : Tx<Card, Refusal> =
    transaction {
        let! card = currentCard id expected
        let! wanted = decide card |> Tx.ofResult Domain
        let tags: Tag list = [ for name in wanted -> { Id = newId (); Name = name } ]
        let! _ = Tx.execute (Bulk.insertMissing(tags, fun l -> l.Name))
        let! stored = Tx.all (Queries.labelsNamed wanted)
        let labels = stored |> List.sortBy (fun tag -> tag.Name)
        return! Tx.update (card, { card with Labels = labels })
    }

/// Sends every card in progress on a board back to the backlog with one
/// UPDATE; how many there were.
///
/// A bulk write runs no per-card decision. That is sound here because
/// `Card.sendBack` never refuses a card in progress, and the statement
/// touches no other card. Each card's version is incremented, so copies read
/// before are stale.
let sendAllBack (slug: Slug) : Tx<int, Refusal> =
    transaction {
        let! board = one $"board {slug.Value}" (Queries.boardBySlug slug)
        return! Tx.execute (Bulk.update(Queries.inProgressOn board.Id, fun c -> { c with Stage = Backlog }))
    }

/// Adds the comment `write` puts on the card, if the domain allows one. The
/// comment is part of the card, so this is a change to the card.
let addComment (id: CardId) (write: Card -> Result<Card, DomainError>) : Tx<Card, Refusal> =
    transaction {
        let! card = one "card" (Queries.cardById id)
        let! changed = write card |> Tx.ofResult Domain
        return! Tx.update (card, changed)
    }
