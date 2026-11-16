# Executable acceptance specification (Reqnroll): distinct values. Written
# before the implementation.
@implemented
Feature: A query can return each selected value once
  As an F# application developer
  I want the distinct values a query selects, computed by the database
  So that I do not read every row to find the few values they hold

  A query ending in `selectDistinct (...)` returns each selected value once, as
  `List.distinct` would: None is one value, and a tuple is distinct as a tuple.
  The order must be by values it selects (the database can only order distinct
  rows by what they hold). `take` and `skip` are refused with it: in the query
  they come before `selectDistinct`, so they would page the rows, but the
  database pages after removing duplicates. `Agg.countDistinct` counts the
  distinct values that are not None, as SQL's COUNT(DISTINCT ...) does.

  # Order = { Id: OrderId; CustomerId: CustomerId option; Status: OrderStatus; Total: decimal; ... }
  Background:
    Given a configured relational database
    And the Customers "Ada" and "Bob" for distinct values
    And the Orders, by customer, status and total:
      | customer | status    | total |
      | Ada      | Published | 10    |
      | Ada      | Published | 20    |
      | Ada      | Draft     | 5     |
      | Bob      | Published | 30    |
      |          | Draft     | 7     |

  Rule: Each selected value comes once

    Scenario: The statuses the Orders have
      When I select the distinct statuses of the Orders, ordered by status
      Then the values are Draft and Published
      And one command is sent, selecting distinct rows

    Scenario: No customer is one value, as List.distinct has it
      When I select the distinct customers of the Orders
      Then the values are Ada, Bob and no customer, in any order

    Scenario: A pair is distinct as a pair
      When I select the distinct pairs of customer and status of the Orders
      Then there are 4 pairs

    Scenario: Over a join, the Customers with a published Order, each once
      When I select the distinct names of the Customers of published Orders, ordered by name
      Then the values are Ada and Bob

    Scenario: A prepared query's arguments bind as usual
      When I select the distinct statuses of the Orders of at least 8, through a prepared query
      Then the values are Published

    Scenario: A module's distinct queries are checked
      When I check the queries of a module whose distinct queries translate
      Then no problem is reported

  Rule: What the database cannot do faithfully is refused

    Scenario: Ordering by a value not selected
      When I select the distinct statuses of the Orders, ordered by total
      Then the read is refused, naming the Total it is ordered by

    Scenario: Paging distinct values
      When I select the distinct statuses of the first two Orders
      Then the read is refused, saying take and skip cannot be used with selectDistinct

    Scenario: A whole record is distinct by its key
      When I select the distinct Orders
      Then the read is refused, saying a whole record is unique by its key

  Rule: Distinct values can be counted

    Scenario: Counting the customers who have Orders
      When I count the distinct customers of the Orders
      Then the count is 2

    Scenario: Counting per group
      When I count the distinct customers of the Orders by status
      Then the counts are Draft 1 and Published 2
