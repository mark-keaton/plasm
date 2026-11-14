/// Lesson 09: About relationships.
module Plasm.Koans.Lessons.L09_Relationships

open Plasm
open Plasm.Koans
open Plasm.Koans.Shop

let private byId (orders: Order list) (id: int64) = orders |> List.find (fun o -> o.Id = OrderId id)

let lesson =
    Lesson.make
        "About relationships"
        [ Koan.Task(
              "Preloading a belongs-to",
              fun k ->
                  task {
                      // A relationship links records: each Order belongs to a Customer
                      // (belongsTo in Db.orders, through the CustomerId field).
                      // A relationship field is an Assoc: NotLoaded, or Loaded with its value.
                      // preload asks the query to load it. byId, at the top of this lesson,
                      // finds the Order with a given id in a list.
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Customer })
                      let order = byId (ok orders) 3L
                      // An Order's Customer is optional, so the loaded value is an option.
                      order.Customer |> Assoc.map (Option.map (fun c -> c.Name)) |> shouldBe __
                  }
          )

          Koan.Task(
              "Preloading a has-many",
              fun k ->
                  task {
                      // An Order has many OrderLines (hasMany in Db.orders): Lines is a list.
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Lines })
                      let order = byId (ok orders) 1L
                      order.Lines |> Assoc.map List.length |> shouldBe __
                      // Payments was not asked for.
                      order.Payments |> shouldBe __
                  }
          )

          Koan.Task(
              "An Order without a Customer is kept",
              fun k ->
                  task {
                      // Order 4 has no customer. Loading Customers does not drop it.
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Customer })
                      (ok orders).Length |> shouldBe __
                      (byId (ok orders) 4L).Customer |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading a loaded relationship",
              fun k ->
                  task {
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Lines })
                      let order = byId (ok orders) 2L
                      // Assoc.get takes the value out of Loaded. It never queries: on a
                      // NotLoaded relationship it fails, saying the relationship was not loaded.
                      let lines = order.Lines |> Assoc.get
                      lines.Head.UnitPrice |> shouldBe __
                  }
          )

          Koan.Task(
              "Not loaded is not empty",
              fun k ->
                  task {
                      let! plain = k.Repo.All(Query.from Db.orders)
                      (byId (ok plain) 1L).Lines |> shouldBe __

                      // Order 4 has no lines: once loaded, that is an empty list.
                      let! loaded = k.Repo.All(plasm { for o in Db.orders do preload o.Lines })
                      (byId (ok loaded) 4L).Lines |> shouldBe __
                  }
          )

          Koan.Task(
              "Reading a field never queries",
              fun k ->
                  task {
                      let! orders = k.Repo.All(Query.from Db.orders)

                      for o in ok orders do
                          for _ in 1..3 do
                              o.Lines |> ignore

                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Loads are batched across parents",
              fun k ->
                  task {
                      // Four Orders, and their Lines.
                      let! _ = k.Repo.All(plasm { for o in Db.orders do preload o.Lines })
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "Siblings load separately",
              fun k ->
                  task {
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Lines; preload o.Payments })
                      let first = byId (ok orders) 1L
                      k.Commands.Length |> shouldBe __
                      (first.Lines |> Assoc.get |> List.length, first.Payments |> Assoc.get |> List.length) |> shouldBe __
                  }
          )

          Koan.Task(
              "Loads nest",
              fun k ->
                  task {
                      let lines = plasm { for l in Db.orderLines do orderBy l.Id; preload l.Product }
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Lines lines })
                      let firstLine = (byId (ok orders) 1L).Lines |> Assoc.get |> List.head
                      (firstLine.Product |> Assoc.get).Name |> shouldBe __
                      k.Commands.Length |> shouldBe __
                  }
          )

          Koan.Task(
              "A child filter filters children",
              fun k ->
                  task {
                      let succeeded = plasm { for p in Db.payments do where (p.Status = Succeeded) }
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Payments succeeded })
                      (ok orders).Length |> shouldBe __
                      (byId (ok orders) 3L).Payments |> Assoc.get |> List.length |> shouldBe __
                  }
          )

          Koan.Task(
              "Filtering parents through children",
              fun k ->
                  task {
                      // Order 3 has two payments, one of them succeeded.
                      let paid = plasm { for o in Db.orders do where (Rel.any (fun p -> p.Status = Succeeded) o.Payments); orderBy o.Id }
                      let! orders = k.Repo.All paid
                      ok orders |> List.map (fun o -> o.Id) |> shouldBe __
                  }
          )

          Koan.Task(
              "At most n per parent",
              fun k ->
                  task {
                      let latest = plasm { for p in Db.payments do orderByDescending p.CreatedAt; limitPerParent 1 }
                      let! orders = k.Repo.All(plasm { for o in Db.orders do preload o.Payments latest })
                      (byId (ok orders) 3L).Payments |> Assoc.get |> List.map (fun p -> p.Status) |> shouldBe __
                  }
          )

          Koan.Task(
              "Loading onto values you hold",
              fun k ->
                  task {
                      let! orders = k.Repo.All(Query.from Db.orders)
                      let originals = ok orders
                      let! withLines = k.Repo.Preload(plasm { for o in Db.orders do preload o.Lines }, originals)
                      (byId originals 1L).Lines |> shouldBe __
                      (byId (ok withLines) 1L).Lines |> Assoc.get |> List.length |> shouldBe __
                  }
          ) ]
