/// Lesson 05: About stored records.
module Plasm.Koans.Lessons.L05_StoredRecords

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// A record with two fields Plasm cannot store: a Uri has no column type, and
/// it is not a wrapper or a union Plasm can store by convention.
[<Table "visits">]
type Visit =
    { Id: int64
      Page: System.Uri
      Referrer: System.Uri }

/// A wish: a plain record that names no table, so it is not stored.
type Wish = { Item: string }

/// A wishlist holding wishes. A list of records is stored only if the records are.
[<Table "wishlists">]
type Wishlist = { Id: int64; Wishes: Wish list }

/// A depot's place: a record that names no table and has no Id. As one field
/// of a stored record it is a value, kept in its owner's row.
type Place = { Street: string; City: string }

[<Table "depots">]
type Depot = { Id: int64; Name: string; Place: Place }

/// A chore's steps: kept as one JSON document in the chore's row.
type Step = { Text: string; Done: bool }

[<Table "chores">]
type Chore =
    { Id: int64
      Title: string
      [<Json>]
      Steps: Step list }

let lesson =
    Lesson.make
        "About stored records"
        [ Koan.Sync(
              "A stored record is still plain F#",
              fun _ ->
                  // Product is an ordinary immutable F# record. In Shop.fs it carries
                  // [<Table "products">], a note saying where it is stored; it has no
                  // base class and nothing to call. Copy-and-update works as always.
                  let widget = { Id = ProductId 1L; Sku = "WIDGET"; Name = "Widget" }
                  let renamed = { widget with Name = "Better widget" }
                  widget.Name |> shouldBe __
                  renamed.Name |> shouldBe __
          )

          Koan.Sync(
              "A record names its table",
              fun repo ->
                  // [<Table "products">] on Product names the table it is stored in.
                  // tableOf<Product> is every stored Product, as a query source, and
                  // Query.from makes a query for all of them.
                  // repo.Explain shows the SQL a query would send, without sending it.
                  let sql = (ok (repo.Explain(Query.from (tableOf<Product>)))).Sql
                  // Which table does the query read from?
                  sql.Contains "FROM \"products\"" |> shouldBe __
          )

          Koan.Sync(
              "Columns are named in snake_case",
              fun repo ->
                  // A column is named after its field, in snake_case: lower case words
                  // joined by underscores. Nothing on Order says so; it is the rule.
                  let sql = (ok (repo.Explain(Query.from (tableOf<Order>)))).Sql
                  // Order has a field PlacedAt. Which name does the SQL use?
                  sql.Contains "\"placed_at\"" |> shouldBe __
                  sql.Contains "\"PlacedAt\"" |> shouldBe __
          )

          Koan.Task(
              "A key finds one record",
              fun repo ->
                  task {
                      // A stored record's key is its field named Id. A key that wraps a
                      // number, such as ProductId, is numbered by the database: 1, 2, 3, in
                      // the order the rows were inserted.
                      // repo runs queries against the database; One reads one record, if any.
                      let! product = repo.One(plasm { for p in tableOf<Product> do where (p.Id = ProductId 2L) })
                      // ok takes the value out of an Ok result; .Value takes it out of Some.
                      (ok product).Value.Name |> shouldBe __
                  }
          )

          Koan.Task(
              "A union is stored as text",
              fun repo ->
                  task {
                      // OrderStatus is a union with no fields. Plasm stores such a union
                      // by convention, each case as its name in snake_case: Draft as
                      // 'draft', Published as 'published', and so on. Nothing registers it.
                      // Here the status column of Order 2 is changed with SQL, as text.
                      // (Tx.executeSql is a step running SQL, in a transaction that
                      // repo.Transact runs; lesson 19 goes deeper. The change is made
                      // in this koan's sandbox and vanishes when it ends.)
                      let cancel: Tx<int, unit> = Tx.executeSql $"update orders set status = 'cancelled', cancellation_reason = 'test' where id = 2"
                      let! _ = repo.Transact cancel
                      let! order = repo.One(plasm { for o in tableOf<Order> do where (o.Id = OrderId 2L) })
                      // Is Status the text, or a case of OrderStatus?
                      (ok order).Value.Status |> shouldBe __
                  }
          )

          Koan.Task(
              "Wrapped keys keep their meaning",
              fun repo ->
                  task {
                      // CustomerId is a single-case union over int64. By convention Plasm
                      // stores it as the number it wraps, and reads it back wrapped.
                      // An Email is stored the same way: email (in Shop.fs) makes one.
                      let adaEmail = email "ada@example.com"
                      let! ada = repo.One(plasm { for c in tableOf<Customer> do where (c.Email = adaEmail) })
                      let ada = (ok ada).Value
                      ada.Id |> shouldBe __
                  }
          )

          Koan.Task(
              "Stored values can be rejected",
              fun repo ->
                  task {
                      // Quantity can only be built by Quantity.create, which refuses zero.
                      Quantity.create 0 |> Result.isOk |> shouldBe __

                      // Plasm reads a Quantity back through Quantity.create, so a zero
                      // stored in the database is refused on the way in, with its field named:
                      // DbError.Materialization, a row Plasm could not make into the record.
                      let zero: Tx<int, unit> = Tx.executeSql $"update order_lines set quantity = 0 where id = 1"
                      let! _ = repo.Transact zero
                      let! lines = repo.All(Query.from (tableOf<OrderLine>))

                      match lines with
                      | Error(DbError.Materialization m) -> m.Property |> shouldBe __
                      | other -> failwithf "expected a materialisation error, got %A" other
                  }
          )

          Koan.Task(
              "Absent values are options",
              fun repo ->
                  task {
                      // Order 4 has no customer (a NULL column) but has a cancellation reason.
                      let! order = repo.One(plasm { for o in tableOf<Order> do where (o.Id = OrderId 4L) })
                      let order = (ok order).Value
                      order.CustomerId |> shouldBe __
                      order.CancellationReason |> shouldBe __
                  }
          )

          Koan.Sync(
              "A model reports every mistake at once",
              fun _ ->
                  // A Visit (above, under Given) has two fields Plasm cannot store.
                  // Model.build makes a model from the record types it lists, and
                  // reports every problem, not just the first, each with its Property
                  // as in lesson 04.
                  match Model.build [] [ typeof<Visit> ] with
                  | Ok _ -> failwith "expected configuration errors"
                  | Error errors ->
                      errors.Length |> shouldBe __
                      errors |> List.map (fun e -> e.Property) |> shouldBe __
          )

          Koan.Sync(
              "A list of records is stored only if the records are",
              fun _ ->
                  // A Wishlist (under Given) holds a list of Wish, a record that names
                  // no table. Plasm does not guess where the wishes would go: building
                  // the model refuses it, naming the field.
                  match Model.build [] [ typeof<Wishlist> ] with
                  | Ok _ -> failwith "expected a configuration error"
                  | Error errors -> errors |> List.map (fun e -> e.Property) |> shouldBe __
          )

          Koan.Task(
              "One record without a table lives in its owner's row",
              fun repo ->
                  task {
                      // Place (under Given) names no table, but a Depot holds one Place,
                      // not a list: it is stored in the depots row, each of its fields in
                      // a column named <field>_<subfield>. repo.Ddl prints the tables a
                      // model expects, without touching the database; a repo started
                      // only to print them has nothing to check (RepoOptions.Unchecked).
                      let! depots = Repo.Start(Model.buildOrFail [] [ typeof<Depot> ], repo.Provider, queriesIn = typeof<Depot>.Assembly, options = RepoOptions.Unchecked)
                      depots.Ddl().Contains "place_city text not null" |> shouldBe __
                  }
          )

          Koan.Task(
              "A field marked Json is one document",
              fun repo ->
                  task {
                      // Chore (under Given) marks its Steps [<Json>]: the whole list is kept
                      // as one jsonb document in the chores row, each step an object keyed
                      // in snake case, {"text": ..., "done": ...}. Queries can still reach
                      // into it: List.exists (fun s -> not s.Done) c.Steps runs in SQL.
                      let! chores = Repo.Start(Model.buildOrFail [] [ typeof<Chore> ], repo.Provider, queriesIn = typeof<Chore>.Assembly, options = RepoOptions.Unchecked)
                      chores.Ddl().Contains "steps jsonb not null" |> shouldBe __
                  }
          ) ]
