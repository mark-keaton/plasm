Feature: Missing and Stale name the record the same way, and Seam.cs can give it another word
  A read that misses, and a change that misses, need no name for what is
  missing: Plasm.CSharp takes the record type's name, as Stale does, so a 404
  and a 409 about one record say the same ({"what":"Order"}). Where the JSON
  is to say another word, Seam.cs says it once, beside the view's name
  (SeamView What), and C# is given Missing and Stale with that word.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System.Threading.Tasks
      open Plasm
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      type OrderRefusal =
          | NotDraft

          member r.Message = "only a draft order can change"

      [<Sealed>]
      type Orders() =
          member _.OrderAsync() : Task<FindResult<Order>> =
              Task.FromResult(Outcome.found (Ok None: Result<Order option, DbError>))

          member _.ShipAsync() : Task<ChangeResult<Order, OrderRefusal>> =
              Task.FromResult(ChangeResult<Order, OrderRefusal>({ What = "Order"; Key = [| "7" |] }: Stale))
      """

  Scenario: Without a word, Missing names the record type, as Stale does
    Given Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """
    When the seam is written into the namespace Shop.Api
    Then the host, run, prints "missing Order; stale Order"
      """
      using Plasm.CSharp;

      namespace Shop.Api;

      public static class Host
      {
          public static string Run()
          {
              var orders = new Orders(new Shop.Orders());
              var read = orders.OrderAsync().Result switch { OrderView o => o.Customer, Missing m => "missing " + m.What };
              var ship = orders.ShipAsync().Result switch
              {
                  OrderView o => o.Customer,
                  Missing m => "missing " + m.What,
                  Refused r => r.Code,
                  Stale s => "stale " + s.What,
                  Invalid i => "invalid",
              };
              return read + "; " + ship;
          }
      }
      """

  Scenario: SeamView What gives the record another word, for Missing and Stale alike
    Given Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      [assembly: SeamView(typeof(Shop.Order), What = "order")]
      """
    When the seam is written into the namespace Shop.Api
    Then seam.lock holds
      """
      record OrderView = Shop.Order, Missing and Stale say "order"
      """
    And the host, run, prints "missing order; stale order"
      """
      using Plasm.CSharp;

      namespace Shop.Api;

      public static class Host
      {
          public static string Run()
          {
              var orders = new Orders(new Shop.Orders());
              var read = orders.OrderAsync().Result switch { OrderView o => o.Customer, Missing m => "missing " + m.What };
              var ship = orders.ShipAsync().Result switch
              {
                  OrderView o => o.Customer,
                  Missing m => "missing " + m.What,
                  Refused r => r.Code,
                  Stale s => "stale " + s.What + " " + s.Key[0],
                  Invalid i => "invalid",
              };
              return read + "; " + ship.Replace(" 7", "");
          }
      }
      """
