/// A domain of its own for json-columns.feature: a card with documents.
namespace Plasm.Specs.JsonColumns

open System
open Plasm

type CardId = CardId of int64

type Priority =
    | Low
    | High

type Item =
    { Text: string
      Done: bool
      Due: DateTime option
      Priority: Priority }

type Settings = { Theme: string; Columns: int }
type Estimate = { Points: int }

[<Table "json_cards">]
type Card =
    { Id: CardId
      Title: string
      [<Json>]
      Checklist: Item list
      [<Json>]
      Settings: Settings
      [<Json>]
      Estimate: Estimate option
      [<Json>]
      Tags: string list }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Card> ]

    let tables =
        "drop table if exists json_cards;
         create table json_cards (
           id bigint generated always as identity primary key,
           title text not null,
           checklist jsonb not null,
           settings jsonb not null,
           estimate jsonb,
           tags jsonb not null);"

/// Queries into documents checked by `repo.CheckQueries`.
module JsonGood =
    let dark =
        plasm {
            for c in tableOf<Card> do
                where (c.Settings.Theme = "dark")
        }

    let withTheme =
        Query.prepare (fun (theme: Arg<string>) ->
            plasm {
                for c in tableOf<Card> do
                    where (c.Settings.Theme = theme.Value)
                    orderBy c.Title
            })

    let unfinished =
        plasm {
            for c in tableOf<Card> do
                where (c.Checklist |> List.exists (fun i -> not i.Done))
        }

/// Fields that cannot be documents, each refused when the model is built.
module Unclear =
    type ScalarJsonId = ScalarJsonId of int64

    [<Table "scalar_json">]
    type ScalarJson = { Id: ScalarJsonId; [<Json>] Name: string }

    type ThingId = ThingId of int64

    [<Table "things">]
    type Thing = { Id: ThingId; Name: string }

    type StoredJsonId = StoredJsonId of int64

    [<Table "stored_json">]
    type StoredJson = { Id: StoredJsonId; [<Json>] Other: Thing }

    type MapJsonId = MapJsonId of int64

    [<Table "map_json">]
    type MapJson = { Id: MapJsonId; [<Json>] Counts: Map<string, int> }

    type WithId = { Id: int; Text: string }
    type IdJsonId = IdJsonId of int64

    [<Table "id_json">]
    type IdJson = { Id: IdJsonId; [<Json>] Inner: WithId list }

    let models =
        Map
            [ "a scalar marked Json", [ typeof<ScalarJson> ]
              "a stored record marked Json", [ typeof<StoredJson>; typeof<Thing> ]
              "a map marked Json", [ typeof<MapJson> ]
              "a document holding an Id record", [ typeof<IdJson> ] ]

namespace Plasm.Specs

open System
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.Specs.JsonColumns

