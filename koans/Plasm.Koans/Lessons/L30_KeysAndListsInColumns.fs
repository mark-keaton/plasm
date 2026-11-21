/// Lesson 30: About keys of several columns and lists in a column.
module Plasm.Koans.Lessons.L30_KeysAndListsInColumns

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private northWidget = { Warehouse = "north"; Sku = "WIDGET" }

let private stockOf (key: StockKey) =
    plasm { for s in tableOf<Stock> do where (s.Id = key) }

let private category (name: string) =
    plasm { for c in tableOf<Category> do where (c.Name = name) }

let lesson =
    Lesson.make
        "About keys of several columns and lists in a column"
        [ Koan.Task(
              "A key of several columns",
              fun repo ->
                  task {
                      // A Stock's Id is a StockKey, a record (in Shop.fs): its key has a part
                      // for each field, and the parts are the table's own columns, warehouse
                      // and sku. = over a whole key compares every part.
                      let! s = repo.One(stockOf northWidget)
                      (ok s).Value.Quantity |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading by one part of the key",
              fun repo ->
                  task {
                      // A query reads a part as a column: s.Id.Warehouse is warehouse.
                      let north = plasm { for s in tableOf<Stock> do where (s.Id.Warehouse = "north"); orderBy s.Id.Sku; select s.Id.Sku }
                      let! skus = repo.All north
                      ok skus |> shouldBe __
                  }
          )

          Koan.Task(
              "An update finds the row by every part",
              fun repo ->
                  task {
                      // Tx.update finds the row by every part of the key and by its version.
                      // A part of the key cannot be changed.
                      let! s = repo.One(stockOf northWidget)
                      let stock = (ok s).Value
                      let! _ = repo.Transact(Tx.update (stock, { stock with Quantity = 4 }): Tx<Stock, unit>)
                      let update = sqlSent () |> List.find (fun q -> q.StartsWith "UPDATE")
                      let where = update.Substring(update.IndexOf "WHERE")
                      [ "warehouse"; "sku"; "version" ] |> List.forall (fun c -> where.Contains $"\"{c}\" = ") |> shouldBe __
                      let! after = repo.One(stockOf northWidget)
                      (ok after).Value.Quantity |> shouldBe __
                  }
          )

          Koan.Task(
              "A whole key has no order",
              fun repo ->
                  task {
                      // A key has no order of its own: ordering by a whole key is refused when
                      // the query is translated, a DbError.Translation naming Stock.Id and
                      // saying to "order by its parts".
                      let! r = repo.All(plasm { for s in tableOf<Stock> do orderBy s.Id })

                      let refused =
                          match r with
                          | Error(DbError.Translation t) -> (string t).Contains "order by its parts"
                          | _ -> false

                      refused |> shouldBe __
                  }
          )

          Koan.Task(
              "A list kept in its owner's row",
              fun repo ->
                  task {
                      // A Category's Keywords is a string list: a list of a scalar is stored
                      // as one array column (text[]), by convention, read and written whole.
                      let! c = repo.One(category "Saws")
                      (ok c).Value.Keywords |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering by a value in a list",
              fun repo ->
                  task {
                      // List.contains over an array field is = ANY (keywords) in SQL.
                      let cutting = plasm { for c in tableOf<Category> do where (List.contains "cutting" c.Keywords); orderBy c.Name; select c.Name }
                      let! names = repo.All cutting
                      ok names |> shouldBe __
                  }
          )

          Koan.Task(
              "An empty list is an empty array",
              fun repo ->
                  task {
                      // A list is never NULL: an empty list is an empty array, and
                      // List.isEmpty tests its cardinality.
                      let none = plasm { for c in tableOf<Category> do where (List.isEmpty c.Keywords); select c.Name }
                      let! names = repo.All none
                      ok names |> shouldBe __
                  }
          )

          Koan.Task(
              "Changing a list writes its column alone",
              fun repo ->
                  task {
                      // A changed list is one changed column: the UPDATE's SET names keywords
                      // and no other column.
                      let! c = repo.One(category "Hoses")
                      let hoses = (ok c).Value
                      let! _ = repo.Transact(Tx.update (hoses, { hoses with Keywords = [ "water"; "garden" ] }): Tx<Category, unit>)
                      let update = sqlSent () |> List.find (fun q -> q.StartsWith "UPDATE")
                      let set = update.Substring(0, update.IndexOf "WHERE")
                      (set.Contains "\"keywords\" = " && not (set.Contains "\"name\"")) |> shouldBe __
                      let! after = repo.One(category "Hoses")
                      (ok after).Value.Keywords |> shouldBe __
                  }
          ) ]
