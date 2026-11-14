/// Lesson 16: About many-to-many and has-one.
module Plasm.Koans.Lessons.L16_ManyToManyAndHasOne

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private names (tags: Assoc<Tag list>) = tags |> Assoc.get |> List.map (fun t -> t.Name) |> List.sort

let private article (k: KoanContext) (id: int64) =
    task {
        let! a = k.Repo.One(plasm { for a in Db.articles do where (a.Id = ArticleId id); preload a.Tags })
        return (ok a).Value
    }

let private tag id name = { Id = TagId id; Name = name }

let lesson =
    Lesson.make
        "About many-to-many and has-one"
        [ Koan.Task(
              "Tags load through the join table",
              fun k ->
                  task {
                      let! articles = k.Repo.All(plasm { for a in Db.articles do orderBy a.Id; preload a.Tags })
                      (ok articles).Head.Tags |> names |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "No tags is an empty, loaded list",
              fun k ->
                  task {
                      let! empty = article k 3L
                      empty.Tags |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering through links",
              fun k ->
                  task {
                      let fsharp = plasm { for a in Db.articles do where (Rel.any (fun (t: Tag) -> t.Name = "fsharp") a.Tags); orderBy a.Id }
                      let! rows = k.Repo.All fsharp
                      ok rows |> List.map (fun a -> a.Title) |> shouldBe __
                  }
          )

          Koan.Task(
              "Replacing links",
              fun k ->
                  task {
                      let! intro = article k 1L
                      let! updated = k.Repo.Update(Changeset.update intro |> Changeset.putLinks ((fun a -> a.Tags), [ tag 4L "ops" ]))
                      (ok updated).Tags |> names |> shouldBe __
                      let! tags = k.Repo.Count(Query.from Db.tags)
                      ok tags |> shouldBe __
                  }
          )

          Koan.Task(
              "Adding and removing one link",
              fun k ->
                  task {
                      let! intro = article k 1L

                      let change =
                          Changeset.update intro
                          |> Changeset.addLinks ((fun a -> a.Tags), [ tag 3L "draft" ])
                          |> Changeset.removeLinks ((fun a -> a.Tags), [ tag 2L "sql" ])

                      let! updated = k.Repo.Update change
                      (ok updated).Tags |> shouldBe __
                      let! reloaded = article k 1L
                      reloaded.Tags |> names |> shouldBe __
                  }
          )

          Koan.Task(
              "At most one related record",
              fun k ->
                  task {
                      let! accounts = k.Repo.All(plasm { for a in Db.accounts do orderBy a.Id; preload a.Profile })
                      ok accounts |> List.map (fun a -> a.Profile |> Assoc.get |> Option.map (fun p -> p.Bio)) |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering through has-one",
              fun k ->
                  task {
                      let writers = plasm { for a in Db.accounts do where (Rel.exists (fun p -> p.Bio.Contains "compiler") a.Profile) }
                      let! rows = k.Repo.All writers
                      ok rows |> List.map (fun a -> a.Name) |> shouldBe __
                  }
          ) ]
