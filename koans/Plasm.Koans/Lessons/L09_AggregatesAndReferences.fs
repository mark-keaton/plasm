/// Lesson 09: About aggregates and references.
module Plasm.Koans.Lessons.L09_AggregatesAndReferences

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private byId (orders: Order list) (id: int64) = orders |> List.find (fun o -> o.Id = OrderId id)

/// An order's payments: the tie between the two aggregates, written once.
/// [<ReflectedDefinition>] lets Plasm read the function as part of a query.
[<ReflectedDefinition>]
let paymentsOf (o: Order) =
    plasm {
        for p in tableOf<Payment> do
            where (p.OrderId = o.Id)
    }

let lesson =
    Lesson.make
        "About aggregates and references"
        [ Koan.Task(
              "An order is read with its lines",
              fun repo ->
                  task {
                      // An aggregate is a record together with the records it owns. An
                      // Order owns its OrderLines: Order has a field Lines: OrderLine list,
                      // and an OrderLine holds its order's key (OrderId). Whenever an
                      // order is read, its Lines are read with it.
                      // byId, at the top of this lesson, finds the Order with an id in a list.
                      let! orders = repo.All(Query.from (tableOf<Order>))
                      let order = byId (ok orders) 1L
                      order.Lines.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Lines come in the order of their key",
              fun repo ->
                  task {
                      // Owned records are read in the order of their key, here l.Id, so
                      // the order they were written in. [<OrderedBy>] on the field would
                      // name another order.
                      let! orders = repo.All(Query.from (tableOf<Order>))
                      (byId (ok orders) 1L).Lines |> List.map (fun l -> l.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "No lines is an empty list",
              fun repo ->
                  task {
                      // Order 4 has no lines. An owned list is always read, so an empty
                      // list means there are none: never "not read yet".
                      let! orders = repo.All(Query.from (tableOf<Order>))
                      (byId (ok orders) 4L).Lines |> shouldBe __
                  }
          )

          Koan.Task(
              "Every order's lines come in one more command",
              fun repo ->
                  task {
                      // Four orders: one command reads them, and one more reads the lines
                      // of all four. Reading o.Lines afterwards never queries.
                      let! orders = repo.All(Query.from (tableOf<Order>))

                      for o in ok orders do
                          o.Lines |> ignore

                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A page of orders reads only its lines",
              fun repo ->
                  task {
                      // take 2 keeps the first two orders. Their lines are read with them,
                      // and only theirs: the second command asks for the lines of two orders.
                      let! page = repo.All(plasm { for o in tableOf<Order> do orderBy o.Id; take 2 })
                      ok page |> List.collect (fun o -> o.Lines) |> List.length |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A customer is referred to by id",
              fun repo ->
                  task {
                      // A Customer is an aggregate of its own, so an Order holds only its
                      // CustomerId. Because CustomerId is the key type of tableOf<Customer>,
                      // Plasm knows the field refers to a customer.
                      // To reach the customer, ask for it with a query.
                      let! orders = repo.All(Query.from (tableOf<Order>))
                      let customerId = (byId (ok orders) 3L).CustomerId
                      customerId |> shouldBe __
                      let id = customerId.Value
                      let! customer = repo.One(plasm { for c in tableOf<Customer> do where (c.Id = id) })
                      (ok customer).Value.Name |> shouldBe __
                  }
          )

          Koan.Task(
              "A join reads both aggregates' fields",
              fun repo ->
                  task {
                      // A join pairs each order with its customer in one command, and
                      // select picks the fields to read. Order 4 has no customer, so an
                      // inner join leaves it out. (Lesson 14 goes deeper.)
                      let! rows =
                          repo.All(
                              plasm {
                                  for o in tableOf<Order> do
                                      join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                      orderBy o.Id
                                      select (o.Id, c.Name)
                              }
                          )

                      ok rows |> shouldBe __
                  }
          )

          Koan.Task(
              "A whole order from a join comes with its lines",
              fun repo ->
                  task {
                      // A whole Order is always read with its lines. Selected from a join,
                      // the joined rows come from one command, and the lines of every
                      // Order in them from one more.
                      let! rows =
                          repo.All(
                              plasm {
                                  for o in tableOf<Order> do
                                      join c in tableOf<Customer> on (o.CustomerId = Some c.Id)
                                      where (c.Name = "Ada")
                                      orderBy o.Id
                                      select (o, c.Name)
                              }
                          )

                      let first, _ = List.head (ok rows)
                      first.Lines.Length |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering orders by their lines",
              fun repo ->
                  task {
                      // List.exists over an owned list is a filter the database runs:
                      // the orders with a line for product 2 (the Gadget), each once.
                      let gadgets =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Lines |> List.exists (fun l -> l.ProductId = ProductId 2L))
                                  orderBy o.Id
                          }

                      let! rows = repo.All gadgets
                      ok rows |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Every line passes, as F# reads it",
              fun repo ->
                  task {
                      // List.forall keeps the orders whose every line passes. As in F#,
                      // an order with no lines passes: there is no line that fails.
                      let tenOrMore =
                          plasm {
                              for o in tableOf<Order> do
                                  where (o.Lines |> List.forall (fun l -> l.UnitPrice >= 10m))
                                  orderBy o.Id
                          }

                      let! rows = repo.All tenOrMore
                      ok rows |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Orders with a succeeded payment",
              fun repo ->
                  task {
                      // A Payment is another aggregate: it refers to its order by OrderId.
                      // exists (an inner query) inside a where asks whether any payment
                      // matches this order, in the same command.
                      let paid =
                          plasm {
                              for o in tableOf<Order> do
                                  where (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) }))
                                  orderBy o.Id
                          }

                      let! rows = repo.All paid
                      ok rows |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Orders without a succeeded payment",
              fun repo ->
                  task {
                      // not (exists ...) keeps the orders no succeeded payment refers to.
                      let unpaid =
                          plasm {
                              for o in tableOf<Order> do
                                  where (not (exists (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id && p.Status = Succeeded) })))
                                  orderBy o.Id
                          }

                      let! rows = repo.All unpaid
                      ok rows |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "Each order with its payments",
              fun repo ->
                  task {
                      // A query ending in including reads each order with the list an
                      // inner query finds for it. The inner query names the tie,
                      // p.OrderId = o.Id; the result's type, (Order * Payment list),
                      // says what was read. Plasm reads the payments of every order in
                      // one more command and pairs them up.
                      let withPayments =
                          plasm {
                              for o in tableOf<Order> do
                                  orderBy o.Id
                                  including (plasm { for p in tableOf<Payment> do where (p.OrderId = o.Id); orderBy p.Id })
                          }

                      let! rows = repo.All withPayments
                      ok rows |> List.map (fun (o, payments) -> o.Id, payments.Length) |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A tie written once serves exists and including",
              fun repo ->
                  task {
                      // paymentsOf (top of this lesson) is the tie, written once. It
                      // works inside exists, and as the start of an inner query that
                      // narrows it further. Inside including, orderBy and take apply to
                      // each order's payments, and select picks what the list holds:
                      // here, the status of the newest one of each.
                      let paidOrders =
                          plasm {
                              for o in tableOf<Order> do
                                  where (exists (paymentsOf o))
                                  orderBy o.Id
                                  including (plasm { for p in paymentsOf o do orderByDescending p.CreatedAt; take 1; select p.Status })
                          }

                      let! rows = repo.All paidOrders
                      ok rows |> List.map (fun (o, newest) -> o.Id, newest) |> shouldBe __
                  }
          )

          Koan.Task(
              "Each order with its payments and its deliveries",
              fun repo ->
                  task {
                      // A query may end in up to eight includings. Each adds one list
                      // to the tuple, in the order written, and is one more command:
                      // two lists never multiply each other's rows.
                      let withBoth =
                          plasm {
                              for o in tableOf<Order> do
                                  orderBy o.Id
                                  including (paymentsOf o)
                                  including (plasm { for d in tableOf<Delivery> do where (d.OrderId = o.Id) })
                          }

                      let! rows = repo.All withBoth
                      ok rows |> List.map (fun (o, payments, deliveries) -> o.Id, payments.Length, deliveries |> List.map (fun d -> d.StageName)) |> shouldBe __
                      sqlSent().Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Each customer with their orders, each with its payments",
              fun repo ->
                  task {
                      // An inner query may end in its own including, so its items come
                      // with their own lists: Customer * (Order * Payment list) list.
                      // An order's CustomerId is optional, so the tie compares it with
                      // Some c.Id.
                      let customers =
                          plasm {
                              for c in tableOf<Customer> do
                                  orderBy c.Id

                                  including (
                                      plasm {
                                          for o in tableOf<Order> do
                                              where (o.CustomerId = Some c.Id)
                                              orderBy o.Id
                                              including (paymentsOf o)
                                      }
                                  )
                          }

                      let! rows = repo.All customers
                      ok rows |> List.map (fun (c, orders) -> c.Name, orders |> List.map (fun (o, payments) -> o.Id, payments.Length)) |> shouldBe __
                  }
          ) ]
