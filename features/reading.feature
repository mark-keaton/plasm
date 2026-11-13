# Executable acceptance specification (Reqnroll): reading at most one row.
# Written before the implementation, following the conventions of core.feature.
# Added after the second architecture audit.
@implemented
Feature: Reading at most one row
  As an F# application developer
  I want repo.One to return the single row a query describes
  So that lookups mean exactly what the query says

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: One respects the query and reports too many rows as data

    Scenario: One keeps the query's own ordering and limit
      Given Orders for several Customers
      When I read One of a query ordered by total, descending, taking 1
      Then the Order with the largest total is returned

    Scenario: A query matching no rows returns None
      Given Orders for several Customers
      When I read One of a query that matches no Order
      Then None is returned

    Scenario: More than one matching row is reported as a property of the data
      Given Orders for several Customers
      When I read One of a query that matches several Orders
      Then the read returns an error saying more than one row matched
      And the error is not a translation error

    Scenario: Reading One of the same query twice plans it once
      Given Orders for several Customers
      When I read One of the same query twice
      Then it is translated once