/// Feature: A field marked Json is stored as one jsonb document
[<Binding>]
type JsonSteps(w: World) =

    let repo () = w.Get<Repo> "jsonRepo"

    let cards () =
        if w.Has "jsonCards" then w.Get<Map<string, Card>> "jsonCards" else Map.empty

    /// Parses "the items "draft" (done, high) and "review" (not done, low, due 2026-12-01)",
    /// "the theme "dark"" and "the tags "urgent" and "q4"" out of a step's text.
    let parse (title: string) (text: string) : Card =
        let items =
            [ for m in Regex.Matches(text, "\"(\\w+)\" \\((done|not done), (low|high)(?:, due (\\d{4}-\\d{2}-\\d{2}))?\\)") ->
                  { Text = m.Groups.[1].Value
                    Done = (m.Groups.[2].Value = "done")
                    Due =
                      if m.Groups.[4].Success then
                          Some(DateTime.SpecifyKind(DateTime.Parse m.Groups.[4].Value, DateTimeKind.Utc))
                      else
                          None
                    Priority = if m.Groups.[3].Value = "high" then High else Low } ]

        let theme =
            match Regex.Match(text, "the theme \"(\\w+)\"") with
            | m when m.Success -> m.Groups.[1].Value
            | _ -> "light"

        let tags =
            match Regex.Match(text, "the tags ((?:\"\\w+\"(?: and |, )?)+)") with
            | m when m.Success -> [ for t in Regex.Matches(m.Groups.[1].Value, "\"(\\w+)\"") -> t.Groups.[1].Value ]
            | _ -> []

        { Id = CardId 0L
          Title = title
          Checklist = items
          Settings = { Theme = theme; Columns = 3 }
          Estimate = None
          Tags = tags }

    let insert (c: Card) =
        task {
            let! r = repo().Transact(Tx.insert c: Tx<_, unit>)
            let saved = ok r
            w.Set("jsonCards", (cards ()).Add(saved.Title, saved))
            w.Set("jsonTitle", saved.Title)
        }

    let readAll (q: Query<Card>) =
        task {
            w.MarkCommands()
            let! r = repo().All q
            w.Set("jsonRead", r)
        }
        |> unit

    let read () = ok (w.Get<Result<Card list, DbError>> "jsonRead")

    let column (name: string) =
        w.Db.Scalar<obj>($"select {name}::text from json_cards where title = @t", [ "t", box (w.Get<string> "jsonTitle") ])

    [<StepDefinition("cards with a JSON checklist, settings, estimate and tags")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec Schemas.tables
            w.Set("jsonRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("^I insert a documented Card \"(\\w+)\" with (.*)$")>]
    member _.Insert(title: string, text: string) =
        task {
            w.MarkCommands()
            do! insert (parse title text)
        }
        |> unit

    [<StepDefinition("^a Card \"(\\w+)\" with (.*)$")>]
    member _.Given(title: string, text: string) = insert (parse title text) |> unit

    [<StepDefinition("^a Card \"(\\w+)\" whose checklist column is set behind Plasm's back to (.*)$")>]
    member _.Behind(title: string, document: string) =
        task {
            do! insert (parse title "")
            let! _ = w.Db.Exec("update json_cards set checklist = @d::jsonb where title = @t", [ "d", box document; "t", box title ])
            ()
        }
        |> unit

    [<StepDefinition("one command is sent, inserting into json_cards")>]
    member _.OneInsert() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 1 sql.Length $"commands: %A{sql}"
        check (sql.[0].StartsWith "INSERT INTO \"json_cards\"") sql.[0]

    [<StepDefinition("the checklist column holds (.*)")>]
    member _.Holds(document: string) =
        task {
            let! stored = column "checklist"
            check (JsonNode.DeepEquals(JsonNode.Parse(string stored), JsonNode.Parse document)) $"the column holds {stored}"
        }
        |> unit

    [<StepDefinition("the estimate column is NULL")>]
    member _.EstimateNull() =
        task {
            let! stored = column "estimate"
            check (isNull stored || stored = box DBNull.Value) $"the column holds {stored}"
        }
        |> unit

    [<StepDefinition("I read the Cards and their documents")>]
    member _.Read() =
        readAll (plasm { for c in tableOf<Card> do orderBy c.Title })

    [<StepDefinition("the Card is read as it was inserted")>]
    member _.AsInserted() =
        equal [ (cards ()).["Plan"] ] (read ()) "cards"

    [<StepDefinition("the read fails, naming the path (.*)")>]
    member _.Fails(path: string) =
        match w.Get<Result<Card list, DbError>> "jsonRead" with
        | Ok rows -> failwithf "the read succeeded: %A" rows
        | Error e -> check ((string e).Contains path) (string e)

    [<StepDefinition("I read the Cards whose theme is \"(.*)\"")>]
    member _.Theme(theme: string) =
        readAll (plasm { for c in tableOf<Card> do where (c.Settings.Theme = theme); orderBy c.Title })

    [<StepDefinition("I read the Cards whose settings are the theme \"(\\w+)\" with (\\d+) columns")>]
    member _.WholeSettings(theme: string, columns: int) =
        let settings = { Theme = theme; Columns = columns }
        readAll (plasm { for c in tableOf<Card> do where (c.Settings = settings); orderBy c.Title })

    [<StepDefinition("I read the Cards whose theme is \"(.*)\", through a prepared query")>]
    member _.PreparedTheme(theme: string) = readAll (JsonGood.withTheme theme)

    [<StepDefinition("I check the queries of a module whose document queries translate")>]
    member _.CheckGood() =
        w.Set("problems", repo().CheckQueries(typeof<JsonSteps>.Assembly, "Plasm.Specs.JsonColumns.JsonGood"))

    [<StepDefinition("I read the Cards whose estimate is over 3 points")>]
    member _.OptionalDocument() =
        readAll (plasm { for c in tableOf<Card> do where (c.Estimate.Value.Points > 3) })

    [<StepDefinition("the read is refused, saying Card.Estimate is an optional document")>]
    member _.OptionalRefused() =
        match w.Get<Result<Card list, DbError>> "jsonRead" with
        | Error(DbError.Translation e) -> check ((string e).Contains "Card.Estimate" && (string e).Contains "optional") (string e)
        | other -> failwithf "expected the read to be refused, got %A" other

    [<StepDefinition("I read the Cards with an item not done")>]
    member _.NotDone() =
        readAll (plasm { for c in tableOf<Card> do where (c.Checklist |> List.exists (fun i -> not i.Done)); orderBy c.Title })

    [<StepDefinition("I read the Cards tagged \"(.*)\"")>]
    member _.Tagged(tag: string) =
        readAll (plasm { for c in tableOf<Card> do where (List.contains tag c.Tags); orderBy c.Title })

    [<StepDefinition("^the documented Cards read are \"(\\w+)\"$")>]
    member _.ReadAre(title: string) =
        equal [ title ] (read () |> List.map (fun c -> c.Title)) "cards"

    [<StepDefinition("I select the themes of the Cards, ordered by title")>]
    member _.Themes() =
        task {
            let! r = repo().All(plasm { for c in tableOf<Card> do orderBy c.Title; select c.Settings.Theme })
            w.Set("jsonThemes", ok r)
        }
        |> unit

    [<StepDefinition("the themes are \"(.*)\" and \"(.*)\"")>]
    member _.ThemesAre(a: string, b: string) =
        equal [ a; b ] (w.Get<string list> "jsonThemes") "themes"

    [<StepDefinition("the item \"(\\w+)\" of \"(\\w+)\" is ticked")>]
    member _.Tick(item: string, title: string) =
        task {
            let before = (cards ()).[title]
            let ticked = before.Checklist |> List.map (fun i -> if i.Text = item then { i with Done = true } else i)
            w.MarkCommands()
            let! r = repo().Transact(Tx.update (before, { before with Checklist = ticked }): Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the update sets the checklist column and no other")>]
    member _.UpdateColumns() =
        let update = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.find (fun s -> s.StartsWith "UPDATE")
        let set = update.Substring(0, update.IndexOf "\nWHERE ")
        check (set.Contains "\"checklist\"") update

        for c in [ "\"title\""; "\"settings\""; "\"estimate\""; "\"tags\"" ] do
            check (not (set.Contains c)) update

    [<StepDefinition("\"(\\w+)\" is read with every item done")>]
    member _.AllDone(title: string) =
        task {
            let! r = repo().One(plasm { for c in tableOf<Card> do where (c.Title = title) })
            check ((ok r).Value.Checklist |> List.forall (fun i -> i.Done)) $"%A{r}"
        }
        |> unit

    [<StepDefinition("I print the DDL of the documented cards model")>]
    member _.Ddl() = w.Set("valueDdl", repo().Ddl())

    [<StepDefinition("the estimate column is text")>]
    member _.EstimateText() =
        task {
            let! _ = w.Db.Exec "alter table json_cards alter column estimate type text"
            ()
        }
        |> unit

    [<StepDefinition("I check the documented cards model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("jsonIssues", ok r)
        }
        |> unit

    [<StepDefinition("an incompatible type is reported for Card.Estimate")>]
    member _.Incompatible() =
        let issues = w.Get<SchemaIssue list> "jsonIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.IncompatibleType && i.Model = "Card" && i.Field = Some "Estimate"))
            $"issues: %A{issues}"

    [<StepDefinition("I make a repository of the documented cards model over a provider without JSON")>]
    member _.WithoutJson() =
        let outcome =
            try
                Repo(Schemas.model, TestDialectProvider(w.Db.Provider)) |> ignore
                "made"
            with
            | PlasmConfigurationException message -> message
            | ex -> ex.Message

        w.Set("jsonRepoOutcome", outcome)

    [<StepDefinition("making it fails, naming (.*)")>]
    member _.MakingFails(field: string) =
        let outcome = w.Get<string> "jsonRepoOutcome"
        check (outcome.Contains field) outcome

    [<StepDefinition("I build a JSON model of (.*)")>]
    member _.Build(records: string) =
        w.Set("storedBuild", Model.build [] Unclear.models.[records])
