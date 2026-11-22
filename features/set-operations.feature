# Executable acceptance specification (Reqnroll): set operations over two
# queries, and a query as a join's source. Written before the implementation.
@implemented
Feature: Two queries can be combined, and a query can be joined
  As an F# application developer whose reads combine two questions
  I want a union, an intersection or a difference of two queries, and to join a query
  So that one command answers them, as SQL would

  Query.union (a, b), Query.unionAll, Query.intersect and Query.except combine
  two queries of the same row type: two queries of records, or two
  projections of one type. Of records, the result is a query like any other:
  `for c in Query.union (a, b) do` filters, orders and pages it, and its
  records are read whole, what they own and link loaded after them; the set
  operation is its source, `FROM (... UNION ...) AS "c"`. Ordering and paging
  come after the set operation, so an operand that orders or pages itself is
  refused by name, and a page is a page of the combined rows. Of projections,
  the result is the projection's rows, read in no order. A record's version is
  a column like the others: two copies of a record at different versions are
  two rows. A set operation's rows cannot be locked.

  A join takes a query where it takes a table: `join c in (plasm { ... }) on
  (...)` joins the query's rows, as a subquery named by the join's variable.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And the Customers Ada, Bob and Cy, with Orders for Ada and Bob

  Rule: Two queries of records combine into a query

    Scenario Outline: <operation> of the Customers named Ada or Bob and those with an Order
      When I read the <operation> of the Customers named Ada or Bob and the Customers with an Order, ordered by name
      Then the combined Customers read are <names>
      And one command read them, its source the set operation

      Examples:
        | operation | names            |
        | union     | Ada, Bob         |
        | unionAll  | Ada, Ada, Bob, Bob |
        | intersect | Ada, Bob         |
        | except    | (none)           |

    Scenario: A union is filtered, ordered and paged after it is formed
      When I read the first Customer by name of the union of those named Bob or Cy and those with an Order
      Then the combined Customers read are Ada
      And its ORDER BY and LIMIT come after the union

    Scenario: The records of a union are read whole
      Given Ada's Order has a Line
      And Cy has an Order too
      When I read the union of Ada's Orders and Bob's Orders
      Then Ada's and Bob's Orders are read, not Cy's
      And Ada's Order is read with its Line

    Scenario Outline: <what> is refused
      When I read <query>
      Then the combined read is refused, saying "<message>"

      Examples:
        | what                          | query                                                   | message                                       |
        | An operand that orders itself | the union of the Customers ordered by name and the rest | order and page the combined rows, not an operand |
        | An operand that pages itself  | the union of the first Customer and the rest            | order and page the combined rows, not an operand |

    @postgres
    Scenario: A locked union is refused
      When I read the union of two Customer queries, locked for update
      Then the combined read is refused, saying "a set operation's rows cannot be locked"

    @sqlite
    Scenario: A locked union is refused on SQLite
      When I read the union of two Customer queries, locked for update
      Then the combined read is refused, saying "Provider SQLite does not support row locks"

  Rule: Two projections combine into their rows

    Scenario: A union of two projections of names gives each name once
      When I read the union of the names of the Customers with an Order and the names of those named Ada
      Then the names read, in any order, are Ada and Bob

  Rule: A join takes a query

    Scenario: Orders joined with a query of Customers
      When I read the Orders joined with the Customers named Ada, as a query
      Then only Ada's Orders are read, with her name
      And the join's source is the query, named by its variable
