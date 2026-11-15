# Executable acceptance specification (Reqnroll): a query used as a condition
# inside another query. Written before the implementation.
@implemented
Feature: A query can ask whether another query finds anything
  As an F# application developer
  I want to filter one aggregate by what another aggregate holds
  So that a record refers to another by id and queries still reach across

  `exists (plasm { ... })` is true when the inner query finds a row. The inner
  query may read the outer row's fields and a prepared query's arguments, and it
  becomes a correlated EXISTS in the same command. It is a condition only: it
  cannot page, order, join or group.

  # Order = { Id: OrderId; Number: string; Total: decimal }
  # Payment = { Id: PaymentId; Order: OrderId; Amount: decimal; Succeeded: bool }
  Background:
    Given a configured relational database
    And orders and payments as separate aggregates
    And the Orders "A" for 10, "B" for 50 and "C" for 200
    And "A" has a succeeded payment of 10, and "B" has a failed payment of 50

  Rule: The inner query reads the outer row

    Scenario: Orders with a succeeded payment
      When I read the Orders with a succeeded payment
      Then the Orders read are "A"
      And one command is sent

    Scenario: Orders without a succeeded payment
      When I read the Orders without a succeeded payment
      Then the Orders read are "B" and "C"

    Scenario: An exists inside a larger condition
      When I read the Orders over 100 or with any payment
      Then the Orders read are "A", "B" and "C"

    Scenario: The inner query takes a prepared argument too
      When I read the Orders with a payment of at least 50, through a prepared query
      Then the Orders read are "B"

  Rule: It is checked like any query

    Scenario: A module's queries that use exists are checked
      When I check the queries of a module whose exists queries translate
      Then no problem is reported

    Scenario: An inner query that cannot be translated is reported
      When I check the queries of a module whose exists query cannot be translated
      Then one problem is reported, for the query "untranslatable", saying why it cannot be translated

    Scenario: An inner query that pages is refused
      When I read the Orders with an exists query that takes one row
      Then the read is refused, saying an exists query is a condition only
