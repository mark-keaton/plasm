namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.Specs.Recursive
open Plasm.TestDomain.Database

/// Feature: A query can follow a record that refers to its own kind
[<Binding>]
type RecursiveSteps(w: World) =
    let events = ResizeArray<CommandEvent>()
    let mutable repo: Repo = Unchecked.defaultof<_>

    let named (name: string) = plasm { for c in tableOf<Category> do where (c.Name = name) }

    /// Everything under the category `name`: it, its children, theirs, ...
    let under (name: string) =
        Query.recursive (
            named name,
            fun found ->
                plasm {
                    for c in tableOf<Category> do
                    join p in found on (c.Parent = Some p.Id)
                    select c
                }
        )

    /// Everything above the category `name`: it, its parent, its parent's, ...
    let above (name: string) =
        Query.recursive (
            named name,
            fun found ->
                plasm {
                    for p in tableOf<Category> do
                    join c in found on (Some p.Id = c.Parent)
                    select p
                }
        )

    let keep (r: Result<'T, DbError>) = w.Set("recursiveResult", r |> Result.map box)

    let read (q: Projection<'R>) =
        task {
            events.Clear()
            let! r = repo.All q
            keep r
        }
        :> Task

    let rows () =
        match w.Get<Result<obj, DbError>> "recursiveResult" with
        | Ok v -> v
        | Error e -> failwithf "unexpected error: %O" e

    let refusal () =
        match w.Get<Result<obj, DbError>> "recursiveResult" with
        | Error(DbError.Translation t) -> string t
        | Error(DbError.Database f) -> failwithf "expected a refusal, the database failed: %O" f
        | other -> failwithf "expected a refusal, got %A" other

    [<StepDefinition("the categories:")>]
    member _.Categories(table: DataTable) =
        task {
            let! _ =
                match w.Db.Kind with
                | PostgreSQL -> w.Db.Exec Tree.tables
                | SQLite -> w.Db.Exec(Repo(Tree.model, w.Db.Provider).Ddl())

            for row in table.Rows do
                let parent = row.["parent"]

                let! _ =
                    if String.IsNullOrEmpty parent then
                        w.Db.Exec("insert into rec_categories (name) values (@name)", [ "name", box row.["name"] ])
                    else
                        w.Db.Exec(
                            "insert into rec_categories (name, parent) values (@name, (select id from rec_categories where name = @parent))",
                            [ "name", box row.["name"]; "parent", box parent ]
                        )

                ()

            repo <- Repo(Tree.model, w.Db.Provider, { RepoOptions.Default with OnCommand = Some events.Add })
        }
        :> Task

    [<StepDefinition("I read the categories under Tools, with their depth, by depth and name")>]
    member _.UnderTools() =
        read (plasm { for c in under "Tools" do orderBy (Query.depth c); thenBy c.Name; select (c.Name, Query.depth c) })

    // Were the cycle not stopped, the statement would not end: the timeout
    // makes that a failure within seconds, not a hang.
    [<StepDefinition("I read the categories under Tools, with their depth, by depth and name, on a connection whose statements time out after 5 seconds")>]
    member _.UnderToolsTimed() =
        task {
            let query = plasm { for c in under "Tools" do orderBy (Query.depth c); thenBy c.Name; select (c.Name, Query.depth c) }

            match w.Db.Kind with
            | PostgreSQL ->
                let timed = Plasm.Postgres.Postgres.connect (w.Db.ConnectionString + ";Options=-c statement_timeout=5000")
                let limited = Repo(Tree.model, timed, { RepoOptions.Default with OnCommand = Some events.Add })
                events.Clear()
                let! r = limited.All query
                keep r
            | SQLite ->
                // SQLite has no statement timeout: a token cancelled after 5
                // seconds interrupts the statement (cancellation.feature).
                use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                events.Clear()
                let! r = repo.All(query, cts.Token)
                keep r
        }
        :> Task

    [<StepDefinition("the statement sent stops a cycle by the category's key")>]
    member _.CycleClause() =
        match events |> Seq.filter (fun e -> e.Kind = CommandKind.Statement) |> List.ofSeq with
        | [ e ] ->
            match w.Db.Kind with
            | PostgreSQL -> check (e.Sql.Contains "CYCLE \"id\" SET") e.Sql
            // The path of the keys above each row (ISqlDialect.Cycle).
            | SQLite ->
                check
                    (e.Sql.Contains "json_array(json_array(\"c\".\"id\")) AS \"plasm_path\""
                     && e.Sql.Contains "EXISTS (SELECT 1 FROM json_each(\"p\".\"plasm_path\") WHERE value = json_array(\"c\".\"id\"))")
                    e.Sql
        | other -> failwithf "expected one statement, got %A" (other |> List.map (fun e -> e.Sql))

    [<StepDefinition("I read the categories above Hand saws, with their depth, by depth")>]
    member _.AboveHandSaws() =
        read (plasm { for c in above "Hand saws" do orderBy (Query.depth c); select (c.Name, Query.depth c) })

    [<StepDefinition("the rows are:")>]
    member _.Rows(table: DataTable) =
        let expected = [ for r in table.Rows -> r.["name"], int r.["depth"] ]
        equal expected (unbox<(string * int) list> (rows ())) "the rows"

    [<StepDefinition("one statement was sent, a recursive one")>]
    member _.OneStatement() =
        match events |> Seq.filter (fun e -> e.Kind = CommandKind.Statement) |> List.ofSeq with
        | [ e ] -> check (e.Sql.Contains "WITH RECURSIVE" && e.Sql.Contains "UNION ALL") e.Sql
        | other -> failwithf "expected one statement, got %A" (other |> List.map (fun e -> e.Sql))

    [<StepDefinition("I read the Category records under Garden, by name")>]
    member _.Records() =
        task {
            let! r = repo.All(plasm { for c in under "Garden" do orderBy c.Name })
            keep r
        }
        :> Task

    [<StepDefinition("the records are Garden and Hoses, each with its parent")>]
    member _.TheRecords() =
        match unbox<Category list> (rows ()) with
        | [ garden; hoses ] ->
            equal ("Garden", None) (garden.Name, garden.Parent) "Garden"
            equal ("Hoses", Some garden.Id) (hoses.Name, hoses.Parent) "Hoses"
        | other -> failwithf "expected Garden and Hoses, got %A" other

    [<StepDefinition("I read the names of the categories under Tools deeper than 0, by name")>]
    member _.Deeper() =
        read (plasm { for c in under "Tools" do where (Query.depth c > 0); orderBy c.Name; select c.Name })

    [<StepDefinition("I read the second and third names under Tools, by name")>]
    member _.Page() =
        read (plasm { for c in under "Tools" do orderBy c.Name; skip 1; take 2; select c.Name })

    [<StepDefinition("^the names are (.*)$")>]
    member _.Names(listed: string) =
        let expected = listed.Replace(" and ", ", ").Split(", ") |> List.ofArray
        equal expected (unbox<string list> (rows ())) "the names"

    [<StepDefinition("Tools has been given Hand saws as its parent")>]
    member _.Cycle() =
        task {
            let! _ = w.Db.Exec "update rec_categories set parent = (select id from rec_categories where name = 'Hand saws') where name = 'Tools'"
            ()
        }
        :> Task

    [<StepDefinition("I read a recursive query whose step reads the categories without joining the rows found")>]
    member _.NoJoin() =
        read (plasm { for c in Query.recursive (named "Tools", fun _ -> plasm { for c in tableOf<Category> do select c }) do select c.Name })

    [<StepDefinition("I read a recursive query whose step orders its rows")>]
    member _.OrderedStep() =
        read (plasm { for c in Refused.orderedStep do select c.Name })

    [<StepDefinition("I read a recursive query whose start takes one row")>]
    member _.PagedStart() =
        let start = plasm { for c in tableOf<Category> do orderBy c.Name; take 1 }

        let q =
            Query.recursive (
                start,
                fun found ->
                    plasm {
                        for c in tableOf<Category> do
                        join p in found on (c.Parent = Some p.Id)
                        select c
                    }
            )

        read (plasm { for c in q do select c.Name })

    [<StepDefinition("I lock the categories under Tools in a transaction")>]
    member _.Lock() =
        task {
            let! r = repo.Transact(Tx.all (plasm { for c in under "Tools" do locking RowLock.ForUpdate }): Tx<_, unit>)

            match r with
            | Error { Failure = TxFailure.Failed e } -> keep (Error e: Result<obj, DbError>)
            | other -> failwithf "expected the read to fail, got %A" other
        }
        :> Task

    [<StepDefinition("I read the depth of the categories of a query that is not recursive")>]
    member _.DepthElsewhere() =
        read (plasm { for c in tableOf<Category> do select (Query.depth c) })

    [<StepDefinition("^it is refused, saying \"(.*)\"$")>]
    member _.Refused(saying: string) =
        let m = refusal ()
        check (m.Contains saying) m

    [<StepDefinition("a repository starts with the checks on, over an assembly whose recursive query is refused")>]
    member _.Start() =
        task {
            try
                let! _ = Repo.Start(Tree.model, w.Db.Provider, queriesIn = typeof<Tree.Marker>.Assembly)
                w.Set("recursiveStart", "started")
            with :? PlasmStartupException as e ->
                w.Set("recursiveStart", e.Message)
        }
        :> Task

    [<StepDefinition("it does not start, naming that query")>]
    member _.DidNotStart() =
        let m = w.Get<string> "recursiveStart"
        check (m.Contains "Refused.orderedStep" && m.Contains "not its step") m
