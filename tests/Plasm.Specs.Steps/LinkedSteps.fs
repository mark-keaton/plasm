namespace Plasm.Specs

open System.Text.RegularExpressions
open Reqnroll
open Plasm

/// A domain of its own for linked-collections.feature: cards linked to shared
/// tags, as a plain list.
module LinkedTags =

    type TagId = TagId of int64
    type CardId = CardId of int64

    type Tag = { Id: TagId; Name: string }

    type Card =
        { Id: CardId
          Title: string
          Tags: Tag list }

    let tags = schema<Tag> "tags" { identityKey (fun t -> t.Id) }

    let cards =
        schema<Card> "cards" {
            identityKey (fun c -> c.Id)
            linksOrderedBy (fun c -> c.Tags) "card_tags" "card_id" "tag_id" (fun t -> t.Name)
        }

    let model = Model.buildOrFail [] [ cards; tags ]

    let tables =
        "drop table if exists card_tags; drop table if exists cards cascade; drop table if exists tags cascade;
         create table tags (id bigint generated always as identity primary key, name text not null unique);
         create table cards (id bigint generated always as identity primary key, title text not null);
         create table card_tags (
           card_id bigint not null references cards(id),
           tag_id bigint not null references tags(id),
           primary key (card_id, tag_id));
         insert into tags (name) values ('urgent'), ('docs'), ('bug');"

    let names (text: string) = Regex.Matches(text, "\"(\\w+)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

open LinkedTags

/// Feature: A linked collection is a plain list of shared records, loaded with its owner
[<Binding>]
type LinkedSteps(w: World) =

    let repo () = w.Get<Repo> "tagRepo"

    let tagsNamed (ns: string list) =
        task {
            let! r = repo().All(plasm { for t in tags do orderBy t.Name })
            let all = ok r
            return ns |> List.map (fun n -> all |> List.find (fun t -> t.Name = n))
        }

    let insertCard (ts: Tag list) =
        task {
            let! r = repo().Insert(Changeset.insert { Id = CardId 0L; Title = "a card"; Tags = ts })
            return r
        }

    let readOne () =
        task {
            let! r = repo().All(plasm { for c in cards do orderBy c.Id })
            return ok r |> List.exactlyOne
        }

    let linkedNames () =
        w.Db.Scalar<string>("select coalesce(string_agg(t.name, ', ' order by t.name), '') from card_tags ct join tags t on t.id = ct.tag_id")

    [<StepDefinition("tags \"urgent\", \"docs\" and \"bug\", and cards, linked through card_tags")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec LinkedTags.tables
            w.Set("tagRepo", w.Db.RepoWith(model = LinkedTags.model))
        }
        |> unit

    [<StepDefinition("^a card linked to (\"\\w+\" and \"\\w+\")$")>]
    member _.Linked(text: string) =
        task {
            let! ts = tagsNamed (names text)
            let! r = insertCard ts
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("a card without tags")>]
    member _.WithoutTags() =
        task {
            let! r = insertCard []
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("three cards, each linked to \"docs\"")>]
    member _.Three() =
        task {
            let! ts = tagsNamed [ "docs" ]

            for _ in 1..3 do
                let! r = insertCard ts
                ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read the cards")>]
    member _.Read() =
        task {
            w.MarkCommands()
            let! r = repo().All(plasm { for c in cards do orderBy c.Id })
            w.Set("cards", ok r)
        }
        |> unit

    [<StepDefinition("^the card reads with the tags (.+)$")>]
    member _.ReadsWith(text: string) =
        let c = w.Get<Card list> "cards" |> List.exactlyOne
        equal (names text) (c.Tags |> List.map (fun t -> t.Name)) "tags"

    [<StepDefinition("the card reads with no tags")>]
    member _.ReadsWithout() =
        let c = w.Get<Card list> "cards" |> List.exactlyOne
        equal [] c.Tags "tags"

    [<StepDefinition("two commands are sent for the cards and their tags")>]
    member _.TwoCommands() = equal 2 w.CommandsSinceMark.Length "commands"

    [<StepDefinition("^I insert a card linked to (\"\\w+\" and \"\\w+\")$")>]
    member _.InsertLinked(text: string) =
        task {
            let! ts = tagsNamed (names text)
            let! r = insertCard ts
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("^card_tags links the card to (.+)$")>]
    member _.Links(text: string) =
        task {
            let! linked = linkedNames ()
            equal (names text |> List.sort |> String.concat ", ") linked "linked tags"
        }
        |> unit

    [<StepDefinition("the tags table still holds three tags")>]
    member _.ThreeTags() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from tags")
            equal 3L n "tags"
        }
        |> unit

    [<StepDefinition("I read it, then link it to \"bug\" and \"docs\" instead")>]
    member _.Relink() =
        task {
            let! c = readOne ()
            let! ts = tagsNamed [ "bug"; "docs" ]
            let! r = repo().Update(c |> Changeset.edit (fun c -> { c with Tags = ts }))
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("I insert a card linked to a tag that was never stored")>]
    member _.Unstored() =
        task {
            let! r = insertCard [ { Id = TagId 999L; Name = "ghost" } ]
            w.Set("linkWrite", r |> Result.map ignore)
        }
        |> unit

    [<StepDefinition("the insert fails with an error on Tags, naming (\\w+)")>]
    member _.FieldError(name: string) =
        match w.Get<Result<unit, WriteError<Card>>> "linkWrite" with
        | Error(WriteError.Invalid cs) ->
            check (cs.Errors |> List.exists (fun e -> e.Field = "Tags" && e.Kind = FieldErrorKind.ForeignKeyConstraint name)) $"%A{cs.Errors}"
        | other -> failwithf "expected invalid changes, got %A" other

    [<StepDefinition("I read it, then rename its tag \"bug\" to \"defect\" through the card")>]
    member _.Rename() =
        task {
            let! c = readOne ()

            let! r =
                repo().Update(c |> Changeset.edit (fun c -> { c with Tags = c.Tags |> List.map (fun t -> if t.Name = "bug" then { t with Name = "defect" } else t) }))

            w.Set("linkWrite", r |> Result.map ignore)
        }
        |> unit

    [<StepDefinition("the change is refused for Tags, saying a linked record belongs to its own aggregate")>]
    member _.Refused() =
        match w.Get<Result<unit, WriteError<Card>>> "linkWrite" with
        | Error(WriteError.Invalid cs) ->
            check (cs.Errors |> List.exists (fun e -> e.Field = "Tags" && e.Message.Contains "belongs to its own aggregate")) $"%A{cs.Errors}"
        | other -> failwithf "expected invalid changes, got %A" other

    [<StepDefinition("the tag \"bug\" keeps its name")>]
    member _.KeepsName() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from tags where name = 'bug'")
            equal 1L n "tags named bug"
        }
        |> unit

    [<StepDefinition("I delete the card I read")>]
    member _.Delete() =
        task {
            let! c = readOne ()
            let! r = repo().Delete(Changeset.delete c)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("card_tags is empty")>]
    member _.Empty() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from card_tags")
            equal 0L n "links"
        }
        |> unit

    [<StepDefinition("card_tags has lost its foreign key on tag_id")>]
    member _.DropFk() = w.Db.Exec "alter table card_tags drop constraint card_tags_tag_id_fkey" |> unit

    [<StepDefinition("I check the cards and tags model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("tagIssues", ok r)
        }
        |> unit

    [<StepDefinition("the join table card_tags is reported as missing its reference from tag_id to tags")>]
    member _.MissingReference() =
        let issues = w.Get<SchemaIssue list> "tagIssues"
        check (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingReference && i.Table = "card_tags" && i.Column = Some "tag_id")) $"%A{issues}"
