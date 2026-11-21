/// Lesson 29: About streaming reads.
module Plasm.Koans.Lessons.L29_StreamingReads

open System.Collections.Generic
open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// Every element of a stream, asked for one at a time, then the stream given back.
let private drain (stream: IAsyncEnumerable<Result<'T, DbError>>) =
    task {
        let e = stream.GetAsyncEnumerator()
        let items = ResizeArray<Result<'T, DbError>>()
        let mutable more = true

        while more do
            let! next = e.MoveNextAsync()
            if next then items.Add e.Current else more <- false

        do! e.DisposeAsync()
        return List.ofSeq items
    }

let private byName = plasm { for c in tableOf<Customer> do orderBy c.Name }

let lesson =
    Lesson.make
        "About streaming reads"
        [ Koan.Task(
              "A stream gives its rows as they are asked for",
              fun repo ->
                  task {
                      // repo.Stream query is an IAsyncEnumerable of Result<'T, DbError>: the
                      // rows, read from the database as the caller asks for the next, never
                      // gathered into a list. drain (above, under Given) asks for each.
                      let! items = drain (repo.Stream byName)
                      items |> List.map (Result.map (fun c -> c.Name)) |> shouldBe __
                  }
          )

          Koan.Task(
              "Nothing is sent before the first row",
              fun repo ->
                  task {
                      // Making a stream sends nothing; its one command is sent when the
                      // caller asks for the first row. Plasm reports the command once, when
                      // the stream ends, with the rows read.
                      let stream = repo.Stream byName
                      sqlSent().Length |> shouldBe __
                      let! _ = drain stream
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A projection is streamed",
              fun repo ->
                  task {
                      // A projection streams as a query of records does.
                      let! items = drain (repo.Stream(plasm { for c in tableOf<Customer> do orderBy c.Name; select c.Name }))
                      items |> shouldBe __
                  }
          )

          Koan.Task(
              "A stream is one command",
              fun repo ->
                  task {
                      // An order owns its lines, which are read by a second command. A stream
                      // is one command, so a stream of orders is a single Error, naming the
                      // list ("Order.Lines is read by another command"), and nothing is sent.
                      let! items = drain (repo.Stream(Query.from (tableOf<Order>)))

                      let refused =
                          match items with
                          | [ Error e ] -> (string e).Contains "Order.Lines"
                          | _ -> false

                      refused |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          ) ]
