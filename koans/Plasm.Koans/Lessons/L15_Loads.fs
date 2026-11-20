/// Lesson 15: About loads.
module Plasm.Koans.Lessons.L15_Loads

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

/// An order's succeeded payments: a relationship that says more than the tie,
/// written once. [<ReflectedDefinition>] lets Plasm read the function as part
/// of a query.
[<ReflectedDefinition>]
let succeededPaymentsOf (o: Order) =
    plasm {
        for p in referring<Payment> o do
            where (p.Status = Succeeded)
    }

let lesson =
    Lesson.make
        "About loads"
        [ Koan.Task(
              "Each order with its payments",
              fun repo ->
                  task {
                      // let! binds what another query finds for each order, here the
                      // payments referring to it, and return builds the row: the load
                      // goes in the field of its name. Plasm reads the payments of every
                      // order in one more command and pairs them up.
                      let withPayments =
                          plasm {
                              for o in tableOf<Order> do
                              orderBy o.Id
                              let! payments = plasm { for p in referring<Payment> o do orderBy p.Id }
                              return {| Order = o; Payments = payments |}
                          }

                      let! rows = repo.All withPayments
                      ok rows |> List.map (fun row -> row.Order.Id, row.Payments.Length) |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A relationship that says more is written once",
              fun repo ->
                  task {
                      // succeededPaymentsOf (top of this lesson) says more than the tie,
                      // so it is a function, written once and built on referring. It
                      // works inside exists, and as the start of an inner query that
                      // narrows it further. In a let!, orderBy and take apply to each
                      // order's payments, and select picks what the list holds: here,
                      // the status of the newest one of each.
                      let paidOrders =
                          plasm {
                              for o in tableOf<Order> do
                              where (exists (succeededPaymentsOf o))
                              orderBy o.Id
                              let! newest = plasm { for p in succeededPaymentsOf o do orderByDescending p.CreatedAt; take 1; select p.Status }
                              return {| Order = o; Newest = newest |}
                          }

                      let! rows = repo.All paidOrders
                      ok rows |> List.map (fun row -> row.Order.Id, row.Newest) |> shouldBe __
                  }
          )

          Koan.Task(
              "Each order with its payments and its deliveries",
              fun repo ->
                  task {
                      // and! binds one more load beside the first, as many as you need.
                      // Each is one more command, so two lists never multiply each
                      // other's rows, and each goes in the field of its name.
                      let withBoth =
                          plasm {
                              for o in tableOf<Order> do
                              orderBy o.Id
                              let! payments = referring<Payment> o
                              and! deliveries = referring<Delivery> o
                              return {| Order = o; Payments = payments; Deliveries = deliveries |}
                          }

                      let! rows = repo.All withBoth
                      ok rows |> List.map (fun row -> row.Order.Id, row.Payments.Length, row.Deliveries |> List.map (fun d -> d.StageName)) |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Each customer with their orders, each with its payments",
              fun repo ->
                  task {
                      // A load's query may bind loads of its own and build its own row,
                      // so each customer's orders come with their payments. An order's
                      // CustomerId is optional; referring finds it all the same, as
                      // o.CustomerId = Some c.Id.
                      let customers =
                          plasm {
                              for c in tableOf<Customer> do
                              orderBy c.Id

                              let! orders =
                                  plasm {
                                      for o in referring<Order> c do
                                      orderBy o.Id
                                      let! payments = referring<Payment> o
                                      return {| Order = o; Payments = payments |}
                                  }

                              return {| Customer = c; Orders = orders |}
                          }

                      let! rows = repo.All customers
                      ok rows |> List.map (fun row -> row.Customer.Name, row.Orders |> List.map (fun x -> x.Order.Id, x.Payments.Length)) |> shouldBe __
                  }
          )

          Koan.Task(
              "Each order with its customer",
              fun repo ->
                  task {
                      // referredBy<Customer> o is the customer this order refers to: the
                      // one whose key is the order's CustomerId. A key finds at most one,
                      // so a let! of it, or of what is selected of it, binds an option;
                      // an order with no customer comes with None.
                      let withCustomer =
                          plasm {
                              for o in tableOf<Order> do
                              orderBy o.Id
                              let! name = plasm { for c in referredBy<Customer> o do select c.Name }
                              return {| Order = o; Name = name |}
                          }

                      let! rows = repo.All withCustomer
                      ok rows |> List.map (fun row -> row.Order.Id, row.Name) |> shouldBe __
                  }
          ) ]
