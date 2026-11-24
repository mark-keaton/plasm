Feature: A request body no JSON context lists stops the build
  The seam's JSON context is source-generated, as a native binary needs, so
  ASP.NET reads a request body only of a type some context lists. A record an
  endpoint takes as its body that none lists would build, start, and answer
  every request to the endpoint with a 500 ("JsonTypeInfo metadata for type
  ... was not provided"). Plasm.CSharp's analyzer stops the build instead
  (SEAM030), naming the type, the endpoint, and where to list it.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      [<Sealed>]
      type Orders() =
          member _.AddOrderAsync(customer: string, cancellationToken: CancellationToken) : Task<Added<Order>> =
              Task.FromResult(Added<Order>({ Customer = customer; Version = 1 }))
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Scenario: A record taken as a POST's body, listed nowhere, stops the build at the parameter
    When the seam is written into the namespace Shop.Api
    Then the C# with the host stops with SEAM030 at Host.cs(9,42): "NewOrder is the body of POST /orders, and no JSON context lists it, so every such request would answer 500: list it in Seam.cs, [assembly: SeamJson(typeof(NewOrder))]"
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.AspNetCore.Http;

      namespace Shop.Api;

      public static class Endpoints
      {
          public static void Map(WebApplication app) =>
              app.MapPost("/orders", (NewOrder input, Orders d, CancellationToken ct) =>
                  Results.Ok());
      }

      public sealed record NewOrder(string Customer);
      """

  Scenario: Listed in Seam.cs, it builds
    Given Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      [assembly: SeamJson(typeof(Shop.Api.NewOrder))]
      """
    When the seam is written into the namespace Shop.Api
    Then the C# compiles with the host:
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.AspNetCore.Http;

      namespace Shop.Api;

      public static class Endpoints
      {
          public static void Map(WebApplication app) =>
              app.MapPost("/orders", (NewOrder input, Orders d, CancellationToken ct) =>
                  Results.Ok());
      }

      public sealed record NewOrder(string Customer);
      """

  Scenario: Route values, services, the request's own parameters and a GET's record are no body
    When the seam is written into the namespace Shop.Api
    Then the C# compiles with the host:
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.AspNetCore.Http;
      using Microsoft.AspNetCore.Mvc;

      namespace Shop.Api;

      public static class Endpoints
      {
          public static void Map(WebApplication app)
          {
              app.MapPost("/orders/{id:guid}", (Guid id, int? page, Orders d, HttpContext http, CancellationToken ct) => Results.Ok());
              app.MapGet("/orders", ([AsParameters] Paging paging, Orders d) => Results.Ok());
              app.MapPut("/orders/{id:guid}", ([FromServices] Clock clock, Guid id) => Results.Ok());
          }
      }

      public sealed record Paging(int Page, int Size);
      public sealed record Clock(DateTimeOffset Now);
      """

  Scenario: A class marked as the body is a body too
    When the seam is written into the namespace Shop.Api
    Then the C# with the host stops with SEAM030 at Host.cs(10,69): "Rename is the body of PUT /orders/{id:guid}, and no JSON context lists it"
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.AspNetCore.Http;
      using Microsoft.AspNetCore.Mvc;

      namespace Shop.Api;

      public static class Endpoints
      {
          public static void Map(WebApplication app) =>
              app.MapPut("/orders/{id:guid}", (Guid id, [FromBody] Rename input) => Results.Ok());
      }

      public sealed class Rename { public string Customer { get; set; } = ""; }
      """
