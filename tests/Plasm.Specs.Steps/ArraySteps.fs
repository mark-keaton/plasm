/// A domain of its own for array-columns.feature: an issue with lists of scalars.
namespace Plasm.Specs.ArrayColumns

open System
open Plasm

type IssueId = IssueId of int64

type Login = Login of string

/// Read through create: lowercase, 1 to 20 characters.
type Topic =
    private
    | Topic of string

    [<ReflectedDefinition>]
    member t.Value =
        let (Topic v) = t
        v

    static member create(text: string) : Result<Topic, string> =
        if String.IsNullOrEmpty text || text.Length > 20 then Error "must be 1 to 20 characters"
        elif text <> text.ToLowerInvariant() then Error "must be lowercase"
        else Ok(Topic text)

type Kind =
    | Bug
    | Feature

[<Table "gh_issues">]
type Issue =
    { Id: IssueId
      Title: string
      Labels: string list
      Assignees: Login list
      Points: int list
      Topics: Topic list
      Kinds: Kind list }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Issue> ]

    let tables =
        "drop table if exists gh_issues;
         create table gh_issues (
           id bigint generated always as identity primary key,
           title text not null,
           labels text[] not null,
           assignees text[] not null,
           points integer[] not null,
           topics text[] not null,
           kinds text[] not null);"

    /// The table in SQLite's own SQL: each list a TEXT holding one JSON array,
    /// held to that form as the Ddl holds it. `columns` replaces a column's
    /// declaration (a scenario's change behind Plasm's back).
    let tablesSqlite (columns: Map<string, string>) =
        let column name declared =
            match columns.TryFind name with
            | Some d -> $"{name} {d}"
            | None -> $"{name} {declared} constraint gh_issues_{name}_check check (json_valid({name}) and json_type({name}) = 'array')"

        "create table gh_issues (
           id INTEGER not null primary key,
           title TEXT not null,
           " + String.concat ",\n           " [ for c in [ "labels"; "assignees"; "points"; "topics"; "kinds" ] -> column c "TEXT not null" ] + ");"

    let topic (text: string) =
        match Topic.create text with
        | Ok t -> t
        | Error e -> failwith e

/// Queries over arrays checked by `repo.CheckQueries`.
module ArrayGood =
    let labelled =
        Query.prepare (fun (label: Arg<string>) ->
            plasm {
                for i in tableOf<Issue> do
                    where (List.contains label.Value i.Labels)
                    orderBy i.Title
            })

/// Fields that cannot be array columns, each refused when the model is built.
module Unclear =
    type OptionalItemsId = OptionalItemsId of int64

    [<Table "optional_items">]
    type OptionalItems = { Id: OptionalItemsId; Items: string option list }

    type OptionalListId = OptionalListId of int64

    [<Table "optional_list">]
    type OptionalList = { Id: OptionalListId; Items: string list option }

    type ArrayFieldId = ArrayFieldId of int64

    [<Table "array_field">]
    type ArrayField = { Id: ArrayFieldId; Items: string[] }

    type Point = { X: int; Y: int }
    type ValueListId = ValueListId of int64

    [<Table "value_list">]
    type ValueList = { Id: ValueListId; Points: Point list }

    type Shape =
        | Circle of radius: int
        | Square of side: int

    type UnionListId = UnionListId of int64

    [<Table "union_list">]
    type UnionList = { Id: UnionListId; Shapes: Shape list }

    /// A fieldless union stored as a PostgreSQL enum (Scalar.asPgEnum).
    type Stage =
        | Draft
        | Done

    type EnumListId = EnumListId of int64

    [<Table "enum_list">]
    type EnumList = { Id: EnumListId; Stages: Stage list }

    /// The mappings a model is built with, where it needs one.
    let scalars =
        Map [ "a list of a PostgreSQL enum", [ Scalar.enum [ Draft, "draft"; Done, "done" ] |> Scalar.asPgEnum "stage" ] ]

    let models =
        Map
            [ "a list of a PostgreSQL enum", [ typeof<EnumList> ]
              "a list of optional strings", [ typeof<OptionalItems> ]
              "an optional list", [ typeof<OptionalList> ]
              "an array field", [ typeof<ArrayField> ]
              "a list of values", [ typeof<ValueList> ]
              "a list of unions of fields", [ typeof<UnionList> ] ]

namespace Plasm.Specs

open System
open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.Specs.ArrayColumns
open Plasm.TestDomain.Database

