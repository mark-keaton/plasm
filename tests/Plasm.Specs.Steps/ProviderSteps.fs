namespace Plasm.Specs

open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Database-specific SQL comes from the provider
[<Binding>]
type ProviderSteps(w: World) =

    let repo () = w.Get<Repo> "testRepo"

    let explain (q: Query<'T>) = (repo().Explain q |> ok)

    let storedOrder = { New.order None Published 10m with Id = OrderId 1L; Version = 1 }

    [<StepDefinition("a test dialect without array parameters, upserts, RETURNING or row locking, with its own null-safe comparison and string length")>]
    member _.TestDialect() =
        w.Set("testRepo", Repo(Schemas.model, TestDialectProvider(w.Db.Provider)))

    [<StepDefinition("^a repository is made with the test dialect for a model storing (a status as an enum type|lists of scalars)$")>]
    member _.MakeRefused(what: string) =
        let model = if what = "lists of scalars" then Plasm.Specs.ArrayColumns.Schemas.model else PgEnums.model

        try
            Repo(model, TestDialectProvider(w.Db.Provider)) |> ignore
            w.Set("madeWith", "made")
        with :? PlasmConfigurationException as e ->
            w.Set("madeWith", e.Message)

    /// On the scenario's own provider (SQLite: EnumTypes false).
    [<StepDefinition("^a repository is made for a model storing a status as an enum type$")>]
    member _.MakeOnProvider() =
        try
            Repo(PgEnums.model, w.Db.Provider) |> ignore
            w.Set("madeWith", "made")
        with :? PlasmConfigurationException as e ->
            w.Set("madeWith", e.Message)

    [<StepDefinition("^it is refused when made, naming (.*) and saying it has no (.*)$")>]
    member _.RefusedWhenMade(fields: string, capability: string) =
        let m = w.Get<string> "madeWith"
        check (m.Contains fields && m.Contains $"has no {capability}") m

    [<StepDefinition("^an Article \"(.*)\" and the Tags \"(.*)\" and \"(.*)\"$")>]
    member _.ArticleAndTags(title: string, a: string, b: string) =
        w.Db.Exec("insert into articles (title) values (@t); insert into tags (name) values (@a), (@b)", [ "t", box title; "a", box a; "b", box b ]) |> unit

    [<StepDefinition("^the Article's Tags are made \"(.*)\" and \"(.*)\" through a provider like the database's without array parameters$")>]
    member _.Relink(a: string, b: string) =
        task {
            let noArrays = Repo(Schemas.model, NoArrayParameters(w.Db.Provider))
            let! article = noArrays.One(plasm { for x in tableOf<Article> do where (x.Title = "Intro") })
            let article = (ok article).Value
            let! tags = noArrays.All(plasm { for t in tableOf<Tag> do where (t.Name = a || t.Name = b); orderBy t.Name })
            let! r = noArrays.Transact(Tx.update (article, { article with Tags = ok tags }): Tx<Article, unit>)
            w.Set("relinked", r |> Result.map ignore |> Result.mapError string)
        }
        |> unit

    [<StepDefinition("^the Article is stored linked to \"(.*)\" and \"(.*)\"$")>]
    member _.Linked(a: string, b: string) =
        task {
            equal (Ok()) (w.Get<Result<unit, string>> "relinked") "the write"
            let! names = w.Db.Scalar<string>("select string_agg(t.name, ',' order by t.name) from article_tags at join tags t on t.id = at.tag_id")
            equal $"{a},{b}" names "the Article's Tags"
        }
        |> unit

    [<StepDefinition("I explain, with a dialect that sends lists as JSON, a query of the tickets, which own their notes under a key of two parts")>]
    member _.CompositeLoad() =
        let repo = Repo(Plasm.Specs.CompositeReferences.Schemas.model, JsonLists(w.Db.Provider))
        let plan = repo.Explain(Query.from (tableOf<Plasm.Specs.CompositeReferences.Ticket>)) |> ok
        w.Set("sql", (plan.Loads |> List.head).Sql)

    [<StepDefinition("the notes' load matches both parts against the dialect's lists, one parameter a part")>]
    member _.CompositeLoadSql() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "IN (SELECT l0.value, l1.value FROM json_each(@p0) AS l0 JOIN json_each(@p1) AS l1 ON l1.key = l0.key)" && not (sql.Contains "unnest")) sql

    [<StepDefinition("I explain, with the test dialect, a prepared query of the Customers whose id is in a list argument")>]
    member _.PreparedList() =
        let byIds = Query.prepare (fun (ids: Arg<CustomerId list>) -> plasm { for c in tableOf<Customer> do where (List.contains c.Id ids.Value) })
        let one = (explain (byIds [ CustomerId 1L ])).Sql
        let three = (explain (byIds [ CustomerId 1L; CustomerId 2L; CustomerId 3L ])).Sql
        w.Set("explainedLists", (one, three))

    [<StepDefinition("the membership is the dialect's, of one parameter, whatever the list's length")>]
    member _.ListMembership() =
        let one, three = w.Get<string * string> "explainedLists"
        check (one = three && one.Contains "\"c\".\"id\" IN (SELECT value FROM json_each(@p0))" && not (one.Contains "@p1")) one

    [<StepDefinition("I explain, with the test dialect, whether any of each customer's Orders is published")>]
    member _.AnyPublished() =
        w.Set("explained", (repo().Explain(plasm { for o in tableOf<Order> do groupBy o.CustomerId; select (o.CustomerId, Agg.any (o.Status = Published)) }) |> ok).Sql)

    [<StepDefinition("the aggregate is the dialect's name for it")>]
    member _.AggregateName() =
        let sql = w.Get<string> "explained"
        check (sql.Contains "MAX(" && not (sql.Contains "BOOL_OR")) sql

    [<StepDefinition("I explain, with the test dialect, each customer's Order totals in the order they were placed")>]
    member _.TotalsListed() =
        w.Set("explained", (repo().Explain(plasm { for o in tableOf<Order> do groupBy o.CustomerId; select (o.CustomerId, Agg.listBy o.Total o.PlacedAt) }) |> ok).Sql)

    [<StepDefinition("a customer with no Orders has the dialect's empty array")>]
    member _.EmptyArray() =
        let sql = w.Get<string> "explained"
        check (sql.Contains ", '[]')" && not (sql.Contains "'{}'")) sql

    [<StepDefinition("I explain, with the test dialect, the least status of the Orders")>]
    member _.LeastStatus() =
        w.Set("explained", (repo().Explain(plasm { for o in tableOf<Order> do select (Agg.min o.Status) }) |> ok).Sql)

    [<StepDefinition("the name is read back from its position by a CASE, with no array")>]
    member _.NameByCase() =
        let sql = w.Get<string> "explained"
        check (not (sql.Contains "ARRAY[") && sql.Contains "THEN 'draft'") sql

    [<StepDefinition("I explain a query of the shipments whose status is Shipped, with a dialect that stores an enum as text")>]
    member _.EnumAsText() =
        let repo = Repo(PgEnums.model, StoredAsDialectSays(w.Db.Provider, [ "json" ]))
        w.Set("explained", (repo.Explain(plasm { for s in tableOf<PgEnums.Shipment> do where (s.Status = PgEnums.Shipped) }) |> ok).Sql)

    [<StepDefinition("the status is compared with its parameter as it is, cast to no type")>]
    member _.NoCast() =
        let sql = w.Get<string> "explained"
        check (sql.Contains "\"status\" = @p0" && not (sql.Contains "CAST(")) sql

    [<StepDefinition("I print the DDL of the documented cards model, with a dialect whose JSON documents are json")>]
    member _.JsonDdl() =
        w.Set("testDdl", Repo(Plasm.Specs.JsonColumns.Schemas.model, StoredAsDialectSays(w.Db.Provider, [ "json" ])).Ddl())

    [<StepDefinition("the cards' checklist is declared json")>]
    member _.ChecklistJson() =
        let ddl = w.Get<string> "testDdl"
        let line = ddl.Split('\n') |> Array.find (fun l -> l.TrimStart().StartsWith "checklist ")
        check (line.Trim().StartsWith "checklist json not null") line

    [<StepDefinition("I explain a query of the Customers by name that skips one, with the test dialect, which needs a LIMIT before an OFFSET")>]
    member _.SkipOnly() =
        w.Set("explained", (explain (plasm { for c in tableOf<Customer> do orderBy c.Name; skip 1 })).Sql)

    [<StepDefinition("the query takes the dialect's unbounded limit before its offset")>]
    member _.UnboundedLimit() =
        let sql = w.Get<string> "explained"
        check (sql.Contains "LIMIT -1 OFFSET @p") sql

    [<StepDefinition("I print the DDL with the test dialect, which declares a generated key its own way and its foreign keys in their tables")>]
    member _.DialectDdl() = w.Set("testDdl", repo().Ddl())

    [<StepDefinition("an Order's key is declared the dialect's way")>]
    member _.KeyDeclared() =
        let ddl = w.Get<string> "testDdl"
        let orders = ddl.Substring(ddl.IndexOf "create table orders")
        let id = orders.Split('\n') |> Array.find (fun l -> l.TrimStart().StartsWith "id ")
        check (id.Trim() = "id bigint not null primary key autoincrement,") id

    [<StepDefinition("the orders table declares its reference to the customers inside it, with no alter table")>]
    member _.ForeignKeyInside() =
        let ddl = w.Get<string> "testDdl"
        let orders = ddl.Substring(ddl.IndexOf "create table orders")
        let table = orders.Substring(0, orders.IndexOf ");")
        check (table.Contains "constraint orders_customer_id_fkey foreign key (customer_id) references customers (id)" && not (ddl.Contains "alter table")) ddl

    [<StepDefinition("the schema checks the model's tables through a provider like the database's whose dialect writes its own catalogue queries")>]
    member _.MarkedCatalogueCheck() =
        task {
            let sent = ResizeArray<CommandEvent>()
            let marked = Repo(w.Db.Model, MarkedCatalogue(w.Db.Provider), { RepoOptions.Default with OnCommand = Some sent.Add })
            let! issues = marked.CheckSchema()
            w.Set("catalogueIssues", ok issues |> List.map string)
            w.Set("catalogueSent", sent |> Seq.map (fun e -> e.Sql) |> List.ofSeq)
        }
        |> unit

    [<StepDefinition("the columns were read by the dialect's query, and no difference is found")>]
    member _.MarkedCatalogueRead() =
        let sent = w.Get<string list> "catalogueSent"
        check (sent |> List.exists (fun s -> s.StartsWith "/* the dialect's columns */")) (String.concat "\n---\n" sent)
        equal [] (w.Get<string list> "catalogueIssues") "the differences found"

    [<StepDefinition("I print the DDL with the test dialect, which accepts \"character varying\" before \"text\" for a string")>]
    member _.TestDdl() = w.Set("testDdl", repo().Ddl())

    [<StepDefinition("a Customer's name is declared \"text\"")>]
    member _.NameDeclared() =
        let ddl = w.Get<string> "testDdl"
        let customers = ddl.Substring(ddl.IndexOf "create table customers")
        let name = customers.Split('\n') |> Array.find (fun l -> l.TrimStart().StartsWith "name ")
        check (name.Trim().StartsWith "name text") name

    [<StepDefinition("I explain a query of the Issues labelled \"bug\" or with no assignee, with a dialect whose tests of a list column are its own")>]
    member _.ExplainLists() =
        let repo = Repo(Plasm.Specs.ArrayColumns.Schemas.model, MarkedListsProvider(w.Db.Provider))

        let plan =
            repo.Explain(plasm { for i in tableOf<Plasm.Specs.ArrayColumns.Issue> do where (List.contains "bug" i.Labels || List.isEmpty i.Assignees) })
            |> ok

        w.Set("sql", plan.Sql)

    [<StepDefinition("the labels are tested by the dialect's membership, and the assignees by its emptiness")>]
    member _.ListsTested() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "= ANY(\"i\".\"labels\") /* the dialect's */" && sql.Contains "cardinality(\"i\".\"assignees\") = 0 /* the dialect's */") sql

    [<StepDefinition("a dialect that is PostgreSQL's, its null-safe comparison spelled its own way")>]
    member _.MarkedDialect() =
        task {
            let! _ = Seed.customer w.Db "Ada" "ada@example.com"
            let events = ResizeArray<CommandEvent>()
            w.Set("markedEvents", events)
            w.Set("markedRepo", Repo(Schemas.model, MarkedComparisonProvider(w.Db.Provider), { RepoOptions.Default with OnCommand = Some events.Add }))
        }
        :> Task

    [<StepDefinition("a Customer read through it is renamed")>]
    member _.RenameMarked() =
        task {
            let repo = w.Get<Repo> "markedRepo"
            let! read = repo.One(plasm { for c in tableOf<Customer> do where (c.Name = "Ada") })
            let ada = (ok read).Value
            (w.Get<ResizeArray<CommandEvent>> "markedEvents").Clear()
            let! renamed = repo.Transact(Tx.update (ada, { ada with Name = "Ada L." }): Tx<Customer, unit>)
            equal "Ada L." (ok renamed).Name "the renamed Customer"
        }
        :> Task

    [<StepDefinition("the UPDATE compares the columns it was read with by the dialect's null-safe operator alone")>]
    member _.MarkedUpdate() =
        let update = w.Get<ResizeArray<CommandEvent>> "markedEvents" |> Seq.map (fun e -> e.Sql) |> Seq.find (fun s -> s.StartsWith "UPDATE")
        let where = update.Substring(update.IndexOf "WHERE")
        let count (s: string) = (where.Length - where.Replace(s, "").Length) / s.Length
        let marked = count "IS NOT DISTINCT FROM /* the dialect's */"
        check (marked > 0 && marked = count "IS NOT DISTINCT FROM") update

    [<StepDefinition("I explain a query for Orders, which own their Lines, with the test dialect")>]
    member _.ExplainLoads() =
        let plan = explain (Query.from (tableOf<Order>))
        w.Set("sql", (plan.Loads |> List.head).Sql)

    [<StepDefinition("the load command matches keys with an IN list of parameters")>]
    member _.InList() =
        let sql = w.Get<string> "sql"
        check (sql.Contains " IN (" && not (sql.Contains "ANY(")) $"expected an IN list: {sql}"

    [<StepDefinition("I explain a query comparing an optional field with <> with the test dialect")>]
    member _.ExplainNullSafe() =
        let reason = Some "late"
        w.Set("sql", (explain (plasm { for o in tableOf<Order> do where (o.CancellationReason <> reason) })).Sql)

    [<StepDefinition("the comparison uses the dialect's null-safe operator")>]
    member _.NullSafe() =
        let sql = w.Get<string> "sql"
        check (sql.Contains " IS NOT @p0" && not (sql.Contains "DISTINCT")) $"expected the dialect's operator: {sql}"

    [<StepDefinition("I explain a query filtering on the length of a name with the test dialect")>]
    member _.ExplainLength() =
        w.Set("sql", (explain (plasm { for c in tableOf<Customer> do where (c.Name.Length > 3) })).Sql)

    [<StepDefinition("the length uses the dialect's function name")>]
    member _.Length() =
        let sql = w.Get<string> "sql"
        check (sql.Contains "length(" && not (sql.Contains "char_length")) $"expected length(): {sql}"

    [<StepDefinition("I attempt (.*) with the test dialect")>]
    member _.Attempt(operation: string) =
        task {
            w.MarkCommands()
            let r = repo ()

            let! outcome =
                task {
                    match operation with
                    | "an upsert" ->
                        let! x = r.Transact(Tx.execute (Bulk.upsert ([ New.product "SKU" "Widget" ], (fun p -> p.Sku), (fun _ incoming -> incoming))): Tx<_, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported upsert, got %A" other
                    | "an insert" ->
                        let! x = r.Transact(Tx.insert (New.customer "Ada" "ada@example.com"): Tx<_, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported insert, got %A" other
                    | "deleting an Order and its Lines" ->
                        // Deleting an owner deletes every line it has, read FOR UPDATE.
                        let! x = r.Transact(Tx.delete storedOrder: Tx<Order, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected an unsupported delete, got %A" other
                    | "changing an Article's links" ->
                        let article = { Id = ArticleId 1L; Title = "Intro"; Tags = [] }

                        let! x = r.Transact(Tx.update (article, { article with Tags = [ { Id = TagId 1L; Name = "fsharp" } ] }): Tx<Article, unit>)

                        return
                            match x with
                            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> Some op
                            | other -> failwithf "expected unsupported links, got %A" other
                    | op -> return failwithf "unknown operation %s" op
                }

            w.Set("refused", outcome.Value)
        }
        |> unit

    /// On the scenario's own provider: a transaction that would write.
    [<StepDefinition("^I run a transaction at (\\w+) isolation$")>]
    member _.AtIsolation(level: string) =
        task {
            let isolation = System.Enum.Parse<System.Data.IsolationLevel> level
            w.MarkCommands()
            let tx: Tx<unit, unit> = Tx.insert (New.customer "Ada" "ada@example.com") |> Tx.map ignore
            let! x = w.Repo.Transact(tx, isolation)

            match x with
            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> w.Set("refused", op)
            | other -> failwithf "expected the isolation level refused, got %A" other
        }
        |> unit

    [<StepDefinition("it fails before execution as unsupported by the provider, naming (.*)")>]
    member _.Refused(capability: string) =
        let op = w.Get<string> "refused"
        check (op.Contains capability) $"expected the refusal to name {capability}: {op}"
        equal [] (w.CommandsSinceMark |> List.map (fun c -> c.Sql)) "commands sent"

    [<StepDefinition("^I attempt an operation the test dialect cannot perform through (an insert step|a bulk write step)$")>]
    member _.AttemptThrough(call: string) =
        task {
            let r = repo ()

            let! x =
                match call with
                | "an insert step" -> r.Transact(Tx.insert (New.customer "Ada" "ada@example.com") |> Tx.map ignore: Tx<unit, unit>)
                | _ -> r.Transact(Tx.execute (Bulk.upsert ([ New.product "SKU" "Widget" ], (fun p -> p.Sku), (fun _ incoming -> incoming))) |> Tx.map ignore: Tx<unit, unit>)

            match x with
            | Error { Failure = TxFailure.Failed(DbError.Translation(TranslationError.UnsupportedByProvider(op, _))) } -> w.Set("refused", op)
            | other -> failwithf "got %A" other
        }
        |> unit

    [<StepDefinition("the failure contains TranslationError.UnsupportedByProvider naming the missing capability")>]
    member _.NamesCapability() =
        let op = w.Get<string> "refused"
        check (op.Contains "RETURNING" || op.Contains "upserts") op
