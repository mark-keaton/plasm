/// Lesson 16: About links and owned records.
module Plasm.Koans.Lessons.L16_LinksAndOwnedRecords

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private names (tags: Tag list) = tags |> List.map (fun t -> t.Name)

let private article (repo: Repo) (id: int64) =
    task {
        let! a = repo.One(plasm { for a in tableOf<Article> do where (a.Id = ArticleId id) })
        return (ok a).Value
    }

let private account (repo: Repo) (name: string) =
    task {
        let! a = repo.One(plasm { for a in tableOf<Account> do where (a.Name = name) })
        return (ok a).Value
    }

let private tag id name = { Id = TagId id; Name = name }

/// The field errors a failed transaction reports, or [] for any other outcome.
let private fieldErrors (r: Result<'T, TransactionError<unit>>) =
    match r with
    | Error e ->
        match e.Failure with
        | TxFailure.InvalidChanges(_, errors) -> errors
        | _ -> []
    | Ok _ -> []

let lesson =
    Lesson.make
        "About links and owned records"
        [ Koan.Task(
              "An article is read with its tags",
              fun repo ->
                  task {
                      // A Tag is shared: many articles can link to the same one. An article
                      // does not own its tags, only its links to them: rows of the
                      // article_tags table (tableOf<Article> says links ... "article_tags").
                      // The tags are read with the article, in the order of their key.
                      // In the starting data, article 2 ('Deep dive') links to tags 1 and 3.
                      let! deepDive = article repo 2L
                      deepDive.Tags |> names |> shouldBe __
                  }
          )

          Koan.Task(
              "No tags is an empty list",
              fun repo ->
                  task {
                      // Article 3 links to no tag.
                      let! empty = article repo 3L
                      empty.Tags |> shouldBe __
                  }
          )

          Koan.Task(
              "Every article's tags come in one more command",
              fun repo ->
                  task {
                      // One command reads the articles, and one more reads the tags of
                      // all of them, through the join table.
                      let! articles = repo.All(plasm { for a in tableOf<Article> do orderBy a.Id })
                      (ok articles).Head.Tags |> names |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering articles by their tags",
              fun repo ->
                  task {
                      // List.exists over a linked list is a filter the database runs,
                      // keeping each matching article once.
                      let fsharp =
                          plasm {
                              for a in tableOf<Article> do
                                  where (a.Tags |> List.exists (fun t -> t.Name = "fsharp"))
                                  orderBy a.Id
                          }

                      let! rows = repo.All fsharp
                      ok rows |> List.map (fun a -> a.Title) |> shouldBe __
                  }
          )

          Koan.Task(
              "Changing the tags writes only links",
              fun repo ->
                  task {
                      // Article 1 links to fsharp and sql. Give it the ops tag instead:
                      // the update rewrites its links, and writes no Tag.
                      let! intro = article repo 1L
                      let! updated = repo.Transact(Tx.update (intro, { intro with Tags = [ tag 4L "ops" ] }): Tx<Article, unit>)
                      (ok updated).Tags |> names |> shouldBe __
                      let! tags = repo.Count(Query.from (tableOf<Tag>))
                      ok tags |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding and removing one tag",
              fun repo ->
                  task {
                      // The list is the article's whole set of links: change it in F#.
                      let! intro = article repo 1L
                      let retagged = (intro.Tags |> List.filter (fun t -> t.Name <> "sql")) @ [ tag 3L "draft" ]
                      let! _ = repo.Transact(Tx.update (intro, { intro with Tags = retagged }): Tx<Article, unit>)
                      let! reloaded = article repo 1L
                      reloaded.Tags |> names |> shouldBe __
                  }
          )

          Koan.Task(
              "A link to a missing tag is the field's error",
              fun repo ->
                  task {
                      // There is no tag 99. The join table's foreign key refuses the link,
                      // and it comes back as the Tags field's error. fieldErrors is at the
                      // top of this lesson.
                      let! intro = article repo 1L
                      let! r = repo.Transact(Tx.update (intro, { intro with Tags = [ tag 99L "nope" ] }): Tx<Article, unit>)
                      fieldErrors r |> List.map (fun e -> e.Field, e.Kind) |> shouldBe __
                  }
          )

          Koan.Task(
              "A tag is changed as a tag",
              fun repo ->
                  task {
                      // A tag belongs to its own aggregate, so renaming it through an
                      // article is refused: write the Tag itself.
                      let! intro = article repo 1L
                      let renamed = intro.Tags |> List.map (fun t -> { t with Name = t.Name.ToUpperInvariant() })
                      let! r = repo.Transact(Tx.update (intro, { intro with Tags = renamed }): Tx<Article, unit>)
                      fieldErrors r |> List.map (fun e -> e.Field) |> shouldBe __
                  }
          )

          Koan.Task(
              "Deleting an article deletes its links, not its tags",
              fun repo ->
                  task {
                      // article_tags has no record of its own; hand-written SQL counts it.
                      let! intro = article repo 1L
                      let! _ = repo.Transact(Tx.delete intro: Tx<Article, unit>)
                      let! links = repo.Sql<int64>($"select count(*) from article_tags")
                      ok links |> shouldBe __
                      let! tags = repo.Count(Query.from (tableOf<Tag>))
                      ok tags |> shouldBe __
                  }
          )

          Koan.Task(
              "An account's profile is an option",
              fun repo ->
                  task {
                      // An Account owns at most one Profile: its own row in profiles. A
                      // Profile holds its account's key (AccountId), so the account owns it.
                      // It is read with the account, as an option.
                      let! ada = account repo "ada"
                      ada.Profile |> Option.map (fun p -> p.Bio) |> shouldBe __
                  }
          )

          Koan.Task(
              "An account without a profile",
              fun repo ->
                  task {
                      // In the starting data, only account 1 ('ada') has a profile.
                      let! bob = account repo "bob"
                      bob.Profile |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding a profile inserts it",
              fun repo ->
                  task {
                      // Changing None to Some inserts the profile, for this account.
                      let! bob = account repo "bob"
                      let profile = { Id = ProfileId 0L; AccountId = bob.Id; Bio = "Builds things" }
                      let! _ = repo.Transact(Tx.update (bob, { bob with Profile = Some profile }): Tx<Account, unit>)
                      let! profiles = repo.Count(Query.from (tableOf<Profile>))
                      ok profiles |> shouldBe __
                  }
          )

          Koan.Task(
              "Removing the profile deletes it",
              fun repo ->
                  task {
                      // Changing Some to None deletes the profile row.
                      let! ada = account repo "ada"
                      let! _ = repo.Transact(Tx.update (ada, { ada with Profile = None }): Tx<Account, unit>)
                      let! profiles = repo.Count(Query.from (tableOf<Profile>))
                      ok profiles |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering through an owned record",
              fun repo ->
                  task {
                      // To keep the accounts whose profile says something, ask whether a
                      // profile of this account exists that says it.
                      let writers =
                          plasm {
                              for a in tableOf<Account> do
                                  where (exists (plasm { for p in tableOf<Profile> do where (p.AccountId = a.Id && p.Bio.Contains "compiler") }))
                          }

                      let! rows = repo.All writers
                      ok rows |> List.map (fun a -> a.Name) |> shouldBe __
                  }
          ) ]
