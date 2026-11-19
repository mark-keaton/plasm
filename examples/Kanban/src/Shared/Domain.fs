// The domain: boards of cards that move from the backlog, through work in
// progress, to done.
//
// These records are what the server stores. The few facts about storage
// that their types cannot say are attributes on them: the table, what is
// unique, what a list is ordered by. The attributes are all this file takes
// from Plasm, and it is compiled twice: for .NET (the server decides with it)
// and to JavaScript by Fable (the client validates input with it).
namespace Kanban

open System
open Plasm

type BoardId = BoardId of Guid
type CardId = CardId of Guid
type CommentId = CommentId of Guid
type LabelId = LabelId of Guid
type AccountId = AccountId of Guid

/// A board's address: 2 to 30 lowercase letters, digits and dashes.
type Slug =
    private
    | Slug of string

    member s.Value =
        let (Slug v) = s
        v

    static member create(text: string | null) : Result<Slug, string> =
        let t = (match text with null -> "" | t -> t).Trim().ToLowerInvariant()
        let allowed c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'

        if t.Length < 2 || t.Length > 30 then
            Error "must be 2 to 30 characters"
        elif not (String.forall allowed t) then
            Error "may only contain letters, digits and dashes"
        else
            Ok(Slug t)

/// What a card or a board is called: 1 to 120 characters, trimmed.
type Title =
    private
    | Title of string

    /// Marked so that a query reads it as the title column (a search of a
    /// card's title).
    [<ReflectedDefinition>]
    member t.Value =
        let (Title v) = t
        v

    static member create(text: string | null) : Result<Title, string> =
        let t = (match text with null -> "" | t -> t).Trim()

        if t.Length = 0 then Error "is required"
        elif t.Length > 120 then Error "must be at most 120 characters"
        else Ok(Title t)

/// Who works on a card or writes a comment: 1 to 40 characters, trimmed.
type Person =
    private
    | Person of string

    member p.Value =
        let (Person v) = p
        v

    static member create(text: string | null) : Result<Person, string> =
        let t = (match text with null -> "" | t -> t).Trim()

        if t.Length = 0 then Error "is required"
        elif t.Length > 40 then Error "must be at most 40 characters"
        else Ok(Person t)

/// How many cards a board allows in progress at once: 1 to 20.
type WipLimit =
    private
    | WipLimit of int

    member l.Value =
        let (WipLimit v) = l
        v

    static member create(n: int) : Result<WipLimit, string> =
        if n < 1 || n > 20 then Error "must be between 1 and 20" else Ok(WipLimit n)

/// A word cards are sorted by: 1 to 24 characters, trimmed and in lower case,
/// so "Docs" and " docs" are the same label.
type Label =
    private
    | Label of string

    member l.Value =
        let (Label v) = l
        v

    static member create(text: string | null) : Result<Label, string> =
        let t = (match text with null -> "" | t -> t).Trim().ToLowerInvariant()

        if t.Length < 1 || t.Length > 24 then Error "must be 1 to 24 characters" else Ok(Label t)

/// A password as someone typed it: 8 to 200 characters. It is never stored;
/// the server keeps a hash of it.
type Password =
    private
    | Password of string

    member p.Value =
        let (Password v) = p
        v

    static member create(text: string | null) : Result<Password, string> =
        let t = match text with null -> "" | t -> t

        if t.Length < 8 then Error "must be at least 8 characters"
        elif t.Length > 200 then Error "must be at most 200 characters"
        else Ok(Password t)

/// How a card ended.
type Resolution =
    | Shipped
    | Dropped of reason: string

/// Where a card is. Each stage carries exactly the facts that exist in it: a
/// card in the backlog has no assignee, and a done card has no "since".
type Stage =
    | Backlog
    | Doing of assignee: Person * since: DateTimeOffset
    | Done of resolution: Resolution * at: DateTimeOffset

/// What keeps a card from moving, and since when. It has no table and no
/// key: it is a value, stored in the card's own row.
type Blocker = { Reason: string; Since: DateTimeOffset }

/// One thing to do on a card. It has no table either: a card's checklist is
/// one document in the card's row.
type ChecklistItem = { Text: string; Done: bool }

[<Table "boards">]
type Board =
    { Id: BoardId
      [<Unique>]
      Slug: Slug
      Name: Title
      WipLimit: WipLimit }

[<Table "comments">]
type Comment =
    { Id: CommentId
      Card: CardId
      Author: Person
      Body: string
      At: DateTimeOffset }

