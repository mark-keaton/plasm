namespace Shop

open System
open System.Threading
open System.Threading.Tasks
open Plasm.CSharp

type OrderId = OrderId of Guid

type Line = { Part: string; Quantity: int }

type Status =
    | Open
    | Shipped of at: DateTimeOffset
    | Cancelled of reason: string

type Order =
    { Id: OrderId
      Customer: string
      Note: string option
      Lines: Line list
      Status: Status
      Version: int }

/// What the host calls. It keeps its orders in memory: the seam is what is checked.
[<Sealed>]
type Orders() =
    let first = OrderId(Guid.Parse "01900000-0000-7000-8000-000000000001")

    let orders =
        [ { Id = first
            Customer = "Ada"
            Note = Some "by the door"
            Lines = [ { Part = "bolt"; Quantity = 4 } ]
            Status = Shipped(DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero))
            Version = 2 }
          { Id = OrderId(Guid.Parse "01900000-0000-7000-8000-000000000002")
            Customer = "Grace"
            Note = None
            Lines = []
            Status = Cancelled "out of stock"
            Version = 1 } ]

    member _.OrderAsync(id: OrderId, cancellationToken: CancellationToken) : Task<Found<Order>> =
        match orders |> List.tryFind (fun o -> o.Id = id) with
        | Some o -> Task.FromResult(Found<Order>(o))
        | None -> Task.FromResult(Found<Order>({ What = "order" }: Missing))

    member _.OrdersAsync(cancellationToken: CancellationToken) : Task<Order list> = Task.FromResult orders

    member _.AddOrderAsync(customer: string, note: string option, cancellationToken: CancellationToken) : Task<Added<Order>> =
        if String.IsNullOrWhiteSpace customer then
            Task.FromResult(Added<Order>(Invalid.field "customer" "is required"))
        else
            Task.FromResult(
                Added<Order>(
                    { Id = OrderId(Guid.Parse "01900000-0000-7000-8000-000000000003")
                      Customer = customer
                      Note = note
                      Lines = []
                      Status = Open
                      Version = 1 }
                )
            )
