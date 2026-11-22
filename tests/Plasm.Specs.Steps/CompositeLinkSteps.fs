namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm

/// composite-links.feature: threads keyed by repository and number, linked to
/// labels keyed by repository and name, and to watchers keyed by one id; and
/// cards of one-part keys linked to the same labels.
module CompositeLinks =
    type ThreadKey = { Repo: string; Number: int }
    type LabelKey = { Repo: string; Name: string }
    type WatcherId = WatcherId of int64
    type CardId = CardId of int64

    [<Table "labels">]
    type Label = { Id: LabelKey; Colour: string }

    [<Table "watchers">]
    type Watcher = { Id: WatcherId; Login: string }

    [<Table "threads">]
    type Thread =
        { Id: ThreadKey
          Title: string
          [<JoinTable "thread_labels">]
          Labels: Label list
          [<JoinTable "thread_watchers">]
          Watchers: Watcher list }

    [<Table "cards">]
    type Card =
        { Id: CardId
          Title: string
          [<JoinTable "card_labels">]
          Labels: Label list }

    let model = Model.buildOrFail [] [ typeof<Thread>; typeof<Card>; typeof<Label>; typeof<Watcher> ]

    /// Written by hand, so that the DDL is not what is tested.
    let tables =
        "create table labels (repo text not null, name text not null, colour text not null, primary key (repo, name));
         create table watchers (id bigint generated always as identity primary key, login text not null);
         create table threads (repo text not null, number integer not null, title text not null, primary key (repo, number));
         create table cards (id bigint generated always as identity primary key, title text not null);
         create table thread_labels (
           thread_repo text not null, thread_number integer not null, label_repo text not null, label_name text not null,
           primary key (thread_repo, thread_number, label_repo, label_name),
           constraint thread_labels_thread_repo_thread_number_fkey foreign key (thread_repo, thread_number) references threads (repo, number),
           constraint thread_labels_label_repo_label_name_fkey foreign key (label_repo, label_name) references labels (repo, name));
         create index thread_labels_label_repo_label_name_idx on thread_labels (label_repo, label_name);
         create table thread_watchers (
           thread_repo text not null, thread_number integer not null, watcher_id bigint not null,
           primary key (thread_repo, thread_number, watcher_id),
           constraint thread_watchers_thread_repo_thread_number_fkey foreign key (thread_repo, thread_number) references threads (repo, number),
           constraint thread_watchers_watcher_id_fkey foreign key (watcher_id) references watchers (id));
         create table card_labels (
           card_id bigint not null, label_repo text not null, label_name text not null,
           primary key (card_id, label_repo, label_name),
           constraint card_labels_card_id_fkey foreign key (card_id) references cards (id),
           constraint card_labels_label_repo_label_name_fkey foreign key (label_repo, label_name) references labels (repo, name));"

open CompositeLinks

