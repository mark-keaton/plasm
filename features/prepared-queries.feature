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

    Scenario: Paging arguments
      Given Orders for several Customers
      When I prepare a paged query whose page size and offset are arguments
      Then each page is bound from the arguments
      And the pages together contain every Order exactly once

    Scenario: Prepared queries keep relationship loads
      Given Orders for several Customers with Payments
      When I prepare a query that preloads Payments
      Then each run loads the Payments with batched commands

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
