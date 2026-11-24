Feature: seam.lock's format is defined, read and written by one module, and versioned
  seam.lock's format is docs/seam-lock.md: a grammar, an example of each of
  its productions, and lines it refuses. One module of the seam (LockFile)
  holds the format's model, reads a lock into it, writes it from it, and
  names what changed between two of them; nothing else in the seam formats or
  splits lock text. Every lock these scenarios write is read back and written
  again, and must come out the same. The first line that is not a comment
  names the format's version: a lock of an older one is read and written
  again, one of a newer one is refused.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System.Threading.Tasks
      open Plasm.CSharp

      type Order = { Customer: string; Version: int }

      [<Sealed>]
      type Orders() =
          member _.OrderAsync() : Task<FindResult<Order>> = Task.FromResult(FindResult<Order>({ Customer = "Ada"; Version = 1 }))
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Rule: The grammar and the reader say the same

    Scenario: Every example in the grammar is read, every refused line is refused, and every production has an example
      Then each example in docs/seam-lock.md is read as a lock, each refused one is refused, and each of its productions has an example

  Rule: A lock is written in the current format, and read back as it was written

    Scenario: The version is the first line that is not a comment, and the header names the format's page
      When the seam is written into the namespace Shop.Api
      Then seam.lock holds
        """
        # (and docs/seam-lock.md in Plasm.CSharp's package).

        seam.lock 1
        record OrderView = Shop.Order
        """
      And seam.lock holds
        """
        # -p:PlasmSeamDrift=fail) stops instead. Its format: https://mark-keaton.github.io/plasm/docs/seam-lock.html
        """

  Rule: Versions

    Scenario: A lock of the format before versions is the same contract: a CI build passes, and another writes it again in the current format
      Given the seam is written into the namespace Shop.Api
      And seam.lock is written as it was before versions
      When the seam is written into the namespace Shop.Api, as a CI build writes it
      Then the seam wrote its files, and left seam.lock as it was
      When the seam is written into the namespace Shop.Api
      Then seam.lock holds
        """
        seam.lock 1
        """

    Scenario: A lock of a newer format is refused, naming both versions
      Given the seam is written into the namespace Shop.Api
      And seam.lock's version is 2
      When the seam is written into the namespace Shop.Api
      Then the seam stops with SEAM021 at seam.lock(7,1): "seam.lock 2 is a format newer than this Plasm.Seam reads (1)"

  Rule: A line the format does not take is refused, naming it

    Scenario: In CI, a lock with a line the grammar does not take stops the build, naming the line
      Given the seam is written into the namespace Shop.Api
      And seam.lock gains the line "  Customer string" after "record OrderView = Shop.Order"
      When the seam is written into the namespace Shop.Api, as a CI build writes it
      Then the seam stops with SEAM021 at seam.lock: "seam.lock is not a lock (line 9: \"  Customer string\" is no member line of record OrderView)"

    Scenario: Elsewhere, such a lock is written again, saying which line it could not read
      Given the seam is written into the namespace Shop.Api
      And seam.lock gains the line "  Customer string" after "record OrderView = Shop.Order"
      When the seam is written into the namespace Shop.Api
      Then the seam rewrote seam.lock, saying "seam.lock was not a lock, line 9: \"  Customer string\" is no member line of record OrderView, and is written again"

  Rule: What a change is reported as

    Scenario: A header whose clause changes is reported as changed, from and to
      Given the seam is written into the namespace Shop.Api
      And Seam.cs:
        """
        using Plasm.CSharp;

        [assembly: SeamData(typeof(Shop.Orders))]
        [assembly: SeamView(typeof(Shop.Order), What = "order")]
        """
      When the seam is written into the namespace Shop.Api
      Then the seam rewrote seam.lock, saying "changed: record OrderView = Shop.Order, now record OrderView = Shop.Order, Missing and Stale say \"order\""
