/// Lesson 28: About recursive queries.
module Plasm.Koans.Lessons.L28_RecursiveQueries

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private named (name: string) =
    plasm { for c in tableOf<Category> do where (c.Name = name) }

/// Everything under the category `name`: it, its children, theirs, ...
let private under (name: string) =
    Query.recursive (
        named name,
        fun found ->
            plasm {
                for c in tableOf<Category> do
                    join p in found on (c.Parent = Some p.Id)
                    select c
            }
    )

let private withDepth (q: Query<Category>) (repo: Repo) =
    task {
        let! rows = repo.All(plasm { for c in q do orderBy (Query.depth c); thenBy c.Name; select (c.Name, Query.depth c) })
        return ok rows
    }

let lesson =
    Lesson.make
        "About recursive queries"
        [ Koan.Task(
              "Everything under a category",
              fun repo ->
                  task {
                      // Query.recursive (start, step) is a query of the rows start finds and,
                      // again and again, the rows step reaches from the rows found so far:
                      // step is given them as a query, found, and joins it. under (above,
                      // under Given) starts at a category and steps to its children.
                      // Query.depth c is how many steps found c: 0 for the start's rows.
                      let! rows = withDepth (under "Tools") repo
                      rows |> shouldBe __
                      // One statement: WITH RECURSIVE ... UNION ALL.
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Everything above a category",
              fun repo ->
                  task {
                      // Joining found from the other side steps to the parent instead.
                      let above =
                          Query.recursive (
                              named "Hand saws",
                              fun found ->
                                  plasm {
                                      for p in tableOf<Category> do
                                          join c in found on (Some p.Id = c.Parent)
                                          select p
                                  }
                          )

                      let! rows = withDepth above repo
                      rows |> shouldBe __
                  }
          )

          Koan.Task(
              "The query made from it is a query",
              fun repo ->
                  task {
                      // A recursive query is read as a source, like a set operation: the
                      // query made from it filters, orders and pages the whole, never a step.
                      let deeper = plasm { for c in under "Tools" do where (Query.depth c > 0); orderBy c.Name; select c.Name }
                      let! names = repo.All deeper
                      ok names |> shouldBe __
                  }
          )

          Koan.Task(
              "A cycle in the data ends",
              fun repo ->
                  task {
                      // Tools is given Hand saws as its parent: a cycle. A row already on the
                      // path that reached it is not followed again (CYCLE on the key), so the
                      // statement ends.
                      let! _ = repo.Transact(Tx.executeSql $"update categories set parent = 3 where name = 'Tools'": Tx<int, unit>)
                      let! rows = withDepth (under "Tools") repo
                      rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A step must join the rows found",
              fun repo ->
                  task {
                      // The step joins found once and selects the record it reaches. A step
                      // that does not join found is refused when the query is translated,
                      // a DbError.Translation saying "the step of a recursive query joins found".
                      let everything = Query.recursive (named "Tools", fun _ -> plasm { for c in tableOf<Category> do select c })
                      let! r = repo.All everything

                      let refused =
                          match r with
                          | Error(DbError.Translation t) -> (string t).Contains "the step of a recursive query joins found"
                          | _ -> false

                      refused |> shouldBe __
                  }
          ) ]
