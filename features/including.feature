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
  holds only what it selects. The outer record still comes whole, with what it owns. A relationship can
  be written once as a reflected function of the outer record (`paymentsOf o`)
  and used in `including` and in `exists` alike.

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

    Scenario: A relationship written once as a reflected function serves including and exists
      When I read the Orders by total, including their succeeded Payments through paymentsOf
      Then the Orders and their Payments are:
        | order | payments                  |
        | 5     |                           |
        | 10    | 10 Succeeded              |
        | 50    | 20 Succeeded, 30 Succeeded |
        | 200   |                           |
      When I read the Orders with a Payment through paymentsOf in exists
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

  Rule: It is checked and prepared like any query

    Scenario: A prepared query's arguments bind inside the inner query
      When I read the Orders including their Payments of at least 25, through a prepared query
      Then the Order of 50 comes with the Payments 30 Succeeded

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
        | including twice        | a query includes Payments and then includes Payments again   |
        | select after including | a query includes Payments and then selects the Order's total |
        | counting it            | the Orders with their Payments are counted                   |
