namespace Plasm.Specs

open System
open Reqnroll
open Plasm

/// A domain of its own for union-fields.feature: a Kanban card whose stage is a
/// union with fields, stored in the cards row itself.
module UnionCards =

    type CardId = CardId of int64
    type Person = Person of string

    type Resolution =
        | Shipped
        | Dropped of reason: string

    type Stage =
        | Backlog
        | Doing of assignee: Person * since: DateTimeOffset
        | Done of resolution: Resolution * at: DateTimeOffset

    /// What a card's stage is, without its facts.
    type Phase =
        | Waiting
        | Working
        | Finished

    type Card =
        { Id: CardId
          Title: string
          Stage: Stage }

        /// Who works on the card: the domain's own vocabulary, which a query
        /// can use because it is a reflected definition.
        [<ReflectedDefinition>]
        member c.Assignee =
            match c.Stage with
            | Doing(who, _) -> Some who
            | _ -> None

        /// When work on the card started, if it is in progress.
        [<ReflectedDefinition>]
        member c.Since =
            match c.Stage with
            | Doing(_, since) -> Some since
            | _ -> None

        /// The card's phase, also a reflected definition.
        [<ReflectedDefinition>]
        member c.Phase =
            match c.Stage with
            | Backlog -> Waiting
            | Doing _ -> Working
            | Done _ -> Finished

    /// A union whose case holds a list: it has no columns.
    type Tagged =
        | Untagged
        | Labelled of names: string list

    type Note = { Id: CardId; Tagged: Tagged }

    let cards = schema<Card> "cards" { identityKey (fun c -> c.Id) }

    let model = Model.buildOrFail [] [ cards ]

    let stageColumns = [ "stage_assignee"; "stage_since"; "stage_resolution"; "stage_resolution_reason"; "stage_at" ]

    let table =
        "drop table if exists cards;
         create table cards (
           id bigint generated always as identity primary key,
           title text not null,
           stage text not null,
           stage_assignee text,
           stage_since timestamptz,
           stage_resolution text,
           stage_resolution_reason text,
           stage_at timestamptz);"

    let since = DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
    let ada = Person "ada"

    /// The stage a scenario names.
    let stage (text: string) =
        match text with
        | "in the backlog" -> Backlog
        | "that Ada is doing" -> Doing(ada, since)
        | "done and shipped" -> Done(Shipped, since)
        | t when t.StartsWith "done and dropped as " -> Done(Dropped(t.Substring("done and dropped as ".Length).Trim('"')), since)
        | other -> failwith $"not a stage: {other}"

    let card (s: Stage) =
        { Id = CardId 0L
          Title = "a card"
          Stage = s }

    let everyStage = [ "in the backlog"; "that Ada is doing"; "done and shipped"; "done and dropped as \"dup\"" ]

open UnionCards

