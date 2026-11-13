# Executable acceptance specification (Reqnroll): counting, aggregates and grouping.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Counts, aggregates and grouping
  As an F# application developer
  I want totals, counts and grouped summaries computed by the database
  So that I never fetch rows only to count or add them up in memory

  Aggregates are expressions inside an ordinary typed query. They are computed
  by the database and return typed values; an aggregate over no rows has an
  explicit F# meaning.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Counting and existence never transfer rows

    Scenario: Counting a filtered query
      Given 5 published Orders and 2 draft Orders
      When I count the published Orders
      Then the count is 5
      And the count runs as one database command that transfers a single row
      And the filter is part of the counting SQL

    Scenario: Counting a page counts the page
      Given 5 published Orders and 2 draft Orders
      When I count a page of 3 published Orders after skipping 4
      Then the count is 1

    Scenario: Testing existence
      Given 5 published Orders and 2 draft Orders
      When I ask whether any cancelled Order exists
      Then the answer is false
      When I ask whether any draft Order exists
      Then the answer is true
      And each existence test reads at most one row

  Rule: Aggregates are typed values with F# meaning

    Scenario: Aggregating a filtered query
      Given 5 published Orders and 2 draft Orders
      When I select the count, sum, minimum, maximum and average of published Order totals
      Then each aggregate is computed by the database
      And the results are typed values that match the stored totals

    Scenario: Aggregates over no rows are explicit
      When I aggregate Order totals over a query that matches nothing
      Then the count is 0 and the sum is 0
      And the minimum, maximum and average are None

    Scenario: Aggregates are checked at compile time
      When I attempt to sum a text column or average an integer key
      Then the aggregate is rejected at compile time

  Rule: Grouping summarises in the database

    Scenario: Grouping Orders by Customer
      Given Customers with different numbers of Orders
      When I group Orders by Customer and select each Customer's count and total
      Then there is one result per Customer with Orders
      And each result has that Customer's count and total
      And the grouping runs as a single database command

    Scenario: Filtering groups
      Given Customers with different numbers of Orders
      When I keep only Customers with more than one Order
      Then only groups satisfying the condition are returned
      And the condition is applied by the database after grouping

    Scenario: Grouping across a join
      Given Customers with different numbers of Orders
      When I join Orders to Customers and group by the Customer name
      Then each Customer name is paired with its Order count
      And the results can be ordered by the aggregate

    Scenario: Grouping mistakes are explicit
      When I select a column that is neither grouped nor aggregated
      Then the query is rejected before execution with the column identified
      When I filter on an aggregate with where instead of having
      Then the query is rejected before execution with an explanation
      And no database command is executed
