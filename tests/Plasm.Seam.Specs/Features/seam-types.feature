Feature: What a type becomes in C#, what cannot cross, and what is carried with it
  The seam reads the data assembly's metadata, which keeps some of what the
  F# says and not the rest. A unit of measure and a type abbreviation are not
  in it: `decimal<kg>` and `type Money = decimal` arrive as decimal. What has
  no C# shape the seam can stand behind stops the build, naming the member
  and saying what to write instead, never a wrong type: a Result, a Map, a
  tuple, an F# function, a generic record, an option of an option, a record
  whose fields are private, a class. The F# doc comments come with the views
  and the members, so C# shows them. A computed field, a member of the F#
  record, is placed where Seam.cs says in the view and its JSON.

  Rule: A unit of measure and an abbreviation are the type underneath

    Scenario: decimal<kg> and Money (decimal) are decimal in C#
      Given the F# data project:
        """
        namespace Shop

        open System.Threading.Tasks
        open Plasm.CSharp

        [<Measure>]
        type kg

        type Money = decimal

        type Parcel = { Weight: decimal<kg>; Postage: Money }

        [<Sealed>]
        type Parcels() =
            member _.ParcelAsync() : Task<Found<Parcel>> = Task.FromResult(Found<Parcel>({ Weight = 2.5m<kg>; Postage = 4m }))
        """
      And Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Parcels))]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record ParcelView(decimal Weight, decimal Postage);"

  Rule: What has no C# shape stops the seam, naming the member

    Scenario Outline: A field of <what> stops the seam, naming the field and saying what to write
      Given the F# data project:
        """
        namespace Shop

        open System.Threading.Tasks
        open Plasm.CSharp

        type Pair<'a> = { First: 'a; Second: 'a }

        type Hidden = private { Secret: string }

        type Kept() =
            member _.Value = 1

        type Order = { Customer: string; Odd: <type> }

        [<Sealed>]
        type Orders() =
            member _.OrderAsync() : Task<Found<Order>> = Task.FromResult(Found<Order>({ What = "order" }: Missing))
        """
      And Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Orders))]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam stops with <code>: "<message>"

      Examples:
        | what                         | type                | code    | message                                                                                                                 |
        | a Result                     | Result<int, string> | SEAM010 | Shop.Order.Odd: Result<int, string> cannot cross the seam: a Result has no C# shape                                     |
        | a Map                        | Map<string, int>    | SEAM010 | Shop.Order.Odd: Map<string, int> cannot cross the seam: a Map has no view; use a list of records                        |
        | a tuple                      | int * string        | SEAM010 | Shop.Order.Odd: int * string cannot cross the seam: a tuple's parts have no names; use a record                         |
        | an F# function               | int -> int          | SEAM010 | Shop.Order.Odd: int -> int cannot cross the seam: an F# function is not something C# passes                             |
        | a generic record             | Pair<int>           | SEAM010 | Shop.Order.Odd: Shop.Pair<int> cannot cross the seam: a generic record has no view yet                                  |
        | an option of an option       | int option option   | SEAM010 | Shop.Order.Odd: int option option cannot cross the seam: an option of something that may itself be null                 |
        | a record with private fields | Hidden              | SEAM011 | Shop.Order.Odd: Shop.Hidden keeps its fields private                                                                    |
        | a class                      | Kept                | SEAM010 | Shop.Order.Odd: Shop.Kept cannot cross the seam: it is an F# class, module or exception, not a record or a union        |

  Rule: The F# doc comments come with what they describe

    Scenario: A record's, its fields', a union's, a case's and a member's comments are in the C#
      Given the F# data project:
        """
        namespace Shop

        open System.Threading
        open System.Threading.Tasks
        open Plasm.CSharp

        /// Where an order is.
        type Status =
            /// Not yet sent.
            | Open
            /// On its way since `at`.
            | Shipped of at: System.DateTimeOffset

        /// An order a customer placed.
        type Order =
            { /// Who placed it.
              Customer: string
              Status: Status }

        /// The orders, as the host reads them.
        [<Sealed>]
        type Orders() =
            /// The order, or Missing when there is none.
            member _.OrderAsync(number: int, cancellationToken: CancellationToken) : Task<Found<Order>> =
                Task.FromResult(Found<Order>({ Customer = "Ada"; Status = Open }))
        """
      And Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Orders))]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "/// <summary>An order a customer placed.</summary> /// <param name=\"Customer\">Who placed it.</param> public sealed record OrderView(string Customer, StatusView Status);"
      And Views.g.cs declares "/// <summary>Not yet sent.</summary> public sealed record Open"
      And Views.g.cs declares "/// <summary>Where an order is.</summary> [JsonUnion(TypeClassifier = typeof(StatusViewByKind))] public union StatusView(Open, Shipped);"
      And Orders.g.cs declares "/// <summary>The orders, as the host reads them.</summary> public sealed class Orders("
      And Orders.g.cs declares "/// <summary>The order, or Missing when there is none.</summary> public async Task<Found<OrderView>> OrderAsync(int number, CancellationToken cancellationToken) =>"
      And the C# compiles with the host

  Rule: Seam.cs places a field in the view, a computed one among them

    Background:
      Given the F# data project:
        """
        namespace Shop

        open System.Threading.Tasks
        open Plasm.CSharp

        type Line = { Part: string; Quantity: int; Price: decimal }

        type Order =
            { Customer: string
              Lines: Line list
              Version: int }

            /// What the lines come to.
            member o.Total = o.Lines |> List.sumBy (fun l -> decimal l.Quantity * l.Price)

        [<Sealed>]
        type Orders() =
            member _.OrderAsync() : Task<Found<Order>> =
                Task.FromResult(Found<Order>({ Customer = "Ada"; Lines = [ { Part = "bolt"; Quantity = 4; Price = 0.5m } ]; Version = 1 }))
        """

    Scenario: A computed field is last, unless Seam.cs places it
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamField(typeof(Order), nameof(Order.Total))]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record OrderView(string Customer, LineView[] Lines, int Version, decimal Total);"
      And the host, run, prints "{\"customer\":\"Ada\",\"lines\":[{\"part\":\"bolt\",\"quantity\":4,\"price\":0.5}],\"version\":1,\"total\":2.0}"
        """
        using System.Text.Json;
        using Plasm.CSharp;

        namespace Shop.Api;

        public static class Host
        {
            public static string Run() => new Orders(new Shop.Orders()).OrderAsync().Result switch
            {
                OrderView o => JsonSerializer.Serialize(o, SeamJson.Default.OrderView),
                Missing m => m.What,
            };
        }
        """

    Scenario: A computed field placed after another is there in the view and in its JSON
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamField(typeof(Order), nameof(Order.Total), After = nameof(Order.Lines))]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record OrderView(string Customer, LineView[] Lines, decimal Total, int Version);"
      And the host, run, prints "{\"customer\":\"Ada\",\"lines\":[{\"part\":\"bolt\",\"quantity\":4,\"price\":0.5}],\"total\":2.0,\"version\":1}"
        """
        using System.Text.Json;
        using Plasm.CSharp;

        namespace Shop.Api;

        public static class Host
        {
            public static string Run() => new Orders(new Shop.Orders()).OrderAsync().Result switch
            {
                OrderView o => JsonSerializer.Serialize(o, SeamJson.Default.OrderView),
                Missing m => m.What,
            };
        }
        """

    Scenario: A record's own field placed first
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamField(typeof(Order), nameof(Order.Version), Before = nameof(Order.Customer))]
        """
      When the seam is written into the namespace Shop.Api
      Then Views.g.cs declares "public sealed record OrderView(int Version, string Customer, LineView[] Lines);"

    Scenario: A place after a field the view does not have stops the seam
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamView(typeof(Order), Omit = [nameof(Order.Version)])]
        [assembly: SeamField(typeof(Order), nameof(Order.Total), After = nameof(Order.Version))]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam stops with SEAM003 at Seam.cs(6,12): "SeamField places Shop.Order.Total after Version, which OrderView does not have"

    Scenario: A member Seam.cs names that the record does not have stops the seam
      Given Seam.cs:
        """
        using Plasm.CSharp;
        using Shop;

        [assembly: SeamData(typeof(Orders))]
        [assembly: SeamField(typeof(Order), "Weight")]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam stops with SEAM003 at Seam.cs(5,12): "SeamField names Shop.Order.Weight, which is neither a field nor a public member of the record"
