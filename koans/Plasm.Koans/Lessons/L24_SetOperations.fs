/// Lesson 24: About set operations.
module Plasm.Koans.Lessons.L24_SetOperations

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private named (names: string list) =
    plasm { for c in tableOf<Customer> do where (List.contains c.Name names) }

/// The customers with at least one order.
let private withAnOrder =
    plasm {
        for c in tableOf<Customer> do
            where (exists (plasm { for o in tableOf<Order> do where (o.CustomerId = Some c.Id) }))
    }

let private names (q: Query<Customer>) (repo: Repo) =
    task {
        let! rows = repo.All(plasm { for c in q do orderBy c.Name })
        return ok rows |> List.map (fun c -> c.Name)
    }

let lesson =
    Lesson.make
        "About set operations"
        [ Koan.Task(
              "A union gives each row once",
              fun repo ->
                  task {
                      // Query.union (a, b) is a query of the rows either finds, each once:
                      // one command, UNION in SQL. Both must be queries of the same rows.
                      let! both = names (Query.union (named [ "Bob"; "Cy" ], withAnOrder)) repo
                      both |> shouldBe __
                  }
          )

          Koan.Task(
              "A union of all keeps every copy",
              fun repo ->
                  task {
                      // Query.unionAll keeps a row once for each query that found it.
                      let! all = names (Query.unionAll (named [ "Bob"; "Cy" ], withAnOrder)) repo
                      all |> shouldBe __
                  }
          )

          Koan.Task(
              "An intersection and a difference",
              fun repo ->
                  task {
                      // Query.intersect keeps the rows both find; Query.except, the rows the
                      // first finds and the second does not.
                      let! inBoth = names (Query.intersect (named [ "Bob"; "Cy" ], withAnOrder)) repo
                      inBoth |> shouldBe __
                      let! onlyFirst = names (Query.except (named [ "Bob"; "Cy" ], withAnOrder)) repo
                      onlyFirst |> shouldBe __
                  }
          )

          Koan.Task(
              "The combined rows are ordered and paged",
              fun repo ->
                  task {
                      // A query made from a union is a query like any other: its filter,
                      // order and page apply to the combined rows, after the union is formed.
                      let first =
                          plasm {
                              for c in Query.union (named [ "Bob"; "Cy" ], withAnOrder) do
                                  orderBy c.Name
                                  take 1
                          }

                      let! rows = repo.All first
                      ok rows |> List.map (fun c -> c.Name) |> shouldBe __
                      let sql = sqlSent () |> List.last
                      (sql.IndexOf "LIMIT" > sql.LastIndexOf ") AS") |> shouldBe __
                  }
          )

          Koan.Task(
              "An operand cannot order itself",
              fun repo ->
                  task {
                      // Order and page the combined rows, not an operand: an operand that
                      // orders or pages itself is refused when the query is translated,
                      // as a DbError.Translation, saying so.
                      let ordered = plasm { for c in tableOf<Customer> do orderBy c.Name }
                      let! r = repo.All(Query.union (ordered, withAnOrder))

                      let refused =
                          match r with
                          | Error(DbError.Translation t) -> (string t).Contains "order and page the combined rows, not an operand"
                          | _ -> false

                      refused |> shouldBe __
                  }
          )

          Koan.Task(
              "Two projections combine into their rows",
              fun repo ->
                  task {
                      // Two projections of one type combine too. Their rows come in no
                      // order, so this sorts them.
                      let ordering = plasm { for c in withAnOrder do select c.Name }
                      let ada = plasm { for c in named [ "Ada" ] do select c.Name }
                      let! rows = repo.All(Query.union (ordering, ada))
                      ok rows |> List.sort |> shouldBe __
                  }
          )

          Koan.Task(
              "A join takes a query",
              fun repo ->
                  task {
                      // join takes a query where it takes a table: its rows, as a subquery
                      // named by the join's variable.
                      let adas =
                          plasm {
                              for o in tableOf<Order> do
                                  join c in named [ "Ada" ] on (o.CustomerId = Some c.Id)
                                  orderBy o.Id
                                  select o.Id
                          }

                      let! ids = repo.All adas
                      ok ids |> shouldBe __
                  }
          ) ]
