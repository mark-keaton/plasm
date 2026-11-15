/// What the server stores. The records are the domain's own (`Kanban.Board`,
/// `Kanban.Card`, `Kanban.Comment`, `Kanban.Tag`), and how each is stored is
/// read from the record itself; see `src/Shared/Domain.fs`.
///
/// A record names its table. The rest follows from its types. The key is the
/// field named Id. A column is named after its field. An id is stored as its
/// Guid, and Slug, Title, Person, WipLimit and Label are read through their
/// own `create`, so a stored title the domain would refuse is a reported
/// error, never a Title. A card's `Stage`, a union with fields, is spread over
/// the columns stage, stage_assignee, stage_since, stage_resolution,
/// stage_resolution_reason and stage_at. `Card.Board` is a BoardId, the key of
/// boards, so it references a board. A comment holds a CardId, so a card's
/// comments are its own rows, written and deleted with it; a Tag holds none,
/// so a card's labels are shared rows, and the card owns only its links to
/// them.
module Kanban.Server.Db

open Plasm
open Kanban

/// Who can sign in. Not part of the domain: the rules know people by name.
[<Table "accounts">]
type Account =
    { Id: AccountId
      [<Unique>]
      Name: Person
      PasswordHash: string }

/// A stage without its facts, for counting cards by stage.
[<RequireQualifiedAccess>]
type StageKind =
    | Backlog
    | Doing
    | Done

/// Plasm reads this definition and turns it into SQL.
[<ReflectedDefinition>]
let kind (card: Card) =
    match card.Stage with
    | Backlog -> StageKind.Backlog
    | Doing _ -> StageKind.Doing
    | Done _ -> StageKind.Done

/// The records stored on their own. A comment is not listed: it is part of
/// its card.
let model = Model.buildOrFail [] [ typeof<Board>; typeof<Card>; typeof<Tag>; typeof<Account> ]