/// Feature: A list of scalars is stored as one array column
[<Binding>]
type ArraySteps(w: World) =

    let repo () = w.Get<Repo> "arrayRepo"

    let issues () =
        if w.Has "arrayIssues" then w.Get<Map<string, Issue>> "arrayIssues" else Map.empty

    let quoted (name: string) (text: string) =
        match Regex.Match(text, name + " ((?:\"[\\w-]+\"(?: and |, )?)+)") with
        | m when m.Success -> [ for q in Regex.Matches(m.Groups.[1].Value, "\"([\\w-]+)\"") -> q.Groups.[1].Value ]
        | _ -> []

    /// Parses "labelled "bug" and "ui", assigned to "ada", with the points 3
    /// and 5, the topics "core", and the kinds Bug" out of a step's text.
    let parse (title: string) (text: string) : Issue =
        let points =
            match Regex.Match(text, "the points ((?:\\d+(?: and |, )?)+)") with
            | m when m.Success -> [ for d in Regex.Matches(m.Groups.[1].Value, "\\d+") -> int d.Value ]
            | _ -> []

        let kinds =
            match Regex.Match(text, "the kinds ((?:(?:Bug|Feature)(?: and |, )?)+)") with
            | m when m.Success -> [ for k in Regex.Matches(m.Groups.[1].Value, "Bug|Feature") -> if k.Value = "Bug" then Bug else Feature ]
            | _ -> []

        { Id = IssueId 0L
          Title = title
          Labels = quoted "labelled" text
          Assignees = quoted "assigned to" text |> List.map Login
          Points = points
          Topics = quoted "the topics" text |> List.map Schemas.topic
          Kinds = kinds }

    let insert (issue: Issue) =
        task {
            let! r = repo().Transact(Tx.insert issue: Tx<_, unit>)
            let stored = ok r
            w.Set("arrayIssues", (issues ()).Add(issue.Title, stored))
            w.Set("arrayTitle", issue.Title)
        }

    let readAll (q: Query<Issue>) =
        task {
            w.MarkCommands()
            let! r = repo().All q
            w.Set("arrayRead", r)
        }
        |> unit

    let read () = ok (w.Get<Result<Issue list, DbError>> "arrayRead")

    let column (name: string) =
        match w.Db.Kind with
        | PostgreSQL -> w.Db.Scalar<obj>($"select {name}::text from gh_issues where title = @t", [ "t", box (w.Get<string> "arrayTitle") ])
        | SQLite -> w.Db.Scalar<obj>($"select {name} from gh_issues where title = @t", [ "t", box (w.Get<string> "arrayTitle") ])

    /// A list written {bug,ui} (each element as it reads; NULL for none) as
    /// the database holds it: PostgreSQL's array literal, or SQLite's JSON
    /// array, the points as numbers.
    let stored (name: string) (braces: string) =
        match w.Db.Kind with
        | PostgreSQL -> braces
        | SQLite ->
            let inner = braces.Trim('{', '}')
            let items = if inner = "" then [] else inner.Split(',') |> List.ofArray

            let json (item: string) =
                if item = "NULL" then "null"
                elif name = "points" then item
                else "\"" + item + "\""

            "[" + String.concat "," (items |> List.map json) + "]"

    /// SQLite has no ALTER COLUMN: the table is made again with a column
    /// declared otherwise, its rows kept.
    let rebuild (columns: Map<string, string>) =
        task {
            let! _ =
                w.Db.Exec(
                    "alter table gh_issues rename to gh_issues_was; "
                    + Schemas.tablesSqlite columns
                    + " insert into gh_issues select * from gh_issues_was; drop table gh_issues_was;"
                )

            ()
        }

    [<StepDefinition("issues with labels, assignees, points, topics and kinds")>]
    member _.Tables() =
        task {
            let! _ =
                match w.Db.Kind with
                | PostgreSQL -> w.Db.Exec Schemas.tables
                | SQLite -> w.Db.Exec("drop table if exists gh_issues; " + Schemas.tablesSqlite Map.empty)

            w.Set("arrayRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("^I insert an Issue \"(\\w+)\" (.*)$")>]
    member _.Insert(title: string, text: string) =
        task {
            w.MarkCommands()
            do! insert (parse title text)
        }
        |> unit

    [<StepDefinition("^an Issue \"(\\w+)\" (.*)$")>]
    member _.Given(title: string, text: string) = insert (parse title text) |> unit

    [<StepDefinition("^the (\\w+) column of \"(\\w+)\" is set behind Plasm's back to (.*)$")>]
    member _.Behind(name: string, title: string, value: string) =
        task {
            match w.Db.Kind with
            | PostgreSQL ->
                let sql =
                    // Behind Plasm's back indeed: the column's NOT NULL goes too.
                    if value = "NULL" then $"alter table gh_issues alter column {name} drop not null; update gh_issues set {name} = NULL where title = @t"
                    else $"update gh_issues set {name} = '{value}' where title = @t"

                let! _ = w.Db.Exec(sql, [ "t", box title ])
                ()
            | SQLite ->
                if value = "NULL" then
                    do! rebuild (Map.ofList [ name, "TEXT" ])
                    let! _ = w.Db.Exec($"update gh_issues set {name} = NULL where title = @t", [ "t", box title ])
                    ()
                else
                    let! _ = w.Db.Exec($"update gh_issues set {name} = @v where title = @t", [ "v", box (stored name value); "t", box title ])
                    ()
        }
        |> unit

    [<StepDefinition("one command is sent, inserting into gh_issues")>]
    member _.OneInsert() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 1 sql.Length $"commands: %A{sql}"
        check (sql.[0].StartsWith "INSERT INTO \"gh_issues\"") sql.[0]

    [<StepDefinition("^the (\\w+) column holds (\\{.*\\})$")>]
    member _.Holds(name: string, value: string) =
        task {
            let! held = column name
            equal (stored name value) (string held) $"the {name} column"
        }
        |> unit

    [<StepDefinition("I read the Issues")>]
    member _.Read() =
        readAll (plasm { for i in tableOf<Issue> do orderBy i.Title })

    [<StepDefinition("the Issue is read as it was inserted")>]
    member _.AsInserted() =
        equal [ (issues ()).["Crash"] ] (read ()) "issues"

    [<StepDefinition("^the Issue \"(\\w+)\" is read with every list empty$")>]
    member _.Empty(title: string) =
        task {
            let! r = repo().One(plasm { for i in tableOf<Issue> do where (i.Title = title) })
            let i = (ok r).Value
            check (i.Labels.IsEmpty && i.Assignees.IsEmpty && i.Points.IsEmpty && i.Topics.IsEmpty && i.Kinds.IsEmpty) $"%A{i}"
        }
        |> unit

    [<StepDefinition("^the read fails, saying (\\S+) and \"(.*)\"$")>]
    member _.Fails(path: string, message: string) =
        match w.Get<Result<Issue list, DbError>> "arrayRead" with
        | Ok rows -> failwithf "the read succeeded: %A" rows
        | Error e -> check ((string e).Contains path && (string e).Contains message) (string e)

    [<StepDefinition("I select the labels of the Issues")>]
    member _.SelectLabels() =
        task {
            let! r = repo().All(plasm { for i in tableOf<Issue> do orderBy i.Title; select i.Labels })
            w.Set("arrayLabels", ok r)
        }
        |> unit

    [<StepDefinition("the labels selected are \"(.*)\" and \"(.*)\"")>]
    member _.LabelsAre(a: string, b: string) =
        equal [ [ a; b ] ] (w.Get<string list list> "arrayLabels") "labels"

    [<StepDefinition("^\"(\\w+)\" is relabelled \"(.*)\" and \"(.*)\"$")>]
    member _.Relabel(title: string, a: string, b: string) =
        task {
            let before = (issues ()).[title]
            w.MarkCommands()
            let! r = repo().Transact(Tx.update (before, { before with Labels = [ a; b ] }): Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the update sets the labels column and no other")>]
    member _.UpdateColumns() =
        let update = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.find (fun s -> s.StartsWith "UPDATE")
        let set = update.Substring(0, update.IndexOf "\nWHERE ")
        check (set.Contains "\"labels\"") update

        for c in [ "\"title\""; "\"assignees\""; "\"points\""; "\"topics\""; "\"kinds\"" ] do
            check (not (set.Contains c)) update

    [<StepDefinition("^\"(\\w+)\" is read labelled \"(.*)\" and \"(.*)\"$")>]
    member _.ReadLabelled(title: string, a: string, b: string) =
        task {
            let! r = repo().One(plasm { for i in tableOf<Issue> do where (i.Title = title) })
            equal [ a; b ] (ok r).Value.Labels "labels"
        }
        |> unit

    [<StepDefinition("^I read the Issues labelled \"(\\w+)\"$")>]
    member _.Labelled(label: string) =
        readAll (plasm { for i in tableOf<Issue> do where (List.contains label i.Labels); orderBy i.Title })

    [<StepDefinition("^I read the Issues assigned to \"(\\w+)\"$")>]
    member _.Assigned(login: string) =
        let who = Login login
        readAll (plasm { for i in tableOf<Issue> do where (List.contains who i.Assignees); orderBy i.Title })

    [<StepDefinition("^I read the Issues labelled \"(\\w+)\", through a prepared query$")>]
    member _.Prepared(label: string) = readAll (ArrayGood.labelled label)

    [<StepDefinition("I read the Issues with no labels")>]
    member _.NoLabels() =
        readAll (plasm { for i in tableOf<Issue> do where (List.isEmpty i.Labels); orderBy i.Title })

    [<StepDefinition("I read the Issues with some label")>]
    member _.SomeLabel() =
        readAll (plasm { for i in tableOf<Issue> do where (not (List.isEmpty i.Labels)); orderBy i.Title })

    [<StepDefinition("^the Issues read are \"(\\w+)\"$")>]
    member _.ReadAre(title: string) =
        equal [ title ] (read () |> List.map (fun i -> i.Title)) "issues"

    [<StepDefinition("^on (PostgreSQL|SQLite) the query's condition is (.*)$")>]
    member _.Condition(database: string, text: string) =
        if string w.Db.Kind = database then
            let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.head
            check (sql.Contains text) sql

    [<StepDefinition("^I read the Issues whose labels are exactly \"(\\w+)\"$")>]
    member _.Exactly(label: string) =
        readAll (plasm { for i in tableOf<Issue> do where (i.Labels = [ label ]) })

    [<StepDefinition("I read the Issues with more than one label")>]
    member _.MoreThanOne() =
        readAll (plasm { for i in tableOf<Issue> do where (List.length i.Labels > 1) })

    [<StepDefinition("^I read the Issues with a label starting with \"(\\w+)\"$")>]
    member _.StartingWith(prefix: string) =
        readAll (plasm { for i in tableOf<Issue> do where (i.Labels |> List.exists (fun l -> l.StartsWith prefix)) })

    [<StepDefinition("I read the Issues ordered by their labels")>]
    member _.OrderedByLabels() =
        readAll (plasm { for i in tableOf<Issue> do orderBy i.Labels })

    [<StepDefinition("^the read is refused, saying Issue.Labels and \"(.*)\"$")>]
    member _.Refused(message: string) =
        match w.Get<Result<Issue list, DbError>> "arrayRead" with
        | Error(DbError.Translation e) -> check ((string e).Contains "Issue.Labels" && (string e).Contains message) (string e)
        | other -> failwithf "expected the read to be refused, got %A" other

    [<StepDefinition("I print the DDL of the issues model")>]
    member _.Ddl() = w.Set("valueDdl", repo().Ddl())

    [<StepDefinition("^the (labels|points) column is (text|bigint\\[\\])$")>]
    member _.ColumnIs(name: string, sqlType: string) =
        task {
            let using' = if sqlType = "text" then $"array_to_string({name}, ',')" else $"{name}::{sqlType}"
            let! _ = w.Db.Exec $"alter table gh_issues alter column {name} type {sqlType} using {using'}"
            ()
        }
        |> unit

    [<StepDefinition("the labels column may hold NULL")>]
    member _.LabelsNullable() =
        task {
            match w.Db.Kind with
            | PostgreSQL ->
                let! _ = w.Db.Exec "alter table gh_issues alter column labels drop not null"
                ()
            | SQLite -> do! rebuild (Map.ofList [ "labels", "TEXT constraint gh_issues_labels_check check (json_valid(labels) and json_type(labels) = 'array')" ])
        }
        |> unit

    [<StepDefinition("^the (labels|points) column is declared (INTEGER|REAL)$")>]
    member _.DeclaredOtherwise(name: string, declared: string) = rebuild (Map.ofList [ name, declared + " not null" ]) |> unit

    [<StepDefinition("each list column is held to a JSON array by its check")>]
    member _.HeldToJson() =
        let ddl = w.Get<string> "valueDdl"

        for c in [ "labels"; "assignees"; "points"; "topics"; "kinds" ] do
            check (ddl.Contains $"constraint gh_issues_{c}_check check (json_valid({c}) and json_type({c}) = 'array')") ddl

    [<StepDefinition("a nullable column is reported for Issue.Labels")>]
    member _.Nullable() =
        let issues = w.Get<SchemaIssue list> "arraySchemaIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.NullableColumn && i.Record = "Issue" && i.Field = Some "Labels"))
            $"issues: %A{issues}"

    [<StepDefinition("I check the issues model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("arraySchemaIssues", ok r)
        }
        |> unit

    [<StepDefinition("^an incompatible type is reported for Issue.(\\w+), naming (.*)$")>]
    member _.Incompatible(field: string, needed: string) =
        let issues = w.Get<SchemaIssue list> "arraySchemaIssues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.IncompatibleType && i.Record = "Issue" && i.Field = Some field && i.Message.Contains needed))
            $"issues: %A{issues}"

    [<StepDefinition("no schema issue is reported")>]
    member _.NoIssue() =
        let issues = w.Get<SchemaIssue list> "arraySchemaIssues"
        equal [] issues $"issues: %A{issues}"

    [<StepDefinition("I build an array model of (.*)")>]
    member _.Build(records: string) =
        let scalars = Unclear.scalars |> Map.tryFind records |> Option.defaultValue []
        w.Set("storedBuild", Model.build scalars Unclear.models.[records])