/// A label as cards share it: it exists once, whichever cards carry it.
[<Table "labels">]
type Tag =
    { Id: LabelId
      [<Unique>]
      Name: Label }

/// A card, whole: its comments belong to it, oldest first, and its labels
/// are the ones it carries, in alphabetical order.
[<Table "cards">]
type Card =
    { Id: CardId
      Board: BoardId
      Title: Title
      Stage: Stage
      /// A card is blocked or it is not: there is no reason without a time.
      Blocked: Blocker option
      /// What is to do on the card, in the order it was written down.
      [<Json>]
      Checklist: ChecklistItem list
      [<OrderedBy(nameof the<Comment>.At)>]
      Comments: Comment list
      [<JoinTable("card_labels", "card_id", "label_id"); OrderedBy(nameof the<Tag>.Name)>]
      Labels: Tag list
      Created: DateTimeOffset
      /// How many times the card has been written. Someone changing a card
      /// says which version they saw.
      [<Version>]
      Version: int }

    // Each stage's facts by name. [<ReflectedDefinition>] keeps the definition
    // readable at run time, so that the server's queries can ask for
    // "cards whose Assignee is Ada" and have it mean exactly this.

    /// Who is working on the card, when someone is.
    [<ReflectedDefinition>]
    member c.Assignee =
        match c.Stage with
        | Doing(who, _) -> Some who
        | _ -> None

    /// Since when the card has been in progress.
    [<ReflectedDefinition>]
    member c.Since =
        match c.Stage with
        | Doing(_, since) -> Some since
        | _ -> None

    /// When the card was finished.
    [<ReflectedDefinition>]
    member c.FinishedAt =
        match c.Stage with
        | Done(_, at) -> Some at
        | _ -> None

/// Why the domain refused a request. Only primitives inside, so it can be sent
/// to the client as it is.
type DomainError =
    /// The board already has as many cards in progress as it allows.
    | WipLimitReached of limit: int
    | AlreadyInProgress of assignee: string
    /// The card is not being worked on (it is in the backlog).
    | NotInProgress
    /// The card is done: it cannot be started, renamed or commented on until it is reopened.
    | CardClosed
    /// Only a done card can be reopened.
    | NotDone
    | DropNeedsReason
    | EmptyComment
    | TooManyLabels of most: int
    | EmptyItem
    | NoSuchItem of text: string
    /// A card does not ship while its checklist has items left.
    | ItemsLeft of count: int
    | BlockNeedsReason
    | AlreadyBlocked of reason: string
    | NotBlocked
    /// A blocked card does not ship until it is unblocked.
    | CardBlocked of reason: string

