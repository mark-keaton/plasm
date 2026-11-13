namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Many-to-many relationships
/// Data: "Intro" is tagged fsharp and sql, "Deep" fsharp and draft, "Empty" has no tags; "ops" is unused.
[<Binding>]
type ManyToManySteps(w: World) =

    let insertId (sql: string) (args: (string * obj) list) = w.Db.Scalar<int64>(sql, args)

    let tag name : Tag = w.Get<Map<string, Tag>> "tags" |> Map.find name
    let article title : Article = w.Get<Map<string, Article>> "articles" |> Map.find title

    let withTags =
        plasm {
            for a in Schemas.articles do
                orderBy a.Id
                preload a.Tags
        }

    let tagNames (a: Article) =
        match a.Tags with
        | Loaded ts -> ts |> List.map (fun t -> t.Name) |> List.sort |> Some
        | NotLoaded -> None

    let linksOf (a: Article) =
        task {
            let (ArticleId id) = a.Id
            let! names =
                w.Repo.Sql<string>($"SELECT t.name FROM article_tags x JOIN tags t ON t.id = x.tag_id WHERE x.article_id = {id} ORDER BY t.name")

            return ok names
        }

    let loaded () =
        task {
            let! r = w.Repo.All withTags
            return ok r |> List.map (fun a -> a.Title, a) |> Map.ofList
        }

    [<StepDefinition("mappings for Articles and Tags linked through a join table")>]
    member _.Mappings() =
        check (w.Db.Model.Types |> List.contains typeof<Article>) "Article is mapped"

    [<StepDefinition("Articles linked to Tags, with one Tag shared by several Articles")>]
    member _.Seed() =
        task {
            let tags = Collections.Generic.Dictionary<string, Tag>()

            for name in [ "fsharp"; "sql"; "draft"; "ops" ] do
                let! id = insertId "insert into tags(name) values (@n) returning id" [ "n", box name ]
                tags.[name] <- { Id = TagId id; Name = name }

            let articles = Collections.Generic.Dictionary<string, Article>()

            for title, linked in [ "Intro", [ "fsharp"; "sql" ]; "Deep", [ "fsharp"; "draft" ]; "Empty", [] ] do
                let! id = insertId "insert into articles(title) values (@t) returning id" [ "t", box title ]

                for name in linked do
                    let (TagId t) = tags.[name].Id
                    let! _ = w.Db.Exec("insert into article_tags(article_id, tag_id) values (@a, @t)", [ "a", box id; "t", box t ])
                    ()

                articles.[title] <-
                    { Id = ArticleId id
                      Title = title
                      Tags = NotLoaded }

            w.Set("tags", tags |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
            w.Set("articles", articles |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
        }
        |> unit

    // Rule: Many-to-many relationships load like other relationships

    [<StepDefinition("I query Articles and preload their Tags")>]
    member _.Preload() =
        task {
            w.MarkCommands()
            let! r = w.Repo.All withTags
            w.Set("loaded", ok r)
        }
        |> unit

    [<StepDefinition("each Article has exactly its linked Tags")>]
    member _.ExactTags() =
        let byTitle = w.Get<Article list> "loaded" |> List.map (fun a -> a.Title, tagNames a)
        equal [ "Intro", Some [ "fsharp"; "sql" ]; "Deep", Some [ "draft"; "fsharp" ]; "Empty", Some [] ] byTitle "tags per article"

    [<StepDefinition("the Tags are loaded with one batched command through the join table")>]
    member _.OneCommand() =
        let cmds = w.CommandsSinceMark
        equal 2 cmds.Length "commands (articles, then tags)"
        let tagsSql = cmds.[1].Sql
        check (tagsSql.Contains "FROM \"tags\" AS \"t0\" INNER JOIN \"article_tags\" AS \"j\" ON (\"j\".\"tag_id\" = \"t0\".\"id\")") tagsSql
        check (tagsSql.Contains "WHERE (\"j\".\"article_id\" = ANY(@p0))") tagsSql

    [<StepDefinition("a Tag linked to several Articles is materialised once")>]
    member _.SharedOnce() =
        let fsharpOf title =
            match (w.Get<Article list> "loaded" |> List.find (fun a -> a.Title = title)).Tags with
            | Loaded ts -> ts |> List.find (fun t -> t.Name = "fsharp")
            | NotLoaded -> failwith "not loaded"

        check (obj.ReferenceEquals(fsharpOf "Intro", fsharpOf "Deep")) "the shared Tag is one value"

    [<StepDefinition("an Article without links has Tags loaded as an empty list")>]
    member _.EmptyLoaded() =
        let empty = w.Get<Article list> "loaded" |> List.find (fun a -> a.Title = "Empty")
        equal (Loaded []) empty.Tags "tags"

    [<StepDefinition("I preload Tags filtered by name, ordered by name and limited to 1 per Article")>]
    member _.FilteredPreload() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for a in Schemas.articles do
                            orderBy a.Id

                            preload
                                a.Tags
                                (plasm {
                                    for t in Schemas.tags do
                                        where (t.Name <> "draft")
                                        orderBy t.Name
                                        limitPerParent 1
                                })
                    }
                )

            w.Set("loaded", ok r)
        }
        |> unit

    [<StepDefinition("each Article has at most 1 Tag satisfying the filter")>]
    member _.AtMostOne() =
        let byTitle = w.Get<Article list> "loaded" |> List.map (fun a -> a.Title, tagNames a)
        equal [ "Intro", Some [ "fsharp" ]; "Deep", Some [ "fsharp" ]; "Empty", Some [] ] byTitle "tags per article"

    [<StepDefinition("the filter, ordering and limit run in the database")>]
    member _.InDatabase() =
        let sql = w.CommandsSinceMark.[1].Sql
        check (sql.Contains "ROW_NUMBER() OVER (PARTITION BY \"j\".\"article_id\" ORDER BY \"t0\".\"name\" ASC") sql
        check (sql.Contains "(\"t0\".\"name\" <> @p") sql
        equal 2 w.CommandsSinceMark.[1].Rows "rows transferred"

    [<StepDefinition("I keep only Articles that have a Tag with a given name")>]
    member _.RelAny() =
        task {
            w.MarkCommands()

            let! r =
                w.Repo.All(
                    plasm {
                        for a in Schemas.articles do
                            where (Rel.any (fun (t: Tag) -> t.Name = "fsharp") a.Tags)
                            orderBy a.Id
                    }
                )

            w.Set("filtered", ok r)
        }
        |> unit

    [<StepDefinition("only those Articles are returned, each once")>]
    member _.FilteredResult() =
        equal [ "Intro"; "Deep" ] (w.Get<Article list> "filtered" |> List.map (fun a -> a.Title)) "articles"

    [<StepDefinition("the condition runs in the database through the join table")>]
    member _.ExistsThroughJoin() =
        let sql = (w.CommandsSinceMark |> List.exactlyOne).Sql
        check (sql.Contains "EXISTS (SELECT 1 FROM \"tags\" AS \"t1\" INNER JOIN \"article_tags\" AS \"t1j\" ON (\"t1j\".\"tag_id\" = \"t1\".\"id\")") sql
        check (sql.Contains "(\"t1j\".\"article_id\" = \"t0\".\"id\")") sql

    // Rule: Changing links writes only the join table

    [<StepDefinition("I replace an Article's Tags with a different set")>]
    member _.Replace() =
        task {
            w.MarkCommands()
            let cs = Changeset.update (article "Intro") |> Changeset.putLinks ((fun a -> a.Tags), [ tag "sql"; tag "ops" ])
            let! r = w.Repo.Update cs
            w.Set("written", ok r)
        }
        |> unit

    [<StepDefinition("the join table links the Article to exactly that set")>]
    member _.ExactlyThatSet() =
        task {
            let! names = linksOf (article "Intro")
            equal [ "ops"; "sql" ] names "links"
        }
        |> unit

    [<StepDefinition("the returned Article has the new Tags loaded")>]
    member _.ReturnedLoaded() =
        equal (Some [ "ops"; "sql" ]) (tagNames (w.Get "written")) "returned tags"

    [<StepDefinition("the Tags themselves are not written")>]
    member _.TagsNotWritten() =
        let writes = w.CommandsSinceMark |> List.filter (fun c -> not (c.Sql.StartsWith "SELECT"))
        check (writes |> List.forall (fun c -> c.Sql.Contains "\"article_tags\"")) $"only join-table writes: %A{writes |> List.map (fun c -> c.Sql)}"

    [<StepDefinition("other Articles keep their links")>]
    member _.OthersKeep() =
        task {
            let! names = linksOf (article "Deep")
            equal [ "draft"; "fsharp" ] names "links of Deep"
        }
        |> unit

    [<StepDefinition("I add one link and remove another")>]
    member _.AddRemove() =
        task {
            let cs =
                Changeset.update (article "Intro")
                |> Changeset.addLinks ((fun a -> a.Tags), [ tag "ops" ])
                |> Changeset.removeLinks ((fun a -> a.Tags), [ tag "sql" ])

            let! r = w.Repo.Update cs
            w.Set("written", ok r)
        }
        |> unit

    [<StepDefinition("only those links change")>]
    member _.OnlyThose() =
        task {
            let! intro = linksOf (article "Intro")
            equal [ "fsharp"; "ops" ] intro "links of Intro"
            let! deep = linksOf (article "Deep")
            equal [ "draft"; "fsharp" ] deep "links of Deep"
        }
        |> unit

    [<StepDefinition("the returned Article's Tags are not loaded, because the full set was not read")>]
    member _.NotLoadedAfterAdd() = equal NotLoaded (w.Get<Article> "written").Tags "returned tags"

    [<StepDefinition("I insert a new Article linked to existing Tags")>]
    member _.InsertLinked() =
        task {
            w.MarkCommands()

            let cs =
                Changeset.insert
                    { Id = ArticleId 0L
                      Title = "New"
                      Tags = NotLoaded }
                |> Changeset.putLinks ((fun a -> a.Tags), [ tag "sql"; tag "ops" ])

            let! r = w.Repo.Insert cs
            w.Set("written", ok r)
        }
        |> unit

    [<StepDefinition("the Article and its links are written in one transaction")>]
    member _.OneTransaction() =
        task {
            let a: Article = w.Get "written"
            let! names = linksOf a
            equal [ "ops"; "sql" ] names "links"
            let writes =
                w.CommandsSinceMark
                |> List.filter (fun c -> not (c.Sql.StartsWith "SELECT"))
                |> List.map (fun c -> c.Sql.Split(' ').[0] + " " + c.Sql.Split(' ').[2])
            equal [ "INSERT \"articles\""; "DELETE \"article_tags\""; "INSERT \"article_tags\"" ] writes "commands"
        }
        |> unit

    [<StepDefinition("the returned Article has its generated key and its Tags loaded")>]
    member _.GeneratedKey() =
        let a: Article = w.Get "written"
        check (a.Id <> ArticleId 0L) "generated key"
        equal (Some [ "ops"; "sql" ]) (tagNames a) "tags"

    [<StepDefinition("I change an Article's title and link it to a Tag that does not exist")>]
    member _.MissingTag() =
        task {
            let ghost = { Id = TagId 999_999L; Name = "ghost" }

            let cs =
                article "Intro"
                |> Changeset.edit (fun a -> { a with Title = "Changed" })
                |> Changeset.putLinks ((fun a -> a.Tags), [ tag "sql"; ghost ])

            let! r = w.Repo.Update cs
            w.Set("linkResult", r)
        }
        |> unit

    [<StepDefinition("the write fails with a classified foreign key violation")>]
    member _.FkViolation() =
        match w.Get<Result<Article, WriteError<Article>>> "linkResult" with
        | Error(WriteError.Failed f) -> equal DbFailureKind.ForeignKeyViolation f.Kind "failure kind"
        | other -> failwithf "expected a foreign key violation, got %A" other

    [<StepDefinition("neither the title nor the links were changed")>]
    member _.NothingChanged() =
        task {
            let (ArticleId id) = (article "Intro").Id
            let! title = w.Db.Scalar<string>("select title from articles where id = @i", [ "i", box id ])
            equal "Intro" title "title"
            let! names = linksOf (article "Intro")
            equal [ "fsharp"; "sql" ] names "links"
        }
        |> unit

    [<StepDefinition("I attempt to write links through a has-many relationship")>]
    member _.HasManyLinks() =
        w.MarkCommands()

        let order =
            { New.order None Draft 1m with
                Id = OrderId 1L }

        let cs = Changeset.update order |> Changeset.putLinks ((fun o -> o.Payments), [])
        w.Set("checkedLinks", Changeset.check w.Db.Model cs)

    [<StepDefinition("the changeset is rejected before execution with the relationship identified")>]
    member _.LinksRejected() =
        let cs: Changeset<Order> = w.Get "checkedLinks"
        check (not cs.IsValid) "invalid"
        let e = cs.Errors |> List.exactlyOne
        equal "Payments" e.Field "field"
        check (e.Message.Contains "not many-to-many" && e.Message.Contains "putChildren") e.Message
        equal 0 w.CommandsSinceMark.Length "commands"
