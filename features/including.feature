# Executable acceptance specification (Reqnroll): reading other aggregates'
# records with a query, the result type saying what was read. Written before
# the implementation.
@implemented
Feature: A query can read each record with the records another query finds for it
  As an F# application developer
  I want to read each Order with its Payments in one query
  So that the result's type says what was read, with no second query and no
  matching by hand

  A query ending in `including (plasm { ... })` returns each record paired with
  the list the inner query finds for it: `Projection<Order * Payment list>`. The
  inner query refers to the outer record by one equality between an inner and an
  outer column, and may filter, order and take: all per outer record. Plasm reads
  the inner records with one more command per batch of outer values, and groups
  them. The inner query may end in `select` or `selectDistinct`, so the list
  holds only what it selects. The outer record still comes whole, with what it owns. A tie the typed ids
  prove is written `referring<Payment> o` (referring.feature); a relationship
  that says more than the tie is written once as a reflected function built on
  it (`succeededPaymentsOf o`) and used in `including` and in `exists` alike.

  `includingOne` reads the one record the inner query can find, as an option
  (`Order * Customer option`); the tie must prove there is at most one, by
  being the inner record's key or a [<Unique>] field, or the query is refused;
  a second match in the database is an error, never a choice.
  The two mix in any order. A query may end in up to sixteen `including`s and `includingOne`s, giving a flat tuple in the order
  written: `Projection<Order * Payment list * Order list>`. There is a limit
  only because F# has no variadic generics, so each length of tuple is one more
  overload of `including`; sixteen is where they stop. An inner query may
  itself end in `including`, so its items come with their own lists:
  `Projection<Order * (Order * Payment list) list>`. Each `including`, at any
  depth, is one more command per batch, so lists never multiply each other's
  rows, and each one's condition, order and take apply per record of its own
  level.

  # Order = { Id: OrderId; CustomerId: CustomerId option; Total: decimal; ...; Lines: OrderLine list }
  # Payment = { Id: PaymentId; OrderId: OrderId; Amount: decimal; Status: PaymentStatus; CreatedAt: DateTime }
  Background:
    Given a configured relational database
    And the Customers "Ada" and "Bob"
    And Ada's Orders of 10 and 50, Bob's Order of 200, and an Order of 5 with no Customer
    And the Order of 10 has one line
    And the Payments, by order and minute:
      | order | amount | status    | minute |
      | 10    | 10     | Succeeded | 1      |
      | 10    | 10     | Failed    | 2      |
      | 50    | 20     | Succeeded | 1      |
      | 50    | 30     | Succeeded | 3      |
      | 50    | 5      | Pending   | 5      |

  Rule: Each record comes with the list the inner query finds for it

    Scenario: Each Order with its Payments
      When I read the Orders by total, including their Payments by minute
      Then the Orders and their Payments are:
        | order | payments                            |
        | 5     |                                     |
        | 10    | 10 Succeeded, 10 Failed             |
        | 50    | 20 Succeeded, 30 Succeeded, 5 Pending |
        | 200   |                                     |
      And the Order of 10 still has its line

    Scenario: The inner records are read with one more command, however many Orders
      When I read the Orders by total, including their Payments by minute
      Then 3 commands are sent: the Orders, their lines, and the Payments
      And the Payments command takes the Orders' ids as one parameter
      And the plan lists the Payments as one more load

    Scenario: The result's type says what was read
      When I compile code that passes the Orders read with their Payments to a function of Order * Payment list
      Then the code compiles
      When I compile code that passes the Orders read without them to that function
      Then the code does not compile

    Scenario: A relationship that says more than the tie is written once and serves including and exists
      When I read the Orders by total, including what succeededPaymentsOf finds for each
      Then the Orders and their Payments are:
        | order | payments                  |
        | 5     |                           |
        | 10    | 10 Succeeded              |
        | 50    | 20 Succeeded, 30 Succeeded |
        | 200   |                           |
      When I read the Orders that succeededPaymentsOf finds something for, in exists
      Then the Orders found are those of 10 and 50

    Scenario: Inside a transaction
      When a transaction reads the Orders by total, including their Payments by minute
      Then the Order of 50 comes with 3 Payments

  Rule: Conditions, order and take apply per outer record

    Scenario: The newest Payment of each Order
      When I read the Orders by total, including each one's newest Payment
      Then the Orders and their Payments are:
        | order | payments    |
        | 5     |             |
        | 10    | 10 Failed   |
        | 50    | 5 Pending   |
        | 200   |             |

    Scenario: Taking without an order is refused
      When I read the Orders including two of their Payments in no order
      Then the read is refused, saying the inner query must be ordered to take

    Scenario: The outer column need not be the key
      When I read the Orders by total, including the Orders of the same Customer
      Then the Orders and the totals of the Orders they include are:
        | order | included |
        | 5     |          |
        | 10    | 10, 50   |
        | 50    | 10, 50   |
        | 200   | 200      |

    Scenario: The inner query refers to the outer record only by one equality
      When I read the Orders including the Payments made after the Order was placed
      Then the read is refused, naming the condition that refers to the Order

  Rule: The inner query may select what the list holds

    Scenario: Each Order with the amounts of its Payments
      When I read the Orders by total, including the amounts of their Payments by minute
      Then the Orders and the values they include are:
        | order | values    |
        | 5     |           |
        | 10    | 10, 10    |
        | 50    | 20, 30, 5 |
        | 200   |           |
      And the Payments command reads only the amount and the Order it belongs to

    Scenario: The newest amount of each Order
      When I read the Orders by total, including the amount of each one's newest Payment
      Then the Orders and the values they include are:
        | order | values |
        | 5     |        |
        | 10    | 10     |
        | 50    | 5      |
        | 200   |        |

    Scenario: The distinct statuses of each Order's Payments
      When I read the Orders by total, including the distinct statuses of their Payments
      Then the Orders and the values they include are:
        | order | values             |
        | 5     |                    |
        | 10    | Succeeded, Failed  |
        | 50    | Pending, Succeeded |
        | 200   |                    |

    Scenario: Taking distinct values per Order is refused
      When I read the Orders including two distinct statuses of their Payments
      Then the read is refused, saying take and skip cannot be used with selectDistinct for each Order either

  Rule: A query may include several lists, and an included query its own

    Scenario: Each Order with its Payments and the Orders of the same Customer
      When I read the Orders by total, including their Payments by minute and the Orders of the same Customer
      Then the Orders, their Payments and the totals of the Orders they include are:
        | order | payments                              | included |
        | 5     |                                       |          |
        | 10    | 10 Succeeded, 10 Failed               | 10, 50   |
        | 50    | 20 Succeeded, 30 Succeeded, 5 Pending | 10, 50   |
        | 200   |                                       | 200      |
      And 5 commands are sent: the Orders, their lines, the Payments, and the Orders of the same Customer with their lines
      And the plan lists the Payments and the Orders of the same Customer as two more loads

    Scenario: Each included Order with its own Payments
      When I read the Orders by total, including the Orders of the same Customer, each with its Payments by minute
      Then the Orders and the Orders they include, with their Payments, are:
        | order | included                                                         |
        | 5     |                                                                  |
        | 10    | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | 50    | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | 200   | 200 ()                                                           |
      And 5 commands are sent: the Orders and their lines, the Orders of the same Customer and their lines, and the Payments

    Scenario: Each Customer with their Orders, each with its Payments
      When I read the Customers by name, including their Orders by total, each with its Payments by minute
      Then the Customers and their Orders, with their Payments, are:
        | customer | orders                                                           |
        | Ada      | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | Bob      | 200 ()                                                           |

    Scenario: Take applies per record at every level
      When I read the Orders by total, including the Orders of the same Customer, each with its newest Payment
      Then the Orders and the Orders they include, with their Payments, are:
        | order | included                    |
        | 5     |                             |
        | 10    | 10 (10 Failed); 50 (5 Pending) |
        | 50    | 10 (10 Failed); 50 (5 Pending) |
        | 200   | 200 ()                      |

    Scenario: Sixteen lists, past the seven elements where F# nests its tuples
      When I read the Orders by total, including sixteen lists: their Payments, then their Payments of at least 2 up to at least 16
      Then each Order comes with sixteen lists, the n-th holding its Payments of at least n:
        | order | counts                          |
        | 5     | 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 |
        | 10    | 2 2 2 2 2 2 2 2 2 2 0 0 0 0 0 0 |
        | 50    | 3 3 3 3 3 2 2 2 2 2 2 2 2 2 2 2 |
        | 200   | 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 |

    Scenario: The result's type lists the lists in the order written
      When I compile code that passes Orders with their Payments and same-Customer Orders, and Orders with included Orders and their Payments, to functions of those types
      Then the code compiles

  Rule: includingOne reads the one record a tie can find, as an option

    Scenario: Each Order with its Customer
      When I read the Orders by total, including one Customer each
      Then the Orders and their Customers are:
        | order | customer |
        | 5     | none     |
        | 10    | Ada      |
        | 50    | Ada      |
        | 200   | Bob      |
      And 3 commands are sent: the Orders, their lines, and the Customers

    Scenario: Each Order with its Customer's name and its Payments
      When I read the Orders by total, including one Customer's name each and their Payments
      Then the Orders, their Customers' names and how many Payments each has are:
        | order | customer | payments |
        | 5     | none     | 0        |
        | 10    | Ada      | 2        |
        | 50    | Ada      | 3        |
        | 200   | Bob      | 0        |

    Scenario: A unique column proves at most one too
      When I read the Customers by name, including one Customer with the same email each
      Then each Customer comes with itself

    Scenario: A second match in the database is an error, never a choice
      Given the customers table has lost its unique constraint on email
      And a Customer "Ann" with Ada's email
      When I read the Customers by name, including one Customer with the same email each
      Then the read fails, saying includingOne found 2 Customers for one Customer where its tie on Email proves at most one

    Scenario: A tie that could find several is refused, naming the column
      When I read the Orders including one Order of the same Customer each
      Then the read is refused, saying Order.CustomerId is neither the key of Order nor unique

    Scenario: Taking inside includingOne is refused
      When I read the Orders including one Customer each, taking 1
      Then the read is refused, saying take and skip mean nothing in includingOne

    Scenario: The result's type has an option where includingOne is written
      When I compile code that passes Orders with their Customer and their Payments to a function of Order * Customer option * Payment list
      Then the code compiles

  Rule: It is checked and prepared like any query

    Scenario: A prepared query's arguments bind inside the inner query
      When I read the Orders including their Payments of at least 25, through a prepared query
      Then the Order of 50 comes with the Payments 30 Succeeded

    Scenario: A query ending in including is planned once
      When I run the Orders with their Payments twice through one repository
      Then it is translated once

    Scenario: A module's including queries are checked
      When I check the queries of a module whose including queries translate
      Then no problem is reported

  Rule: What it cannot do is refused

    Scenario: A record loaded with its owner cannot be included
      When I read the Orders including their OrderLines
      Then the read is refused, saying OrderLines are loaded with their Order

    Scenario: A joined query cannot include
      When I read the Orders joined with their Customers, including their Payments
      Then the read is refused, saying including follows a query of one record

    Scenario Outline: <use> does not compile
      When I compile code where <code>
      Then the code does not compile

      Examples:
        | use                    | code                                                         |
        | a seventeenth list     | a query includes seventeen lists                             |
        | select after including | a query includes Payments and then selects the Order's total |
        | counting it            | the Orders with their Payments are counted                   |
