Feature: The seam writes the C# a host calls from its F# data project
  A C# host that references Plasm.CSharp and an F# data project, and has a
  Seam.cs, gets the C# side of its data layer written for it before its own
  C# is compiled: a C# record for each F# record a data method answers, a C#
  union for each F# union, the mapping from one to the other, and a C# class
  with the data type's members, which take plain arguments and answer those
  views. The files are readable source in the project's Seam folder, so
  System.Text.Json's and ASP.NET's own generators see them, and seam.lock
  describes the contract they make. The F# project holds no view type and no
  mapping. The seam reads the data assembly's metadata; it runs none of it.

  Background:
    Given the F# data project:
      """
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
          | Cancelled of reason: string * at: DateTimeOffset

      type Order =
          { Id: OrderId
            Customer: string
            Note: string option
            Lines: Line list
            Status: Status
            Version: int }

      [<Sealed>]
      type Orders() =
          let order =
              { Id = OrderId(Guid.Empty); Customer = "Ada"; Note = None; Lines = []; Status = Open; Version = 1 }

          member _.OrderAsync(id: OrderId, cancellationToken: CancellationToken) : Task<FindResult<Order>> =
              Task.FromResult(FindResult<Order>(order))

          member _.OrdersAsync(cancellationToken: CancellationToken) : Task<Order list> = Task.FromResult [ order ]

          member _.AddOrderAsync(customer: string, note: string option, cancellationToken: CancellationToken) : Task<AddResult<Order>> =
              Task.FromResult(AddResult<Order>(order))
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Rule: An F# record crosses as a C# record, field by field

    Scenario: Each field in order: an option may be null, a list is an array, a typed id is its value
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record OrderView(Guid Id, string Customer, string? Note, LineView[] Lines, StatusView Status, int Version);"
      And Views.g.cs declares "public sealed record LineView(string Part, int Quantity);"
      And the C# compiles with the host

  Rule: An F# union crosses as a C# union of a record per case

    Scenario: Each case is a record of its fields, named in PascalCase, with its JSON kind first
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public union StatusView(Open, Shipped, Cancelled);"
      And Views.g.cs declares "public sealed record Cancelled(string Reason, DateTimeOffset At)"
      And Views.g.cs declares "[JsonPropertyOrder(-1)] public string Kind => \"cancelled\";"
      And the C# compiles with the host

  Rule: The data type crosses as a C# class with its members, over views

    Scenario: Each member takes plain arguments and answers the outcome of the view
      When the seam is written into the namespace Shop.Api
      Then seam.lock holds
        """
        class Orders = Shop.Orders
          Task<FindResult<OrderView>> OrderAsync(Guid id, CancellationToken cancellationToken)
          Task<OrderView[]> OrdersAsync(CancellationToken cancellationToken)
          Task<AddResult<OrderView>> AddOrderAsync(string customer, string? note, CancellationToken cancellationToken)
        """
      And the C# compiles with the host:
        """
        using Plasm.CSharp;

        namespace Shop.Api;

        public static class Host
        {
            public static async System.Threading.Tasks.Task<string> Describe(Orders orders)
            {
                var all = await orders.OrdersAsync(default);
                return await orders.OrderAsync(Guid.Empty, default) switch
                {
                    OrderView o => $"{o.Customer} of {all.Length}",
                    Missing m => m.What,
                };
            }
        }
        """

  Rule: Seam.cs names, leaves out and renames

    Scenario: A view and a case renamed, a field left out, a kind given
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamView(typeof(Order), Name = "Purchase", Omit = [nameof(Order.Version)])]
        [assembly: SeamCase(typeof(Status), nameof(Status.Open), Name = "Pending", Kind = "pending")]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record Purchase(Guid Id, string Customer, string? Note, LineView[] Lines, StatusView Status);"
      And Views.g.cs declares "public union StatusView(Pending, Shipped, Cancelled);"
      And Views.g.cs declares "[JsonPropertyOrder(-1)] public string Kind => \"pending\";"
      And the C# compiles with the host

    Scenario: A type Seam.cs names that the data project does not have stops the seam, naming where
      Given Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Orders))]
        [assembly: SeamView(typeof(Shop.Invoice), Name = "Bill")]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam stops with SEAM002 at Seam.cs(4,12): "typeof(Shop.Invoice) names no type in Shop.dll"

    Scenario: A field Seam.cs leaves out that the record does not have stops the seam
      Given Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Orders))]
        [assembly: SeamView(typeof(Shop.Order), Omit = ["Total"])]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam stops with SEAM003 at Seam.cs(4,12): "SeamView omits Shop.Order.Total, which is not a field of the record"

  Rule: Only what changed is written

    Scenario: Written again with nothing changed, no file is rewritten
      Given the seam is written into the namespace Shop.Api
      When the seam is written into the namespace Shop.Api again
      Then no file in Seam is rewritten, nor seam.lock
