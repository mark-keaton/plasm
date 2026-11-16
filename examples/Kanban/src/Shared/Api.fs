// The contract between server and client (Fable.Remoting serves the record
// of functions below).
//
// Everything that crosses the wire is plain data: strings, numbers, Guids.
// The client's input is parsed into domain values on the server, so a
// validated type is never trusted from outside; what the server sends back
// are views, not domain values.
//
// No request says who is making it. The server knows who is signed in.
//
// Collections are arrays, not F# lists: compiled by Fable 5, the remoting
// client (Fable.SimpleJson 3.24) cannot read an F# list.
namespace Kanban.Api

open System
open Kanban

type StageView =
    | InBacklog
    | InProgress of assignee: string * since: DateTimeOffset
    | WasShipped of at: DateTimeOffset
    | WasDropped of reason: string * at: DateTimeOffset

type CommentView =
    { Author: string
      Body: string
      At: DateTimeOffset }

/// What blocks a card, and since when.
type BlockedView = { Reason: string; Since: DateTimeOffset }

type CardView =
    { Id: Guid
      Title: string
      Stage: StageView
      Blocked: BlockedView option
      /// Sent back with every change, so a change based on an old copy is refused.
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
      /// Titles of the three cards created last, newest first.
      Newest: string[] }

type ApiError =
    /// Input that is not acceptable, by field.
    | Invalid of (string * string)[]
    /// Acceptable input that the domain's rules refuse.
    | Rejected of DomainError
    | NotFound of what: string
    /// The card changed after the client read it.
    | Stale
    /// The request changes something, and nobody is signed in.
    | SignInRequired
    | WrongNameOrPassword
    | Unavailable of message: string

type NewBoard =
    { Slug: string
      Name: string
      WipLimit: int
      /// Titles of the cards the board starts with, in order.
      Cards: string[] }

type NewCard = { Board: string; Title: string }

/// A card as the client last saw it.
type CardRef = { Card: Guid; Version: int }

type DropCard = { Ref: CardRef; Reason: string }
type BlockCard = { Ref: CardRef; Reason: string }
type RenameCard = { Ref: CardRef; Title: string }

type NewComment = { Card: Guid; Body: string }

type LabelCard = { Ref: CardRef; Labels: string[] }

type LabelQuery = { Board: string; Label: string }

/// A card someone is working on, with the board it is on.
type WorkItem =
    { Board: string
      BoardName: string
      Card: Guid
      Title: string
      Since: DateTimeOffset }

/// Pages are numbered from 1.
type PageQuery = { Board: string; Page: int; Size: int }

type CardPage =
    { Cards: CardView[]
      Page: int
      /// How many cards there are on all pages together.
      Total: int
      Pages: int }

type Credentials = { Name: string; Password: string }

type IKanbanApi =
    { boards: unit -> Async<Result<BoardSummary[], ApiError>>
      createBoard: NewBoard -> Async<Result<BoardSummary, ApiError>>
      board: string -> Async<Result<BoardView, ApiError>>
      addCard: NewCard -> Async<Result<CardView, ApiError>>
      start: CardRef -> Async<Result<CardView, ApiError>>
      ship: CardRef -> Async<Result<CardView, ApiError>>
      drop: DropCard -> Async<Result<CardView, ApiError>>
      sendBack: CardRef -> Async<Result<CardView, ApiError>>
      reopen: CardRef -> Async<Result<CardView, ApiError>>
      rename: RenameCard -> Async<Result<CardView, ApiError>>
      block: BlockCard -> Async<Result<CardView, ApiError>>
      unblock: CardRef -> Async<Result<CardView, ApiError>>
      /// A board's blocked cards, the longest blocked first.
      blocked: string -> Async<Result<CardView[], ApiError>>
      comment: NewComment -> Async<Result<CardView, ApiError>>
      label: LabelCard -> Async<Result<CardView, ApiError>>
      /// A board's cards that carry a label.
      labelled: LabelQuery -> Async<Result<CardView[], ApiError>>
      /// Sends every card in progress on a board back; how many there were.
      sendAllBack: string -> Async<Result<int, ApiError>>
      /// What the person signed in is working on, on every board.
      myWork: unit -> Async<Result<WorkItem[], ApiError>>
      /// A board's done cards, newest first.
      finished: PageQuery -> Async<Result<CardPage, ApiError>>
      /// Creates an account and signs it in; the name it is signed in as.
      register: Credentials -> Async<Result<string, ApiError>>
      signIn: Credentials -> Async<Result<string, ApiError>>
      signOut: unit -> Async<Result<unit, ApiError>>
      /// The name of the person signed in.
      whoAmI: unit -> Async<Result<string, ApiError>> }

module Route =
    /// /api/IKanbanApi/board, and so on.
    let builder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"
