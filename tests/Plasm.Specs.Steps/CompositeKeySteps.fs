/// A domain of its own for composite-keys.feature: issues keyed by their
/// repository and number.
namespace Plasm.Specs.CompositeKeys

open Plasm

type IssueKey = { Repo: string; Number: int }

[<Table "keyed_issues">]
type Issue =
    { Id: IssueKey
      Title: string
      [<Version>]
      Version: int }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Issue> ]

    let tables =
        "drop table if exists keyed_issues;
         create table keyed_issues (
           repo text not null,
           number integer not null,
           title text not null,
           version int not null,
           primary key (repo, number));"

/// Queries by key checked by `repo.CheckQueries`.
module KeyedGood =
    let byKey =
        Query.prepare (fun (key: Arg<IssueKey>) ->
            plasm {
                for i in tableOf<Issue> do
                    where (i.Id = key.Value)
            })

/// Keys that cannot be, or are not yet, each refused when the model is built.
module Unclear =
    type OptionalKey = { Repo: string; Number: int option }

    [<Table "optional_part">]
    type OptionalPart = { Id: OptionalKey; Title: string }

    type OneKey = { Number: int }

    [<Table "one_part">]
    type OnePart = { Id: OneKey; Title: string }

    type ListKey = { Repo: string; Numbers: int list }

    [<Table "list_part">]
    type ListPart = { Id: ListKey; Title: string }

    type Point = { X: int; Y: int }
    type ValueKey = { Repo: string; At: Point }

    [<Table "value_part">]
    type ValuePart = { Id: ValueKey; Title: string }

    type GeneratedKeyParts = { Repo: string; Number: int }

    [<Table "generated_key">]
    type GeneratedKey = { [<Generated>] Id: GeneratedKeyParts; Title: string }

    type ThreadKey = { Forum: string; Number: int }
    type TagId = TagId of int64

    [<Table "keyed_tags">]
    type Tag = { Id: TagId; Name: string }

    [<Table "keyed_threads">]
    type Thread = { Id: ThreadKey; [<JoinTable "keyed_thread_tags">] Tags: Tag list }

    type BoardId = BoardId of int64
    type CardKey = { Lane: string; Position: int }

    [<Table "keyed_cards">]
    type Card = { Id: CardKey; Board: BoardId; Text: string }

    [<Table "keyed_boards">]
    type Board = { Id: BoardId; Cards: Card list }

    let models =
        Map
            [ "a key with an optional part", [ typeof<OptionalPart> ]
              "a key with one part", [ typeof<OnePart> ]
              "a key holding a list", [ typeof<ListPart> ]
              "a key holding a value", [ typeof<ValuePart> ]
              "a generated key", [ typeof<GeneratedKey> ]
              "a linked list under a key of parts", [ typeof<Thread>; typeof<Tag> ]
              "an owned list of records keyed by parts", [ typeof<Board> ] ]

namespace Plasm.Specs

open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.Specs.CompositeKeys

