# Executable acceptance specification (Reqnroll): explicit joins.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Explicit, typed joins
  As an F# application developer
  I want to combine rows from several mapped tables in one query
  So that I can filter, sort, page and project across relationships without client-side work

  Joins complement relationship loading: a join returns combined rows from one
  command, while preload returns immutable graphs from batched commands.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Joins are explicit, typed, and run in the database

    Scenario: An inner join returns matching pairs
      Given Orders with and without a Customer
      When I join Orders to Customers on the customer key
      Then each result pairs an Order with its Customer
      And Orders without a Customer are not returned
      And the join runs as a single database command

    Scenario: A left join represents a missing match as None
      Given Orders with and without a Customer
      When I left join Orders to Customers on the customer key
      Then every Order is returned exactly once
      And an Order without a Customer is paired with None
      And an Order with a Customer is paired with Some Customer

    Scenario: Join keys are checked at compile time
      When I attempt to join on keys of different types
      Then the join is rejected at compile time

    Scenario: Filters on joined columns run in the database
      Given Orders for several Customers
      When I join Orders to Customers and filter on the Customer name
      Then the filter is part of the executed SQL
      And only matching rows are transferred

    Scenario: Left-joined rows are filtered with option semantics
      Given Orders with and without a Customer
      When I keep only left-joined Orders whose Customer is None
      Then only Orders without a Customer are returned
      When I keep only left-joined Orders whose Customer satisfies a predicate
      Then only Orders whose Customer satisfies it are returned

  Rule: Joins preserve relational meaning

    Scenario: A join returns one row per matching pair
      Given an Order with 3 Payments
      When I join Orders to Payments
      Then the Order appears once for each Payment
      And filtering through a relationship predicate still returns the Order once

    Scenario: Pagination over joined rows is stable
      Given joined rows that tie on the requested ordering
      When I page through the joined rows
      Then the ordering includes the key of every joined source
      And the pages are disjoint and together complete
      And paging a join without an ordering is rejected

    Scenario: Several joins compose in one query
      Given Orders with OrderLines for several Products
      When I join Orders to OrderLines and OrderLines to Products
      Then each result combines an Order, a line and its Product
      And the joins run as a single database command

    Scenario: Projections over joins retrieve only the requested columns
      Given Orders with and without a Customer
      When I project Order identifiers with left-joined Customer names
      Then only the projected columns are retrieved
      And a missing Customer name is represented as None

  Rule: Joined queries are values

    Scenario: Joined queries compose through ordinary functions
      Given a joined query of Orders and Customers
      When a function adds a filter over both sources
      Then the original joined query is unchanged
      And the derived query applies the filter in the database

    Scenario: Joined queries can be inspected without database access
      Given a joined query of Orders and Customers
      When I inspect its execution plan
      Then the plan shows the join and its condition
      And inspection performs no database reads or writes

    Scenario: Unsupported join requests are explicit
      When I join on an expression that cannot be translated
      Then execution fails with the unsupported expression identified
      And no database command is executed
      When I request relationship loads on a joined query
      Then the request is rejected with an explanation
      And no database command is executed

    Scenario: Joined queries run inside transactions
      Given Orders with and without a Customer
      When a transaction step reads a joined query
      Then the step receives the joined rows
