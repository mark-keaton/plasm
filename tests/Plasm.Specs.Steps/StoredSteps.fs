/// A domain for stored-records.feature: records that say how they are stored,
/// with no schema declared for them.
namespace Plasm.Specs.StoredDomain

open System
open Plasm

type BoardId = BoardId of int64
type CoverId = CoverId of int64
type CardId = CardId of int64
type CommentId = CommentId of int64
type LabelId = LabelId of Guid

/// Owned by its board: it holds the board's key.
[<Table "covers">]
type Cover =
    { Id: CoverId
      Board: BoardId
      Image: string }

[<Table "boards">]
type Board =
    { Id: BoardId
      [<Unique>]
      Slug: string
      Cover: Cover option }

/// Owned by its card.
[<Table "comments">]
type Comment =
    { Id: CommentId
      Card: CardId
      Text: string
      At: DateTime }

/// Shared by cards: it holds no card's key.
[<Table "labels">]
type Label = { Id: LabelId; Name: string }

[<Table "cards">]
type Card =
    { Id: CardId
      Board: BoardId
      Title: string
      [<Version>]
      Version: int
      [<Generated>]
      CreatedAt: DateTime
      /// The board the card was on before; kept after that board is deleted.
      [<NotReference>]
      MovedFrom: BoardId option
      [<OrderedBy(nameof the<Comment>.At)>]
      Comments: Comment list
      [<JoinTable "card_labels">]
      Labels: Label list }

module Stored =
    let model = Model.buildOrFail [] [ typeof<Board>; typeof<Card>; typeof<Label> ]

    let titledLike (prefix: string) =
        plasm {
            for c in tableOf<Card> do
                where (c.Title.StartsWith prefix)
                orderBy c.Title
        }

/// Records the types cannot describe, one model each.
module Unclear =
    type UntabledId = UntabledId of int64
    type Untabled = { Id: UntabledId; Name: string }

    type KeylessId = KeylessId of int64

    [<Table "keyless">]
    type Keyless = { Key: KeylessId; Name: string }

    type HolderId = HolderId of int64
    type ItemId = ItemId of int64

    [<Table "ambiguous_items">]
    type AmbiguousItem =
        { Id: ItemId
          First: HolderId
          Second: HolderId }

    [<Table "ambiguous">]
    type Ambiguous = { Id: HolderId; Items: AmbiguousItem list }

    type JoineeId = JoineeId of int64

    [<Table "joinees">]
    type Joinee = { Id: JoineeId; Name: string }

    type UnjoinedId = UnjoinedId of int64

    [<Table "unjoined">]
    type Unjoined = { Id: UnjoinedId; Joinees: Joinee list }

    type LinkeeId = LinkeeId of int64

    [<Table "linkees">]
    type Linkee = { Id: LinkeeId; Name: string }

    type LinkerId = LinkerId of int64

    [<Table "linkers">]
    type Linker =
        { Id: LinkerId
          [<JoinTable "linker_linkees">]
          Linkees: Linkee list }

    type OwnedParentId = OwnedParentId of int64
    type OwnedChildId = OwnedChildId of int64

    [<Table "owned_children">]
    type OwnedChild = { Id: OwnedChildId; Parent: OwnedParentId }

    [<Table "owned_parents">]
    type OwnedParent = { Id: OwnedParentId; Children: OwnedChild list }

    type FirstOwnerId = FirstOwnerId of int64
    type SecondOwnerId = SecondOwnerId of int64
    type SharedPartId = SharedPartId of int64

    [<Table "shared_parts">]
    type SharedPart =
        { Id: SharedPartId
          First: FirstOwnerId
          Second: SecondOwnerId }

    [<Table "first_owners">]
    type FirstOwner = { Id: FirstOwnerId; Parts: SharedPart list }

    [<Table "second_owners">]
    type SecondOwner = { Id: SecondOwnerId; Parts: SharedPart list }

    type MisorderedId = MisorderedId of int64
    type StepId = StepId of int64

    [<Table "misordered_steps">]
    type Step = { Id: StepId; Owner: MisorderedId; At: DateTime }

    [<Table "misordered">]
    type Misordered =
        { Id: MisorderedId
          [<OrderedBy "Missing">]
          Steps: Step list }

    type Unstored = { Name: string }
    type PlainId = PlainId of int64

    [<Table "plain">]
    type Plain = { Id: PlainId; Items: Unstored list }

    type DoubledId = DoubledId of int64

    [<Table "doubled">]
    type Doubled =
        { Id: DoubledId
          [<Version>]
          Version: int
          [<Version>]
          Revision: int }

    let models =
        Map
            [ "a record without a table", [ typeof<Untabled> ]
              "a record without an Id", [ typeof<Keyless> ]
              "a list item holding the owner's key twice", [ typeof<Ambiguous> ]
              "a linked list without a join table", [ typeof<Unjoined>; typeof<Joinee> ]
              "a linked record that is not in the model", [ typeof<Linker> ]
              "an owned record that is also listed", [ typeof<OwnedParent>; typeof<OwnedChild> ]
              "a record owned by two owners", [ typeof<FirstOwner>; typeof<SecondOwner> ]
              "an owned list ordered by a field it does not have", [ typeof<Misordered> ]
              "a list of records that are not stored", [ typeof<Plain> ]
              "a record with two version fields", [ typeof<Doubled> ] ]

namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.Specs.StoredDomain

/// Feature: A record says how it is stored
[<Binding>]
type StoredSteps(w: World) =

    let repo () = w.Get<Repo> "storedRepo"
    let board () = w.Get<BoardId> "storedBoard"

    let card title =
        { Id = CardId 0L
          Board = board ()
          Title = title
          Version = 1
          CreatedAt = DateTime.MinValue
          MovedFrom = None
          Comments = []
          Labels = [] }

    let comment text at =
        { Id = CommentId 0L
          Card = CardId 0L
          Text = text
          At = at }

    let insert (value: 'T) =
        task {
            let! r = repo().Transact(Tx.insert value: Tx<'T, unit>)
            return r
        }

    let insertCard (c: Card) =
        task {
            let! r = insert c
            w.Set("storedCard", ok r)
        }
        |> unit

    let readBack () =
        task {
            let id = (w.Get<Card> "storedCard").Id
            let! r = repo().One(plasm { for c in tableOf<Card> do where (c.Id = id) })
            w.Set("storedRead", (ok r).Value)
        }
        |> unit

    let at (hhmm: string) = DateTime.Parse($"2026-11-14T{hhmm}:00Z").ToUniversalTime()

    [<StepDefinition("boards, cards, comments and labels stored by what their types say")>]
    member _.Tables() =
        task {
            let r = w.Db.RepoWith(model = Stored.model)
            let! _ = w.Db.Exec "drop table if exists card_labels, comments, cards, labels, covers, boards cascade"
            let! _ = w.Db.Exec(r.Ddl())
            let! _ = w.Db.Exec "alter table cards alter column created_at set default now()"
            w.Set("storedRepo", r)

            let! b =
                r.Transact(
                    Tx.insert
                        { Id = BoardId 0L
                          Slug = "main"
                          Cover = None }
                    : Tx<_, unit>
                )

            w.Set("storedBoard", (ok b).Id)
        }
        |> unit

    [<StepDefinition("I print the DDL of the stored model")>]
    member _.Ddl() = w.Set("storedDdl", repo().Ddl())

    [<StepDefinition("^the DDL creates the tables (.+)$")>]
    member _.Creates(list: string) =
        let ddl = w.Get<string> "storedDdl"

        for t in list.Replace(" and ", ", ").Split(", ") do
            check (ddl.Contains $"create table {t} (") $"the DDL does not create {t}:\n{ddl}"

    [<StepDefinition("the key of cards is generated by the database")>]
    member _.Generated() =
        let ddl = w.Get<string> "storedDdl"
        check (ddl.Contains "create table cards (\n  id bigint not null generated always as identity primary key") ddl

    [<StepDefinition("the key of labels is assigned by the application")>]
    member _.Assigned() =
        let ddl = w.Get<string> "storedDdl"
        check (ddl.Contains "create table labels (\n  id uuid not null primary key") ddl

    [<StepDefinition("comments has a foreign key from card to cards")>]
    member _.CommentReference() =
        let ddl = w.Get<string> "storedDdl"
        check (ddl.Contains "add constraint comments_card_fkey foreign key (card) references cards (id)") ddl

    [<StepDefinition("^I insert a stored Card with the comments \"(.*)\" and \"(.*)\"$")>]
    member _.WithComments(a: string, b: string) =
        insertCard { card "commented" with Comments = [ comment a (at "09:00"); comment b (at "10:00") ] }

    [<StepDefinition("I read the Card back")>]
    member _.ReadCard() = readBack ()

    [<StepDefinition("^the Card has the comments \"(.*)\" and \"(.*)\"$")>]
    member _.HasComments(a: string, b: string) =
        equal [ a; b ] ((w.Get<Card> "storedRead").Comments |> List.map (fun c -> c.Text)) "comments"

    [<StepDefinition("^I insert a stored Card with comments written at (.+)$")>]
    member _.CommentsAt(times: string) =
        let times = times.Replace(" and ", ", ").Split(", ")
        insertCard { card "timed" with Comments = [ for t in times -> comment t (at t) ] }

    [<StepDefinition("^its comments are read in the order (.+)$")>]
    member _.CommentOrder(times: string) =
        let expected = times.Replace(" and ", ", ").Split(", ") |> List.ofArray
        equal expected ((w.Get<Card> "storedRead").Comments |> List.map (fun c -> c.Text)) "comments in order"

    [<StepDefinition("^the labels \"(.*)\" and \"(.*)\"$")>]
    member _.Labels(a: string, b: string) =
        task {
            let mutable labels = []

            for name in [ a; b ] do
                let! r = insert { Id = LabelId(Guid.NewGuid()); Name = name }
                labels <- labels @ [ ok r ]

            w.Set("storedLabels", labels)
        }
        |> unit

    [<StepDefinition("I insert a stored Card linked to both labels")>]
    member _.Linked() =
        insertCard { card "labelled" with Labels = w.Get<Label list> "storedLabels" }

    [<StepDefinition("^the Card has the labels \"(.*)\" and \"(.*)\"$")>]
    member _.HasLabels(a: string, b: string) =
        equal (set [ a; b ]) ((w.Get<Card> "storedRead").Labels |> List.map (fun l -> l.Name) |> set) "labels"

    [<StepDefinition("the labels table still has two rows")>]
    member _.TwoLabels() =
        task {
            let! n = w.Db.Scalar<int64> "select count(*) from labels"
            equal 2L n "labels"
        }
        |> unit

    [<StepDefinition("I insert a Board with a cover")>]
    member _.BoardWithCover() =
        task {
            let! r =
                insert
                    { Id = BoardId 0L
                      Slug = "covered"
                      Cover =
                        Some
                            { Id = CoverId 0L
                              Board = BoardId 0L
                              Image = "sky.png" } }

            w.Set("storedNewBoard", ok r)
        }
        |> unit

    [<StepDefinition("I read the Board back")>]
    member _.ReadBoard() =
        task {
            let id = (w.Get<Board> "storedNewBoard").Id
            let! r = repo().One(plasm { for b in tableOf<Board> do where (b.Id = id) })
            w.Set("storedBoardRead", (ok r).Value)
        }
        |> unit

    [<StepDefinition("the Board has its cover")>]
    member _.HasCover() =
        equal (Some "sky.png") ((w.Get<Board> "storedBoardRead").Cover |> Option.map (fun c -> c.Image)) "cover"

    [<StepDefinition("^a Board with the slug \"(.*)\"$")>]
    member _.Slug(slug: string) =
        task {
            let! r = insert { Id = BoardId 0L; Slug = slug; Cover = None }
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("^I insert another Board with the slug \"(.*)\"$")>]
    member _.DuplicateSlug(slug: string) =
        task {
            let! r = insert { Id = BoardId 0L; Slug = slug; Cover = None }

            match r with
            | Error { Failure = TxFailure.InvalidChanges(_, errors) } -> w.Set("constraintFailure", (Choice1Of2 errors: Choice<FieldError list, DbFailure>))
            | Error { Failure = TxFailure.Failed(DbError.Database f) } -> w.Set("constraintFailure", (Choice2Of2 f: Choice<FieldError list, DbFailure>))
            | other -> failwithf "expected a uniqueness error, got %A" other
        }
        |> unit

    [<StepDefinition("a stored Card read from the database")>]
    member _.CardRead() =
        task {
            do! insertCard (card "read")
            do! readBack ()
        }
        |> unit

    [<StepDefinition("another writer changes that Card's title and version")>]
    member _.OtherWriter() = w.Db.Exec "update cards set title = 'theirs', version = version + 1" |> unit

    [<StepDefinition("I change the title of the stored Card I read")>]
    member _.Change() =
        task {
            let read = w.Get<Card> "storedRead"
            let! r = repo().Transact(read |> edit (fun c -> { c with Title = "mine" }))

            w.Set(
                "noteWrite",
                r
                |> Result.map ignore
                |> Result.mapError (fun e -> string e, (match e.Failure with TxFailure.Conflict _ -> true | _ -> false))
            )
        }
        |> unit

    [<StepDefinition("I insert a stored Card")>]
    member _.InsertCard() = insertCard (card "fresh")

    [<StepDefinition("the Card comes back with the database's creation time")>]
    member _.CreatedAt() =
        let c = w.Get<Card> "storedCard"
        check (c.CreatedAt > DateTime.UtcNow.AddMinutes -5.0 && c.CreatedAt.Year > 2000) $"created at {c.CreatedAt}"

    [<StepDefinition("I list the references the stored model infers")>]
    member _.References() = w.Set("references", Stored.model.References)

    [<StepDefinition("^(\\w+)\\.(\\w+) refers to nothing$")>]
    member _.NoReference(model: string, field: string) =
        let refs = w.Get<ModelReference list> "references"
        check (refs |> List.forall (fun r -> not (r.Model = model && r.Field = field))) $"the references are %A{refs}"

    [<StepDefinition("^Cards with the titles \"(.*)\" and \"(.*)\"$")>]
    member _.Titled(a: string, b: string) =
        task {
            for t in [ a; b ] do
                let! r = insert (card t)
                ok r |> ignore
        }
        |> unit

    [<StepDefinition("^I read the Cards whose title starts with \"(.*)\"$")>]
    member _.ReadTitled(prefix: string) =
        task {
            let! r = repo().All(Stored.titledLike prefix)
            w.Set("storedCards", ok r)
        }
        |> unit

    [<StepDefinition("^the Cards read are \"(.*)\"$")>]
    member _.CardsRead(title: string) =
        equal [ title ] (w.Get<Card list> "storedCards" |> List.map (fun c -> c.Title)) "cards"

    [<StepDefinition("^I build a stored model of (.+)$")>]
    member _.Build(records: string) =
        w.Set("storedBuild", Model.build [] Unclear.models.[records])

    [<StepDefinition("^the model is refused for (\\w+), saying \"(.*)\"$")>]
    member _.Refused(model: string, message: string) =
        match w.Get<Result<Model, ConfigError list>> "storedBuild" with
        | Ok _ -> failwith "the model was built"
        | Error errors ->
            check (errors |> List.exists (fun e -> e.Model = model && e.Message.Contains message)) $"the errors are %A{errors}"
