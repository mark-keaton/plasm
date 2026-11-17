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

  # OrderStatus is declared Draft | Published | Cancelled and stored as the text
  # "draft", "published", "cancelled": alphabetical order would put cancelled first.
  Rule: Union cases stored as text compare in the order F# declares them

    Scenario Outline: Ordering by a union follows its declaration, <direction>
      Given Orders in every status
      When I order Orders by Status <direction>
      Then the Orders come in the order sorting them by Status in F# gives

      Examples:
        | direction  |
        | ascending  |
        | descending |

    Scenario Outline: Comparing a union with a case keeps F# meaning (<comparison>)
      Given Orders in every status
      When I keep Orders whose Status <comparison> Published
      Then the result equals evaluating the predicate over the same Orders in F#

      Examples:
        | comparison |
        | <          |
        | <=         |
        | >          |
        | >=         |

    Scenario: A prepared query compares a union argument in declaration order
      Given Orders in every status
      When I run a prepared query for Orders whose Status is before a given status, with Cancelled
      Then the Draft and Published Orders are returned

    Scenario: The earliest and latest case of a union follow its declaration
      Given Orders in every status
      When I select the minimum and maximum Status of the Orders
      Then they are Draft and Cancelled

    Scenario: The ordering is done by the database
      When I explain a query that orders Orders by Status
      Then the SQL orders by the position of each stored name in the declaration

  Rule: A null where F# has no absent value is refused, never read as IS NULL

    A string (or any reference type that is not an option) has no absent value
    in F#: `c.Name = name` with a null `name` is false for every Customer, while
    SQL's IS NULL would mean "has no name". An option says a value may be absent.

    Scenario Outline: A captured null compared with <op> is refused
      When I read the Customers whose name <op> a captured null string
      Then the read is refused, saying the value is null and an option says a value may be absent

      Examples:
        | op  |
        | =   |
        | <>  |

    Scenario: A null prepared argument is refused when the query runs
      When I run a prepared query for the Customers named an argument, with null
      Then the read is refused, saying the argument is null and an option says a value may be absent

  Rule: Clauses keep the order they are written in, or the query is refused

    SQL filters, groups and orders before it pages, and pages once. A clause
    written after `take` or `skip` that SQL would apply before it, a second
    `take` or `skip`, and a `where` after `groupBy` (whose rows are groups) mean
    something else in F# than in SQL, so Plasm refuses them rather than reorder
    them. `skip` then `take` reads as F# reads it.

    Scenario Outline: <clauses> is refused
      When I read the Orders, written with <clauses>
      Then the read is refused, saying <reason>

      Examples:
        | clauses                 | reason                                      |
        | take 2, then where      | where is written after take                 |
        | skip 1, then where      | where is written after skip                 |
        | take 2, then orderBy    | orderBy is written after take               |
        | take 2, then take 3     | take is written twice                       |
        | skip 1, then skip 2     | skip is written twice                       |
        | take 2, then skip 1     | skip is written after take                  |
        | groupBy, then where     | where is written after groupBy; use having  |
        | take 2, then groupBy    | groupBy is written after take               |

    Scenario: skip, then take, is read as F# reads it
      Given Orders for several Customers
      When I read the Orders by id with skip 1, then take 2
      Then the second and third Orders by id are read, as F# pages them

  Rule: String functions trim what .NET trims

    `Trim`, `TrimStart` and `TrimEnd` remove every character .NET counts as
    whitespace (`Char.IsWhiteSpace`): tabs, line breaks, a no-break space and
    the other Unicode spaces, not only the space character.

    Scenario Outline: <function> trims as .NET does
      Given Customers whose names are padded with spaces, tabs, line breaks and Unicode spaces
      When I read the Customers' names through <function>
      Then each name read equals <function> of the stored name in F#

      Examples:
        | function  |
        | Trim      |
        | TrimStart |
        | TrimEnd   |
