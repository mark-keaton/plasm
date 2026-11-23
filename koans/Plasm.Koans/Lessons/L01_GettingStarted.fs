/// Lesson 01: Getting started.
module Plasm.Koans.Lessons.L01_GettingStarted

open Plasm.Koans
open Plasm.Koans.Shop

let lesson =
    Lesson.make
        "Getting started"
        [ Koan.Sync(
              "Fill in the blank",
              fun _ ->
                  // A koan is a few lines of F# with a blank, written __. Replace
                  // the blank with a value and run the koans again.
                  // `actual |> shouldBe expected` passes when the two are equal.
                  let name = "Widget"
                  name |> shouldBe __
          )

          Koan.Sync(
              "Values do not change",
              fun _ ->
                  // let names a value. The name keeps that value: working out a
                  // new value gives a new name, and the old one is unchanged.
                  // 10m is the number 10 as a decimal, the type for money: the m
                  // after a number marks a decimal.
                  let price = 10m
                  let discounted = price - 2m
                  discounted |> shouldBe __
                  price |> shouldBe __
          )

          Koan.Sync(
              "The pipe passes a value along",
              fun _ ->
                  // [ ... ] is a list. The pipe, |>, hands the value on its left
                  // to the function on its right: `xs |> List.sort` is `List.sort xs`.
                  let totals = [ 30m; 12.5m; 99m ]
                  totals |> List.sort |> List.head |> shouldBe __
          )

          Koan.Sync(
              "A record groups named fields",
              fun _ ->
                  // A record is a value with named fields. Product is a record
                  // defined in Shop.fs, with an Id, a Sku and a Name.
                  let widget = { Id = ProductId 1L; Sku = "WIDGET"; Name = "Widget" }
                  widget.Sku |> shouldBe __
          )

          Koan.Sync(
              "with makes a changed copy",
              fun _ ->
                  // Records cannot be changed. `{ r with Field = value }` makes a
                  // new record with that field changed and every other field copied.
                  let widget = { Id = ProductId 1L; Sku = "WIDGET"; Name = "Widget" }
                  let renamed = { widget with Name = "Blue widget" }
                  renamed.Name |> shouldBe __
                  widget.Name |> shouldBe __
          )

          Koan.Sync(
              "A wrapper keeps a value's meaning",
              fun _ ->
                  // `type CustomerId = CustomerId of int64` (in Shop.fs) wraps a
                  // number, so a customer's id cannot be mixed up with an order's.
                  // Matching on the case takes the number back out.
                  let ada = CustomerId 1L
                  let (CustomerId n) = ada
                  n |> shouldBe __
          )

          Koan.Sync(
              "An option may be absent",
              fun _ ->
                  // An option is either Some value or None (nothing there).
                  // Option.defaultValue gives the value, or the default for None.
                  // `let reason: string option` names a value and says its type,
                  // after the colon.
                  let reason: string option = Some "duplicate"
                  let noReason: string option = None
                  reason |> Option.defaultValue "no reason" |> shouldBe __
                  noReason |> Option.defaultValue "no reason" |> shouldBe __
          )

          Koan.Sync(
              "A result is Ok or Error",
              fun _ ->
                  // A Result is either Ok value or Error reason. Quantity.create
                  // (in Shop.fs) refuses a quantity that is not positive.
                  // `ok r` takes the value out of Ok, `err r` the reason out of Error;
                  // given the other case, they fail the koan.
                  let three = Quantity.create 3
                  let zero = Quantity.create 0
                  (ok three).Value |> shouldBe __
                  err zero |> shouldBe __
          )

          Koan.Task(
              "let! waits for a task",
              fun _ ->
                  // A task is work that finishes later, such as a database call.
                  // Inside task { ... }, let! waits for a task and names its result.
                  task {
                      let countLater () = task { return 2 + 2 }
                      let! n = countLater ()
                      n |> shouldBe __
                  }
          ) ]