/// Feature: A record whose Id is a record has a key of several columns
[<Binding>]
type CompositeKeySteps(w: World) =

    let repo () = w.Get<Repo> "keyedRepo"

    let key (text: string) =
        match text.Split '#' with
        | [| r; n |] -> { Repo = r; Number = int n }
        | _ -> failwith $"not a key: {text}"

    /// "plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"".
    let issues (text: string) =
        [ for m in Regex.Matches(text, "(\\w+#\\d+) \"([^\"]+)\"") -> { Id = key m.Groups.[1].Value; Title = m.Groups.[2].Value; Version = 1 } ]

    let show (i: Issue) = $"{i.Id.Repo}#{i.Id.Number} \"{i.Title}\""

    let stored () =
        if w.Has "keyedStored" then w.Get<Map<IssueKey, Issue>> "keyedStored" else Map.empty

    let remember (i: Issue) =
        w.Set("keyedStored", (stored ()).Add(i.Id, i))

    let readAll (q: Query<Issue>) =
        task {
            w.MarkCommands()
            let! r = repo().All q
            w.Set("keyedRead", r)
        }
        |> unit

    let read () = ok (w.Get<Result<Issue list, DbError>> "keyedRead")

    let change (before: Issue) (after: Issue) =
        task {
            w.MarkCommands()
            let! r = repo().Transact(Tx.update (before, after): Tx<_, unit>)
            w.Set("keyedChange", r)

            match r with
            | Ok i -> remember i
            | Error _ -> ()
        }

    let firstSql () = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.head

    [<StepDefinition("issues keyed by their repository and number")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec Schemas.tables
            w.Set("keyedRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("^I insert the keyed Issue (\\w+#\\d+) \"(.*)\"$")>]
    member _.Insert(k: string, title: string) =
        task {
            w.MarkCommands()
            let! r = repo().Transact(Tx.insert { Id = key k; Title = title; Version = 1 }: Tx<_, unit>)
            remember (ok r)
        }
        |> unit

    [<StepDefinition("^one command is sent, inserting the columns (.*) into keyed_issues$")>]
    member _.Inserted(columns: string) =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 1 sql.Length $"commands: %A{sql}"
        let expected = columns.Replace(" and ", ", ").Split(", ") |> Array.map (fun c -> "\"" + c + "\"") |> String.concat ", "
        check (sql.[0].StartsWith $"INSERT INTO \"keyed_issues\" ({expected})") sql.[0]

    [<StepDefinition("^the keyed Issues ((?:\\w+#\\d+ \"[^\"]+\"(?:, | and )?)+)$")>]
    member _.Given(text: string) =
        task {
            for i in issues text do
                let! r = repo().Transact(Tx.insert i: Tx<_, unit>)
                remember (ok r)
        }
        |> unit

    [<StepDefinition("^I read the keyed Issue (\\w+#\\d+) by its key, through a prepared query$")>]
    member _.ByKey(k: string) = readAll (KeyedGood.byKey (key k))

    [<StepDefinition("^the keyed Issues? read (?:is|are) (.*)$")>]
    member _.ReadAre(text: string) =
        task {
            let expected = issues text |> List.map show

            let actual =
                if w.Has "keyedRead" && not (w.Has "keyedReRead") then
                    read () |> List.map show
                else
                    []

            if actual <> expected then
                // After a change or delete, read the table again.
                let! r = repo().All(plasm { for i in tableOf<Issue> do orderBy i.Id.Repo; thenBy i.Id.Number })
                equal expected (ok r |> List.map show) "issues"
        }
        |> unit

    [<StepDefinition("^the query's condition compares \"(\\w+)\" and \"(\\w+)\"$")>]
    member _.Compares(a: string, b: string) =
        let sql = firstSql ()
        check (sql.Contains $"\"{a}\" = " && sql.Contains $"\"{b}\" = ") sql

    [<StepDefinition("^I read the keyed Issues of the repository \"(\\w+)\", ordered by number$")>]
    member _.OfRepo(r: string) =
        readAll (plasm { for i in tableOf<Issue> do where (i.Id.Repo = r); orderBy i.Id.Number })

    [<StepDefinition("I read the first two keyed Issues ordered by title")>]
    member _.Page() =
        readAll (plasm { for i in tableOf<Issue> do orderBy i.Title; take 2 })

    [<StepDefinition("^the query orders by \"(\\w+)\", then \"(\\w+)\" and \"(\\w+)\"$")>]
    member _.Orders(a: string, b: string, c: string) =
        let sql = firstSql ()
        check (Regex.IsMatch(sql, $"ORDER BY \"i\".\"{a}\", \"i\".\"{b}\", \"i\".\"{c}\"")) sql

    [<StepDefinition("^the keyed Issue (\\w+#\\d+) is renamed \"(.*)\"$")>]
    member _.Rename(k: string, title: string) =
        task {
            let before = (stored ()).[key k]
            w.Set("keyedFirstCopy", before)
            do! change before { before with Title = title }
        }
        |> unit

    [<StepDefinition("^the keyed Issue (\\w+#\\d+) is renamed \"(.*)\" from the copy read first$")>]
    member _.RenameStale(_k: string, title: string) =
        let before = w.Get<Issue> "keyedFirstCopy"
        change before { before with Title = title } |> unit

    [<StepDefinition("^the update finds the row by \"(\\w+)\", \"(\\w+)\" and \"(\\w+)\"$")>]
    member _.UpdateWhere(a: string, b: string, c: string) =
        let update = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.find (fun s -> s.StartsWith "UPDATE")
        let where = update.Substring(update.IndexOf "WHERE ")

        for col in [ a; b; c ] do
            check (where.Contains $"\"{col}\" = ") update

        w.Set("keyedReRead", true)

    [<StepDefinition("the change is refused as changed by another writer")>]
    member _.Stale() =
        match w.Get<Result<Issue, TransactionError<unit>>> "keyedChange" with
        | Error e when (match e.Failure with TxFailure.Conflict _ -> true | _ -> false) -> ()
        | other -> failwithf "expected a conflict, got %A" other

    [<StepDefinition("^the keyed Issue (\\w+#\\d+) is given the number (\\d+)$")>]
    member _.Renumber(k: string, n: int) =
        let before = (stored ()).[key k]
        change before { before with Id = { before.Id with Number = n } } |> unit

    [<StepDefinition("the change is refused, saying the primary key cannot be changed")>]
    member _.KeyChange() =
        match w.Get<Result<Issue, TransactionError<unit>>> "keyedChange" with
        | Ok i -> failwithf "the change was made: %A" i
        | Error e -> check ((string e).Contains "primary key cannot be changed") (string e)

    [<StepDefinition("^the keyed Issue (\\w+#\\d+) is deleted$")>]
    member _.Delete(k: string) =
        task {
            let! r = repo().Transact(Tx.delete (stored ()).[key k]: Tx<_, unit>)
            ok r |> ignore
            w.Set("keyedReRead", true)
        }
        |> unit

    [<StepDefinition("I read the keyed Issues ordered by their key")>]
    member _.OrderedByKey() =
        readAll (plasm { for i in tableOf<Issue> do orderBy i.Id })

    [<StepDefinition("^I read the keyed Issues whose key is less than (\\w+#\\d+)$")>]
    member _.LessThan(k: string) =
        let bound = key k
        readAll (plasm { for i in tableOf<Issue> do where (i.Id < bound) })

    [<StepDefinition("^the read is refused, saying Issue.Id and \"(.*)\"$")>]
    member _.Refused(message: string) =
        match w.Get<Result<Issue list, DbError>> "keyedRead" with
        | Error(DbError.Translation e) -> check ((string e).Contains "Issue.Id" && (string e).Contains message) (string e)
        | other -> failwithf "expected the read to be refused, got %A" other

    [<StepDefinition("I print the DDL of the keyed issues model")>]
    member _.Ddl() = w.Set("valueDdl", repo().Ddl())

    [<StepDefinition("I check the keyed issues model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("keyedIssues", ok r)
        }
        |> unit

    [<StepDefinition("no keyed schema issue is reported")>]
    member _.NoIssue() =
        let issues = w.Get<SchemaIssue list> "keyedIssues"
        equal [] issues $"issues: %A{issues}"

    [<StepDefinition("^the keyed issues' primary key is \\(repo\\) alone$")>]
    member _.OnePartPk() =
        task {
            let! _ =
                w.Db.Exec "alter table keyed_issues drop constraint keyed_issues_pkey; alter table keyed_issues add primary key (repo)"

            ()
        }
        |> unit

    [<StepDefinition("^a primary key issue is reported for Issue, naming repo and number$")>]
    member _.PkIssue() =
        let issues = w.Get<SchemaIssue list> "keyedIssues"

        check
            (issues |> List.exists (fun i -> i.Model = "Issue" && i.Message.Contains "repo" && i.Message.Contains "number" && i.Message.Contains "primary key"))
            $"issues: %A{issues}"

    [<StepDefinition("I build a keyed model of (.*)")>]
    member _.Build(records: string) =
        w.Set("storedBuild", Model.build [] Unclear.models.[records])
