Feature: A record a data method takes is a C# record, made back into the F# one
  A data method may take an F# record, the request it is asked: a booking's
  who, from and until. C# is given a record of the same fields, its JSON is
  in the seam's context, and the seam makes the F# record from it, field by
  field, so an endpoint passes its request body whole and declares no record
  of its own. A record first met as a parameter keeps its F# name; one first
  met as an answer is a view (EquipmentView). Only a record C# can give whole
  can be a parameter: a field C# cannot give, such as a validated value made
  only by its create, stops the seam, naming it.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System
      open System.Threading.Tasks
      open Plasm.CSharp

      type OrderId = OrderId of Guid

      type Line = { Part: string; Quantity: int }

      /// An order asked for.
      type NewOrder = { Customer: string; Note: string option; Lines: Line list }

      type Order = { Id: OrderId; Customer: string; Note: string option; Lines: Line list }

      [<Sealed>]
      type Orders() =
          member _.AddOrderAsync(order: NewOrder) : Task<AddResult<Order>> =
              Task.FromResult(
                  AddResult<Order>(
                      { Id = OrderId(Guid.Parse "01900000-0000-7000-8000-000000000001")
                        Customer = order.Customer
                        Note = order.Note
                        Lines = order.Lines }
                  )
              )
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Scenario: The request is a C# record of its fields, and the F# record is made from it
    When the seam is written into the namespace Shop.Api
    Then Views.g.cs declares "public sealed record NewOrder(string Customer, string? Note, LineView[] Lines);"
    And seam.lock holds
      """
      record NewOrder = Shop.NewOrder, and from C#
        Customer: string
        Note: string?
        Lines: LineView[]
      """
    And the host, run, prints "Ada, by the door, 4 bolt"
      """
      using Plasm.CSharp;

      namespace Shop.Api;

      public static class Host
      {
          public static string Run() =>
              new Orders(new Shop.Orders()).AddOrderAsync(new NewOrder("Ada", "by the door", [new LineView("bolt", 4)])).Result switch
              {
                  OrderView o => $"{o.Customer}, {o.Note}, {o.Lines[0].Quantity} {o.Lines[0].Part}",
                  Invalid i => "invalid",
              };
      }
      """

  Scenario: An endpoint takes the request whole: its JSON is in the seam's context, and nothing more is listed
    When the seam is written into the namespace Shop.Api
    Then SeamJson.g.cs declares "[JsonSerializable(typeof(NewOrder))]"
    And the C# compiles with the host:
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.AspNetCore.Http;

      namespace Shop.Api;

      public static class Endpoints
      {
          public static void Map(WebApplication app) =>
              app.MapPost("/orders", (NewOrder input, Orders d) => Results.Ok());
      }
      """

  Scenario: A record only answered is made back into nothing
    When the seam is written into the namespace Shop.Api
    Then SeamMap.g.cs declares "public static global::Shop.NewOrder Domain(this NewOrder value) => new("
    And SeamMap.g.cs does not declare "Domain(this OrderView"

  Scenario: A record C# cannot give whole cannot be a parameter, and the seam says which field
    Given the F# data project:
      """
      namespace Shop

      open System.Threading.Tasks
      open Plasm.CSharp

      type Customer =
          private
          | Customer of string

          member c.Value = let (Customer v) = c in v

      type NewOrder = { Customer: Customer; Note: string }

      type Order = { Note: string }

      [<Sealed>]
      type Orders() =
          member _.AddOrderAsync(order: NewOrder) : Task<AddResult<Order>> = Task.FromResult(AddResult<Order>({ Note = order.Note }))
      """
    When the seam is written into the namespace Shop.Api
    Then the seam stops with SEAM010: "Shop.Orders.AddOrderAsync's parameter order: Shop.NewOrder cannot be a parameter: NewOrder cannot be made from C#, so it cannot be a parameter: Customer cannot come from C# (Shop.Customer keeps its case private"