[<RequireQualifiedAccess>]
module Card =

    let create (id: CardId) (board: Board) (title: Title) (now: DateTimeOffset) : Card =
        { Id = id
          Board = board.Id
          Title = title
          Stage = Backlog
          Blocked = None
          Checklist = []
          Comments = []
          Labels = []
          Created = now
          Version = 1 }

    /// Starts work on a card. `inProgress` is how many of the board's cards
    /// are being worked on now; the board's limit is a rule over all of them.
    let start (assignee: Person) (now: DateTimeOffset) (board: Board) (inProgress: int) (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Doing(who, _) -> Error(AlreadyInProgress who.Value)
        | Done _ -> Error CardClosed
        | Backlog when inProgress >= board.WipLimit.Value -> Error(WipLimitReached board.WipLimit.Value)
        | Backlog -> Ok { card with Stage = Doing(assignee, now) }

    /// The card with one more thing to do, after the ones it has.
    let addItem (text: string) (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Done _ -> Error CardClosed
        | _ when String.IsNullOrWhiteSpace text -> Error EmptyItem
        | _ -> Ok { card with Checklist = card.Checklist @ [ { Text = text.Trim(); Done = false } ] }

    /// Ticks the item with this text, or unticks it if it is ticked.
    let tick (text: string) (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Done _ -> Error CardClosed
        | _ when card.Checklist |> List.forall (fun item -> item.Text <> text) -> Error(NoSuchItem text)
        | _ ->
            Ok
                { card with
                    Checklist = [ for item in card.Checklist -> if item.Text = text then { item with Done = not item.Done } else item ] }

    /// Says what keeps a card from moving.
    let block (reason: string) (now: DateTimeOffset) (card: Card) : Result<Card, DomainError> =
        match card.Stage, card.Blocked with
        | Done _, _ -> Error CardClosed
        | _, Some blocker -> Error(AlreadyBlocked blocker.Reason)
        | _ when String.IsNullOrWhiteSpace reason -> Error BlockNeedsReason
        | _ -> Ok { card with Blocked = Some { Reason = reason.Trim(); Since = now } }

    let unblock (card: Card) : Result<Card, DomainError> =
        match card.Blocked with
        | Some _ -> Ok { card with Blocked = None }
        | None -> Error NotBlocked

    /// Finishes a card. Only work in progress can ship, and not while it is
    /// blocked or has checklist items left; a card can be dropped from the backlog too, but never without
    /// a reason.
    let finish (resolution: Resolution) (now: DateTimeOffset) (card: Card) : Result<Card, DomainError> =
        let left = card.Checklist |> List.filter (fun item -> not item.Done) |> List.length

        match card.Stage, resolution, card.Blocked with
        | Done _, _, _ -> Error CardClosed
        | _, Dropped reason, _ when String.IsNullOrWhiteSpace reason -> Error DropNeedsReason
        | Backlog, Shipped, _ -> Error NotInProgress
        // Whatever blocked a dropped card no longer matters.
        | (Backlog | Doing _), Dropped reason, _ -> Ok { card with Stage = Done(Dropped(reason.Trim()), now); Blocked = None }
        | Doing _, Shipped, Some blocker -> Error(CardBlocked blocker.Reason)
        | Doing _, Shipped, None when left > 0 -> Error(ItemsLeft left)
        | Doing _, Shipped, None -> Ok { card with Stage = Done(Shipped, now) }

    /// How long a card may be in progress. A card in progress longer than
    /// this is stale, and is sent back to the backlog (`sendBack`).
    let staleAfter = TimeSpan.FromDays 14.0

    /// Puts work in progress back in the backlog.
    let sendBack (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Doing _ -> Ok { card with Stage = Backlog }
        | Backlog -> Error NotInProgress
        | Done _ -> Error CardClosed

    /// Returns a done card to the backlog.
    let reopen (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Done _ -> Ok { card with Stage = Backlog }
        | Backlog
        | Doing _ -> Error NotDone

    let rename (title: Title) (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Done _ -> Error CardClosed
        | Backlog
        | Doing _ -> Ok { card with Title = title }

    /// How many labels a card carries at most.
    let mostLabels = 5

    /// The labels a card would carry: each once, in alphabetical order.
    let relabel (labels: Label list) (card: Card) : Result<Label list, DomainError> =
        let labels = labels |> List.distinct |> List.sort

        match card.Stage with
        | Done _ -> Error CardClosed
        | _ when labels.Length > mostLabels -> Error(TooManyLabels mostLabels)
        | _ -> Ok labels

    /// The card with one more comment, after the ones it has.
    let comment (id: CommentId) (author: Person) (body: string) (now: DateTimeOffset) (card: Card) : Result<Card, DomainError> =
        match card.Stage with
        | Done _ -> Error CardClosed
        | _ when String.IsNullOrWhiteSpace body -> Error EmptyComment
        | _ ->
            let comment =
                { Id = id
                  Card = card.Id
                  Author = author
                  Body = body.Trim()
                  At = now }

            Ok { card with Comments = card.Comments @ [ comment ] }

[<RequireQualifiedAccess>]
module DomainError =

    /// The refusal in words, for people.
    let describe (error: DomainError) =
        match error with
        | WipLimitReached limit -> $"This board allows {limit} cards in progress at once. Finish or send one back first."
        | AlreadyInProgress who -> $"{who} is already working on this card."
        | NotInProgress -> "This card is not in progress."
        | CardClosed -> "This card is done. Reopen it to change it."
        | NotDone -> "Only a done card can be reopened."
        | DropNeedsReason -> "Say why the card is dropped."
        | EmptyComment -> "A comment cannot be empty."
        | TooManyLabels most -> $"A card carries at most {most} labels."
        | EmptyItem -> "A checklist item needs some text."
        | NoSuchItem text -> $"This card has no item called {text}."
        | ItemsLeft 1 -> "This card has 1 item left to do."
        | ItemsLeft count -> $"This card has {count} items left to do."
        | BlockNeedsReason -> "Say what blocks the card."
        | AlreadyBlocked reason -> $"This card is already blocked: {reason}."
        | NotBlocked -> "This card is not blocked."
        | CardBlocked reason -> $"This card is blocked: {reason}. Unblock it first."
