# Executable acceptance specification (Reqnroll): query meaning at the edges.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit, which found both rules violated.
@implemented
Feature: Queries keep their F# meaning
  As an F# application developer
  I want a query to return what the same predicate would return over F# values
  So that I can reason about queries as F# code, including absent values and captured data

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Negation follows F# meaning, including for absent values

    Scenario: Negating an optional equality keeps rows whose value is absent
      Given Orders with and without a Customer
      When I keep Orders that are not for a given Customer, written with not
      Then the Orders without a Customer are included
      And the result equals evaluating the predicate over the same Orders in F#

    Scenario Outline: not (a = b) and a <> b return the same rows
      Given Orders with and without a Customer and with and without a cancellation reason
      When I filter Orders on <field> with not and with <>
      Then both filters return the same Orders
      And both equal evaluating the predicate over the same Orders in F#

      Examples:
        | field              |
        | CustomerId         |
        | CancellationReason |
        | Status             |

    Scenario: Negating a compound predicate over absent values
      Given Orders with and without a Customer
      When I keep Orders for which "for a given Customer and published" is false
      Then the result equals evaluating the predicate over the same Orders in F#

    Scenario: Negating a comparison that cannot be absent stays a plain NOT
      When I explain a query that negates a comparison of a required field
      Then the SQL negates it with NOT, so an index on the field can still be used

  Rule: Captured values are read each time the query runs

    Scenario Outline: A query sees the current contents of a captured <container>
      Given Orders for several Customers
      When I build a query that filters on the statuses in a captured <container>
      And I run it, change the <container> and run the same query again
      Then the second run reflects the changed <container>

      Examples:
        | container    |
        | array        |
        | reference    |
        | ResizeArray  |

    Scenario: A query comparing with the current time reads the clock each run
      Given Orders for several Customers
      When I run a query comparing with DateTime.UtcNow twice, a moment apart
      Then each run sends the time at which it ran

    Scenario Outline: A query sees the current contents of a module-level <container>
      Given Orders for several Customers
      When I build a query that filters on the statuses in a module-level <container>
      And I run it, change the module-level <container> and run the same query again
      Then the second run reflects the changed <container>

      Examples:
        | container   |
        | array       |
        | ResizeArray |

    Scenario: A query capturing only immutable values is planned once
      Given Orders for several Customers
      When I run a query that captures only immutable values twice
      Then it is translated once
