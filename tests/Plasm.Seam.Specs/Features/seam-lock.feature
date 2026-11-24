Feature: seam.lock holds the contract, and a CI build does not change it
  seam.lock, beside the host and committed with it, describes the contract
  the seam writes: each view's fields, each union's cases and kinds, each
  refusal's codes, each member of the C# class, and every type the JSON
  context lists. A build that changes the contract rewrites seam.lock and
  says, change by change, what changed. A build in CI (ContinuousIntegrationBuild,
  or PlasmSeamDrift=fail) does not: it stops, naming each change, so no change
  to what C# is given reaches main unseen.

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
          member _.OrderAsync(cancellationToken: CancellationToken) : Task<FindResult<Order>> =
              Task.FromResult(FindResult<Order>({ Customer = "Ada"; Version = 1 }))
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Scenario: The first build writes seam.lock
    When the seam is written into the namespace Shop.Api
    Then the seam wrote seam.lock
    And seam.lock holds
      """
      record OrderView = Shop.Order
        Customer: string
        Version: int
      """

  Scenario: A changed contract rewrites seam.lock, saying what changed
    Given the seam is written into the namespace Shop.Api
    And the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Note: string option; Version: int }

      [<Sealed>]
      type Orders() =
          member _.OrderAsync(cancellationToken: CancellationToken) : Task<FindResult<Order>> =
              Task.FromResult(FindResult<Order>({ Customer = "Ada"; Note = None; Version = 1 }))
      """
    When the seam is written into the namespace Shop.Api
    Then the seam rewrote seam.lock, saying "added to record OrderView = Shop.Order: Note: string?"
    And seam.lock holds
      """
      record OrderView = Shop.Order
        Customer: string
        Note: string?
        Version: int
      """

  Scenario: In CI, a changed contract stops the build, naming each change, and seam.lock is left as it was
    Given the seam is written into the namespace Shop.Api
    And the F# data project:
      """
      namespace Shop

      open System.Threading
      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string }

      [<Sealed>]
      type Orders() =
          member _.OrderAsync(cancellationToken: CancellationToken) : Task<FindResult<Order>> =
              Task.FromResult(FindResult<Order>({ Customer = "Ada" }))
      """
    When the seam is written into the namespace Shop.Api, as a CI build writes it
    Then the seam stops with SEAM020 at seam.lock: "the generated C# no longer matches seam.lock (1 change, below)"
    And it names the change "removed from record OrderView = Shop.Order: Version: int"
    And seam.lock holds
      """
      record OrderView = Shop.Order
        Customer: string
        Version: int
      """

  Scenario: In CI, an unchanged contract builds
    Given the seam is written into the namespace Shop.Api
    When the seam is written into the namespace Shop.Api, as a CI build writes it
    Then the seam wrote its files, and left seam.lock as it was

  Scenario: In CI, a host without seam.lock stops the build: there is nothing to check the contract against
    When the seam is written into the namespace Shop.Api, as a CI build writes it
    Then the seam stops with SEAM020 at seam.lock: "seam.lock is not there, and PlasmSeamDrift=fail writes none"
