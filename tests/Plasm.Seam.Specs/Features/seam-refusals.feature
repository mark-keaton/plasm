Feature: A domain's refusal reaches C# as Refused, its code the case's name
  A change that the rules can refuse answers Changed<'T, 'R> in F#, 'R the
  domain's own refusal: an F# union whose `Message` says why in the domain's
  words. The seam gives C# a Changed of the record's view, and the refusal a
  Refused whose Code is the name of its case, known when the seam is written,
  so nothing is read by reflection at run time, natively or not. No project
  writes a match from its refusal to a code.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      type OrderRefusal =
          | NotDraft
          | QuantityNotPositive of quantity: int

          member r.Message =
              match r with
              | NotDraft -> "only a draft order can change"
              | QuantityNotPositive q -> $"a quantity of {q} is not positive"

      [<Sealed>]
      type Orders() =
          member _.PlaceAsync(refuse: bool, cancellationToken: CancellationToken) : Task<Changed<Order, OrderRefusal>> =
              Task.FromResult(
                  if refuse then Changed<Order, OrderRefusal>(QuantityNotPositive 0)
                  else Changed<Order, OrderRefusal>({ Customer = "Ada"; Version = 2 })
              )

          member _.CancelAsync(cancellationToken: CancellationToken) : Task<Changed<Order, OrderRefusal>> =
              Task.FromResult(Changed<Order, OrderRefusal>(NotDraft))
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Scenario: C# is given Changed of the view, the refusal Refused with its case's name and its Message
    When the seam is written into the namespace Shop.Api
    Then seam.lock holds
      """
      refusal Shop.OrderRefusal
        NotDraft
        QuantityNotPositive
      class Orders = Shop.Orders
        Task<Changed<OrderView>> PlaceAsync(bool refuse, CancellationToken cancellationToken)
        Task<Changed<OrderView>> CancelAsync(CancellationToken cancellationToken)
      """
    And the host, run, prints "refused QuantityNotPositive: a quantity of 0 is not positive; refused NotDraft: only a draft order can change; changed Ada"
      """
      using Plasm.CSharp;

      namespace Shop.Api;

      public static class Host
      {
          static string Say(Changed<OrderView> outcome) => outcome switch
          {
              OrderView o => $"changed {o.Customer}",
              Missing m => $"missing {m.What}",
              Refused r => $"refused {r.Code}: {r.Message}",
              Stale s => "stale",
              Invalid i => "invalid",
          };

          public static string Run()
          {
              var orders = new Orders(new Shop.Orders());
              return string.Join("; ",
                  Say(orders.PlaceAsync(true, default).Result),
                  Say(orders.CancelAsync(default).Result),
                  Say(orders.PlaceAsync(false, default).Result));
          }
      }
      """

  Scenario: A refusal without a Message stops the seam, saying what to write
    Given the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      type OrderRefusal =
          | NotDraft

      [<Sealed>]
      type Orders() =
          member _.CancelAsync(cancellationToken: CancellationToken) : Task<Changed<Order, OrderRefusal>> =
              Task.FromResult(Changed<Order, OrderRefusal>(NotDraft))
      """
    When the seam is written into the namespace Shop.Api
    Then the seam stops with SEAM013: "Shop.Orders.CancelAsync's result: Shop.OrderRefusal is the refusal of a Changed, and has no Message: write `member r.Message = ...` on it"

  Scenario: A refusal that is no union stops the seam
    Given the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      [<Sealed>]
      type Orders() =
          member _.CancelAsync(cancellationToken: CancellationToken) : Task<Changed<Order, string>> =
              Task.FromResult(Changed<Order, string>("no"))
      """
    When the seam is written into the namespace Shop.Api
    Then the seam stops with SEAM013: "Shop.Orders.CancelAsync's result: string is the refusal of a Changed, and a refusal is a public F# union"
