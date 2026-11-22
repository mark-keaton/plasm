namespace Plasm.Specs

open System
open System.Text.RegularExpressions
open Reqnroll
open Plasm
open Plasm.TestDomain.Database

/// A table whose columns Ddl holds with checks on SQLite: a union stored as
/// its cases' names and a time.
module Chores =
    type ChoreId = ChoreId of int64

    type ChoreState =
        | Open
        | Done
        | Dropped

    [<Table "chk_chores">]
    type Chore =
        { Id: ChoreId
          Title: string
          State: ChoreState
          Due: DateTime }

    let model = Model.buildOrFail [] [ typeof<Chore> ]

/// Feature: A column's check is the model's
[<Binding>]
type ColumnCheckSteps(w: World) =
    let repo () = Repo(Chores.model, w.Db.Provider)

    /// The table made again from its own text, `edit`ed, its rows kept (SQLite
    /// alters no constraint).
    let rebuild (edit: string -> string) =
        task {
            let! text = w.Db.Scalar<string>("select sql from sqlite_schema where type = 'table' and name = 'chk_chores'")
            let edited = edit text
            check (edited <> text) $"the edit changed nothing in {text}"
            let made = Regex.Replace(edited, "^create table chk_chores\\b", "create table chk_chores_rebuilt", RegexOptions.IgnoreCase)

            let! _ =
                w.Db.Exec(
                    $"{made}; insert into chk_chores_rebuilt select * from chk_chores; drop table chk_chores; alter table chk_chores_rebuilt rename to chk_chores;"
                )

            ()
        }

    /// Where the named check is in a table's text: the start of
    /// `constraint <name>`, of its `check`, and the end of its closing
    /// parenthesis (balanced outside quotes).
    let place (name: string) (text: string) =
        let m = Regex.Match(text, $"constraint {name}\\s+(check)\\s*\\(", RegexOptions.IgnoreCase)
        check m.Success $"no check {name} in {text}"
        let mutable depth = 0
        let mutable i = m.Groups.[1].Index
        let mutable quoted = false
        let mutable ended = -1

        while ended < 0 do
            match text.[i] with
            | '\'' -> quoted <- not quoted
            | '(' when not quoted -> depth <- depth + 1
            | ')' when not quoted ->
                depth <- depth - 1
                if depth = 0 then ended <- i + 1
            | _ -> ()

            i <- i + 1

        m.Index, m.Groups.[1].Index, ended

    [<StepDefinition("^the chores table, made by Ddl$")>]
    member _.Table() = w.Db.Exec(repo().Ddl()) |> unit

    [<StepDefinition("^I check the chores model against the database$")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("choreIssues", ok r)
        }
        |> unit

    [<StepDefinition("^no issue is reported for the chores$")>]
    member _.NoIssue() =
        let issues = w.Get<SchemaIssue list> "choreIssues"
        check issues.IsEmpty $"the issues are %A{issues}"

    [<StepDefinition("^the chores model's DDL is printed$")>]
    member _.Print() = w.Set("choreDdl", repo().Ddl())

    [<StepDefinition("^its state column is held by \"(.*)\"$")>]
    member _.HeldBy(text: string) =
        let ddl = w.Get<string> "choreDdl"
        check (ddl.Contains text) ddl

    [<StepDefinition("^the chores table's check (\\w+) is \"(.*)\"$")>]
    member _.CheckIs(name: string, text: string) =
        rebuild (fun t ->
            let _, from, ended = place name t
            t.Substring(0, from) + text + t.Substring ended)
        |> unit

    [<StepDefinition("^the chores table's check (\\w+) is Ddl's, in capitals and with its spaces doubled$")>]
    member _.Respaced(name: string) =
        rebuild (fun t ->
            let _, from, ended = place name t
            // Outside quotes only: what is quoted is compared as it is.
            let respaced =
                t.Substring(from, ended - from).Split('\'')
                |> Array.mapi (fun i part -> if i % 2 = 0 then part.ToUpperInvariant().Replace(" ", "  ") else part)
                |> String.concat "'"

            t.Substring(0, from) + respaced + t.Substring ended)
        |> unit

    [<StepDefinition("^the chores table has lost its check (\\w+)$")>]
    member _.Lost(name: string) =
        rebuild (fun t ->
            let start, _, ended = place name t
            let before = t.Substring(0, start).TrimEnd()
            before.Substring(0, before.Length - 1) + t.Substring ended)
        |> unit

    [<StepDefinition("^Chore\\.(\\w+) is reported, saying \"(.*)\"$")>]
    member _.Reported(field: string, saying: string) =
        let issues = w.Get<SchemaIssue list> "choreIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.EnumLabels && i.Field = Some field && i.Message = saying))
            $"the issues are %A{issues}"

    [<StepDefinition("^Chore\\.(\\w+) is reported as of an incompatible type, saying \"(.*)\"$")>]
    member _.Incompatible(field: string, saying: string) =
        let issues = w.Get<SchemaIssue list> "choreIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.IncompatibleType && i.Field = Some field && i.Message.StartsWith saying))
            $"the issues are %A{issues}"
