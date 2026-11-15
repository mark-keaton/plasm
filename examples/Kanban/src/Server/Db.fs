/// How the domain is stored. The records Plasm reads and writes are the
/// domain's own (`Kanban.Board`, `Kanban.Card`, `Kanban.Comment`,
/// `Kanban.Tag`); this file says only what their types cannot.
///
/// The rest follows from the types. A column is named after its field. An id
/// is stored as its Guid, and Slug, Title, Person, WipLimit and Label are
/// read through their own `create`, so a stored title the domain would refuse
/// is a reported error, never a Title. A card's `Stage`, a union with fields,
/// is spread over the columns stage, stage_assignee, stage_since,
/// stage_resolution, stage_resolution_reason and stage_at. `Card.Board` is a
/// BoardId, the key of boards, so it references a board.
module Kanban.Server.Db

open Plasm
open Kanban

/// Who can sign in. Not part of the domain: the rules know people by name.
type Account =
    { Id: AccountId
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

let boards =
    schema<Board> "boards" {
        key (fun b -> b.Id)
        unique (fun b -> b.Slug)
    }

let comments = schema<Comment> "comments" { key (fun m -> m.Id) }

let labels =
    schema<Tag> "labels" {
        key (fun l -> l.Id)
        unique (fun l -> l.Name)
    }

/// A card is read and written whole: its comments are its own rows, deleted
/// with it; its labels are shared rows, and the card owns only the links to
/// them, in card_labels.
let cards =
    schema<Card> "cards" {
        key (fun c -> c.Id)
        version (fun c -> c.Version)
        ownsOrderedBy (fun c -> c.Comments) (fun m -> m.Card) (fun m -> m.At)
        linksOrderedBy (fun c -> c.Labels) "card_labels" "card_id" "label_id" (fun l -> l.Name)
    }

let accounts =
    schema<Account> "accounts" {
        key (fun a -> a.Id)
        unique (fun a -> a.Name)
    }

let private schemas: ISchema list = [ boards; cards; comments; labels; accounts ]

let model = Model.buildOrFail [] schemas