[<Binding>]
type CompositeLinkSteps(w: World) =

    let repo () = w.Get<Repo> "linkRepo"

    let threadKey (text: string) =
        match text.Split '#' with
        | [| r; n |] -> ({ Repo = r; Number = int n }: ThreadKey)
        | _ -> failwith $"not a thread key: {text}"

    let label (text: string) : Label =
        match text.Split '/' with
        | [| r; n |] -> { Id = { Repo = r; Name = n }; Colour = "red" }
        | _ -> failwith $"not a label: {text}"

    /// "plasm/bug and plasm/docs", "plasm/bug, plasm/docs and other/x".
    let labels (text: string) =
        text.Replace(" and ", ", ").Split(", ", StringSplitOptions.RemoveEmptyEntries) |> Array.map label |> List.ofArray

    let watcher (login: string) : Watcher =
        { Id = WatcherId (w.Get<Map<string, int64>> "watchers").[login]
          Login = login }

    let showLabel (l: Label) = $"{l.Id.Repo}/{l.Id.Name}"
    let showThread (k: ThreadKey) = $"{k.Repo}#{k.Number}"

    let thread key title labelled watched : Thread =
        { Id = threadKey key
          Title = title
          Labels = labels labelled
          Watchers = [ watcher watched ] }

    let insertThread (t: Thread) =
        task {
            let! r = repo().Transact(Tx.insert t: Tx<Thread, unit>)
            w.Set("threadWrite", r)
            return r
        }

    let readThreads (q: Query<Thread>) =
        task {
            let! r = repo().All q
            w.Set("threads", ok r)
        }
        :> Task

    let rows (sql: string) = w.Db.Texts sql

    [<StepDefinition("^threads and cards linked to labels and watchers, keyed by several parts$")>]
    member _.Tables() =
        task {
            do! w.Db.MakeTables(CompositeLinks.tables, CompositeLinks.model)
            w.Set("linkRepo", w.Db.RepoWith(model = CompositeLinks.model))
        }
        :> Task

    [<StepDefinition("^the labels (.+), and the watcher (\\w+)$")>]
    member _.Seed(text: string, login: string) =
        task {
            for l in labels text do
                let! _ = w.Db.Exec($"insert into labels (repo, name, colour) values ('{l.Id.Repo}', '{l.Id.Name}', 'red')")
                ()

            let! id = w.Db.Scalar<int64>($"insert into watchers (login) values ('{login}') returning id")
            w.Set("watchers", Map [ login, id ])
        }
        :> Task

    [<StepDefinition("^I insert the Thread (\\w+#\\d+) \"(.*)\" labelled (.+), watched by (\\w+)$")>]
    member _.Insert(key: string, title: string, labelled: string, watched: string) =
        insertThread (thread key title labelled watched) :> Task

    [<StepDefinition("^the Thread (\\w+#\\d+) \"(.*)\" labelled (.+), watched by (\\w+)$")>]
    member _.Given(key: string, title: string, labelled: string, watched: string) =
        task {
            let! r = insertThread (thread key title labelled watched)
            ok r |> ignore
        }
        :> Task

    [<StepDefinition("^(thread_labels|thread_watchers) holds (.+)$")>]
    member _.Holds(table: string, text: string) =
        task {
            let sql =
                if table = "thread_labels" then
                    "select thread_repo || '#' || thread_number || ' to ' || label_repo || '/' || label_name from thread_labels order by 1"
                else
                    "select t.thread_repo || '#' || t.thread_number || ' to ' || w.login from thread_watchers t join watchers w on w.id = t.watcher_id order by 1"

            let! found = rows sql
            let expected = text.Replace(" and ", "|").Split('|') |> Array.map (fun s -> s.Trim()) |> List.ofArray
            equal expected found table
        }
        :> Task

    [<StepDefinition("^I read the Threads$")>]
    member _.Read() = readThreads (Query.from (tableOf<Thread>))

    [<StepDefinition("^the Thread (\\w+#\\d+) is labelled (.+), watched by (\\w+)$")>]
    member _.Labelled(key: string, labelled: string, watched: string) =
        let t = w.Get<Thread list> "threads" |> List.find (fun t -> t.Id = threadKey key)
        equal (labels labelled |> List.map showLabel) (t.Labels |> List.map showLabel) "labels"
        equal [ watched ] (t.Watchers |> List.map (fun x -> x.Login)) "watchers"

    [<StepDefinition("^the Thread (\\w+#\\d+) is relabelled (.+)$")>]
    member _.Relabel(key: string, labelled: string) =
        task {
            let k = threadKey key
            let! r = repo().One(plasm { for t in tableOf<Thread> do where (t.Id = k) })
            let before = (ok r).Value
            let! u = repo().Transact(Tx.update (before, { before with Labels = labels labelled }): Tx<Thread, unit>)
            ok u |> ignore
        }
        :> Task

    [<StepDefinition("^I insert a linked Card \"(.*)\" labelled (.+)$")>]
    member _.Card(title: string, labelled: string) =
        task {
            let! r = repo().Transact(Tx.insert { Id = CardId 0L; Title = title; Labels = labels labelled }: Tx<Card, unit>)
            w.Set("card", ok r)
        }
        :> Task

    [<StepDefinition("^reading the linked Card back gives the labels (.+)$")>]
    member _.CardBack(labelled: string) =
        task {
            let id = (w.Get<Card> "card").Id
            let! r = repo().One(plasm { for c in tableOf<Card> do where (c.Id = id) })
            equal (labels labelled |> List.map showLabel) ((ok r).Value.Labels |> List.map showLabel) "the card's labels"
        }
        :> Task

    [<StepDefinition("^I read the Threads labelled with the name \"(.*)\"$")>]
    member _.LabelledWith(name: string) =
        readThreads (plasm { for t in tableOf<Thread> do where (t.Labels |> List.exists (fun l -> l.Id.Name = name)) })

    [<StepDefinition("^only the Thread (\\w+#\\d+) is read$")>]
    member _.Only(key: string) =
        equal [ key ] (w.Get<Thread list> "threads" |> List.map (fun t -> showThread t.Id)) "threads read"

    [<StepDefinition("^the Thread is refused with a reference error on (\\w+), naming (\\w+)$")>]
    member _.Refused(field: string, name: string) =
        match w.Get<Result<Thread, TransactionError<unit>>> "threadWrite" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check
                (errors |> List.exists (fun e -> e.Field = field && e.Kind = FieldErrorKind.ForeignKeyConstraint name))
                $"the errors are %A{errors}"
        | other -> failwithf "expected the field's reference error, got %A" other

    [<StepDefinition("^the join table thread_labels has lost its column label_name$")>]
    member _.LostColumn() =
        match w.Db.Kind with
        | Plasm.TestDomain.Database.PostgreSQL -> w.Db.Exec "alter table thread_labels drop column label_name" |> unit
        // SQLite drops no column of a primary key: the join table is made
        // again without it.
        | Plasm.TestDomain.Database.SQLite ->
            w.Db.Exec
                "drop table thread_labels; create table thread_labels (thread_repo TEXT not null, thread_number INTEGER not null, label_repo TEXT not null, primary key (thread_repo, thread_number, label_repo));"
            |> unit

    [<StepDefinition("^I check the linked threads against the database$")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("issues", ok r)
        }
        :> Task

    [<StepDefinition("^the missing column label_name of thread_labels is reported for Thread.Labels$")>]
    member _.MissingColumn() =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MissingColumn
                 && i.Model = "Thread"
                 && i.Field = Some "Labels"
                 && i.Table = "thread_labels"
                 && i.Column = Some "label_name"))
            $"the issues are %A{issues}"