/// Feature: A union with fields is stored across columns
[<Binding>]
type UnionFieldSteps(w: World) =

    let repo () = w.Get<Repo> "cardRepo"

    let insert (s: Stage) =
        task {
            let! r = repo().Insert(Changeset.insert (card s))
            return ok r
        }

    let all () =
        task {
            let! r = repo().All(plasm { for c in cards do orderBy c.Id })
            return r
        }

    let keep (query: Query<Card>) =
        task {
            w.MarkCommands()
            let! r = repo().All query
            w.Set("kept", ok r |> List.map (fun c -> c.Stage))
        }

    /// The Stage columns of the one cards row, as text (NULL as None).
    let columns () =
        task {
            let! values =
                w.Db.Scalar<string>(
                    "select concat_ws('|', stage, coalesce(stage_assignee, '~'), coalesce(stage_since::text, '~'), coalesce(stage_resolution, '~'), coalesce(stage_resolution_reason, '~'), coalesce(stage_at::text, '~')) from cards limit 1"
                )

            let parts = values.Split '|'
            return ("stage" :: stageColumns, parts |> Array.map (fun v -> if v = "~" then None else Some v) |> List.ofArray) ||> List.zip |> Map.ofList
        }

    [<StepDefinition("a cards table with the Stage columns")>]
    member _.Table() =
        task {
            let! _ = w.Db.Exec UnionCards.table
            w.Set("cardRepo", w.Db.RepoWith(model = UnionCards.model))
        }
        |> unit

    [<StepDefinition("^I insert a Card (.+)$")>]
    member _.Insert(text: string) =
        task {
            let! c = insert (stage text)
            w.Set("card", c)
        }
        |> unit

    [<StepDefinition("reading it back gives the same Card")>]
    member _.ReadBack() =
        task {
            let! r = all ()
            equal [ w.Get<Card> "card" ] (ok r) "cards"
        }
        |> unit

    /// "stage doing, stage_assignee ada and stage_since, NULL in every other Stage column":
    /// each named column holds the value given, or any value; the rest hold NULL.
    [<StepDefinition("^the cards table holds (.+), NULL in every other Stage column$")>]
    member _.Holds(spec: string) =
        task {
            let! held = columns ()

            let named =
                spec.Replace(" and ", ", ").Split(", ")
                |> Array.map (fun item ->
                    match item.Trim().Split(' ', 2) with
                    | [| name; value |] -> name, Some value
                    | [| name |] -> name, None
                    | _ -> failwith $"cannot read {item}")
                |> Map.ofArray

            for KeyValue(column, value) in held do
                match named.TryFind column with
                | Some(Some expected) -> equal (Some expected) value column
                | Some None -> check value.IsSome $"{column} holds NULL"
                | None -> equal None value column
        }
        |> unit

    [<StepDefinition("^a Card that Ada is doing$")>]
    member _.Doing() =
        task {
            let! c = insert (stage "that Ada is doing")
            w.Set("card", c)
        }
        |> unit

    [<StepDefinition("I change the Card to done and shipped")>]
    member _.ToDone() =
        task {
            let! r = repo().Update(w.Get<Card> "card" |> Changeset.edit (fun c -> { c with Stage = Done(Shipped, since) }))
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("stage_assignee and stage_since hold NULL")>]
    member _.Cleared() =
        task {
            let! held = columns ()
            equal (None, None) (held.["stage_assignee"], held.["stage_since"]) "columns"
            equal (Some "done") held.["stage"] "stage"
        }
        |> unit

    [<StepDefinition("a cards row whose stage is doing without an assignee")>]
    member _.DoingWithoutAssignee() =
        w.Db.Exec "insert into cards (title, stage, stage_since) values ('t', 'doing', now())" |> unit

    [<StepDefinition("a cards row whose stage is backlog with an assignee")>]
    member _.BacklogWithAssignee() =
        w.Db.Exec "insert into cards (title, stage, stage_assignee) values ('t', 'backlog', 'ada')" |> unit

    [<StepDefinition("I read the Cards")>]
    member _.Read() =
        task {
            let! r = all ()
            w.Set("readCards", r)
        }
        |> unit

    [<StepDefinition("the read fails for the Stage field, naming (\\w+)")>]
    member _.ReadFails(column: string) =
        match w.Get<Result<Card list, DbError>> "readCards" with
        | Error(DbError.Materialization m) ->
            equal "Stage" m.Property "field"
            check (m.Message.Contains column) $"the failure says {m.Message}"
        | other -> failwithf "expected a materialization error, got %A" other

    [<StepDefinition("I build a model of a record whose union has a case holding a list")>]
    member _.BuildList() =
        match Model.build [] [ schema<Note> "notes" { identityKey (fun n -> n.Id) } ] with
        | Ok _ -> failwith "the model was built"
        | Error errors -> w.Set("refusal", errors)

    [<StepDefinition("a Card in each stage")>]
    member _.EveryStage() =
        task {
            for s in everyStage do
                let! _ = insert (stage s)
                ()
        }
        |> unit

    [<StepDefinition("I keep the Cards whose Stage.IsDoing")>]
    member _.IsDoing() = keep (plasm { for c in cards do where c.Stage.IsDoing }) |> unit

    [<StepDefinition("I keep the Cards whose Stage is Backlog")>]
    member _.IsBacklog() = keep (plasm { for c in cards do where (c.Stage = Backlog) }) |> unit

    [<StepDefinition("I keep the Cards that are Doing by Ada, written as a match")>]
    member _.MatchDoing() =
        keep (
            plasm {
                for c in cards do
                    where (
                        match c.Stage with
                        | Doing(who, _) -> who = ada
                        | _ -> false
                    )
            }
        )
        |> unit

    [<StepDefinition("I keep the Cards that are Done and Dropped as \"(.*)\", written as a match")>]
    member _.MatchDropped(reason: string) =
        keep (
            plasm {
                for c in cards do
                    where (
                        match c.Stage with
                        | Done(Dropped r, _) -> r = reason
                        | _ -> false
                    )
            }
        )
        |> unit

    [<StepDefinition("I keep the Cards whose Assignee is Some Ada, through the member Card.Assignee")>]
    member _.ThroughMember() = keep (plasm { for c in cards do where (c.Assignee = Some ada) }) |> unit

    [<StepDefinition("I keep the Cards whose Stage equals the value Doing \\(Ada, since\\)")>]
    member _.WholeValue() =
        let value = Doing(ada, since)
        keep (plasm { for c in cards do where (c.Stage = value) }) |> unit

    [<StepDefinition("^only the Card (.+) is returned$")>]
    member _.Only(text: string) = equal [ stage text ] (w.Get<Stage list> "kept") "stages"

    [<StepDefinition("the SQL compares the column stage_assignee, without a CASE")>]
    member _.Bare() =
        let sql = w.CommandsSinceMark.Head.Sql
        check (sql.Contains "\"stage_assignee\" =" && not (sql.Contains "CASE")) sql

    [<StepDefinition("I select the title, Card.Assignee and Card.Since of the Cards that Ada is doing")>]
    member _.SelectMembers() =
        task {
            let! r = repo().All(plasm { for c in cards do where c.Stage.IsDoing; select (c.Title, c.Assignee, c.Since) })
            w.Set("selected", ok r)
        }
        |> unit

    [<StepDefinition("the row holds the title, Some Ada and Some of the time she started")>]
    member _.SelectedMembers() =
        equal [ "a card", Some ada, Some since ] (w.Get<(string * Person option * DateTimeOffset option) list> "selected") "row"

    [<StepDefinition("I count the Cards grouped by the member Card.Phase, which names each case")>]
    member _.CountByPhase() =
        task {
            let! r =
                repo().All(
                    plasm {
                        for c in cards do
                            groupBy c.Phase
                            orderBy c.Phase
                            select (c.Phase, Agg.count ())
                    }
                )

            w.Set("counts", ok r)
        }
        |> unit

    [<StepDefinition("the counts are 1 Waiting, 1 Working and 2 Finished")>]
    member _.Counts() = equal [ Waiting, 1L; Working, 1L; Finished, 2L ] (w.Get<(Phase * int64) list> "counts") "counts"

    [<StepDefinition("I bulk update every Card back to Backlog")>]
    member _.BulkBacklog() =
        task {
            let! r = repo().Execute(Bulk.update (Query.from cards, fun c -> { c with Stage = Backlog }))
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("every Card reads back in the backlog")>]
    member _.AllBacklog() =
        task {
            let! r = all ()
            check (ok r |> List.forall (fun c -> c.Stage = Backlog)) $"%A{ok r}"
        }
        |> unit

    [<StepDefinition("every other Stage column holds NULL")>]
    member _.AllNull() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from cards where coalesce(stage_assignee, stage_resolution, stage_resolution_reason) is not null or stage_since is not null or stage_at is not null")
            equal 0L n "rows with values left"
        }
        |> unit

    [<StepDefinition("the cards table has lost its stage_since column")>]
    member _.DropColumn() = w.Db.Exec "alter table cards drop column stage_since" |> unit

    [<StepDefinition("I check the cards model against the database")>]
    member _.CheckCards() =
        task {
            let! r = repo().CheckSchema()
            w.Set("cardIssues", ok r)
        }
        |> unit

    [<StepDefinition("the missing column (\\w+) is reported for Card.Stage")>]
    member _.MissingColumn(column: string) =
        let issues = w.Get<SchemaIssue list> "cardIssues"

        check
            (issues |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingColumn && i.Field = Some "Stage" && i.Column = Some column))
            $"the issues are %A{issues}"
