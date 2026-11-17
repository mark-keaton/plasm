# Executable acceptance specification (Reqnroll): prepared (compiled) queries.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Prepared queries
  As an F# application developer
  I want to describe a query once and run it with different arguments
  So that hot paths do not rebuild and re-plan the same query on every call

  A prepared query is a function from typed arguments to a query value. The
  description is built and translated once; each call only binds arguments.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Prepared queries are built once and bound per call

    Scenario: A prepared query is built once and runs with different arguments
      Given Orders for several Customers
      When I prepare a query for the Orders of a Customer
      And I run it for each Customer
      Then each run returns that Customer's Orders
      And the query description was built once
      And every run sends the same SQL text with different parameter values

    Scenario: Arguments are typed and always parameters
      Given Orders for several Customers
      When I prepare a query with an optional Customer argument and a list of statuses
      Then running it with Some Customer returns that Customer's matching Orders
      And running it with None returns the matching Orders without a Customer
      And both runs send the same SQL text
      And the optional argument is compared as a value or as absent, so an index on the column can serve it

    Scenario: Paging arguments
      Given Orders for several Customers
      When I prepare a paged query whose page size and offset are arguments
      Then each page is bound from the arguments
      And the pages together contain every Order exactly once

    Scenario: Prepared queries load what an aggregate owns
      Given Orders for several Customers with Payments
      When I prepare a query for the Orders of a Customer, which own their OrderLines
      Then each run loads the OrderLines with batched commands

    Scenario: Prepared queries can be inspected and counted
      Given Orders for several Customers
      When I prepare a query for the Orders of a Customer
      Then its plan can be inspected for an argument without database access
      And it can be counted for an argument

  Rule: Argument misuse is explicit

    Scenario: Arguments are only read inside query operations
      When a prepared query reads an argument while it is being built
      Then preparation fails with an explanation

    Scenario: Arguments cannot choose the ordering
      When a prepared query orders by an argument
      Then preparation fails with an explanation

  Rule: Prepared queries compose like any other query

    Scenario: Extending a prepared query applies the extension everywhere
      Given Orders for several Customers
      When I prepare a query for the Orders of a Customer
      And I extend it with a filter, an ordering and a limit for one Customer
      Then reading, counting and explaining the extended query all apply the extension
      And the prepared query on its own is unchanged

    Scenario: An extended prepared query still binds its arguments per call
      Given Orders for several Customers
      When I prepare a query for the Orders of a Customer
      And I extend it with a filter and run it for two different Customers
      Then each run returns only that Customer's Orders that satisfy the filter
      And the two runs send one SQL text

    Scenario: A prepared query over another aggregate binds its own arguments
      Given Orders for several Customers with Payments
      When I read the Payments of the Orders I hold with a prepared query bound to a status
      Then each Order has only the Payments with that status
      And no exception is thrown
