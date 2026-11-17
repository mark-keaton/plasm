# Executable acceptance specification (Reqnroll): reading other aggregates'
# records with a query, each bound by name with let!, the row built by return.
# Written before the implementation.
@implemented
Feature: A query binds what it reads for each record with let!, and builds its row with return
  As an F# application developer
  I want to read each Order with its Payments in one query, naming each list I read
  So that each row is a record whose fields say what was read, with no second
  query and no matching by hand

  After `for`, and its `where`, `orderBy` and `take`, each `let!` or `and!`
  binds what one more query finds for each record, and `return` builds the
  row:

      plasm {
          for o in tableOf<Order> do
          orderBy o.Total
          let! payments = referring<Payment> o
          return {| Order = o; Payments = payments |}
      }

  Each load is one more command for all the records, in batches of their
  values, so loads never multiply each other's rows. Its query refers to the
  outer record by one equality between an inner and an outer column, and its
  conditions, order and `take` apply per record. A query gives a list of its
  records, or of what it selects; `referredBy<Customer> o`, the one record a
  tie can find, gives an option, and so does what it selects. A load's query
  may bind loads of its own and end in `return`. There is no limit to how
  many loads a query binds.

  `return` builds a record or an anonymous record. Its fields are the query's
  record, one of its fields or members, or a load, each load once and in the
  field of its own name (`let! newest` in `Newest`), so two loads of one type
  cannot be crossed. The query reads the record whole, so a member of it is
  read from the record. What the compiler lets through but cannot mean what it
  says is refused when the query is planned, and so when the repository
  starts (query-check.feature).

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

  Rule: Each let! is one more command, and return builds the row

    Scenario: Each Order with its Payments
      When I read the Orders by total, binding their Payments by minute
      Then the Orders and their Payments are:
        | order | payments                              |
        | 5     |                                       |
        | 10    | 10 Succeeded, 10 Failed               |
        | 50    | 20 Succeeded, 30 Succeeded, 5 Pending |
        | 200   |                                       |
      And the Order of 10 still has its line

    Scenario: The Payments are read with one more command, however many Orders
      When I read the Orders by total, binding their Payments by minute
      Then 3 commands are sent: the Orders, their lines, and the Payments
      And the Payments command takes the Orders' ids as one parameter
      And the plan lists the Payments as one more load, named payments

    Scenario: A field of the record, or a member of it, goes in the row beside the loads
      When I read the Orders by total, each row with the Order's total, whether it is large, and its Payments
      Then each row's Total is its Order's total, and Large says whether it is 50 or more

    Scenario: The row's type is the record return builds
      When I compile code that passes the Orders read with their Payments to a function of a record of them
      Then the code compiles

    Scenario: A relationship that says more than the tie is written once and serves let! and exists
      When I read the Orders by total, binding what succeededPaymentsOf finds for each
      Then the Orders and their Payments are:
        | order | payments                   |
        | 5     |                            |
        | 10    | 10 Succeeded               |
        | 50    | 20 Succeeded, 30 Succeeded |
        | 200   |                            |
      When I read the Orders that succeededPaymentsOf finds something for, in exists
      Then the Orders found are those of 10 and 50

    Scenario: Inside a transaction
      When a transaction reads the Orders by total, binding their Payments by minute
      Then the Order of 50 comes with 3 Payments

  Rule: Conditions, order and take apply per outer record

    Scenario: The newest Payment of each Order
      When I read the Orders by total, binding each one's newest Payment
      Then the Orders and their Payments are:
        | order | payments  |
        | 5     |           |
        | 10    | 10 Failed |
        | 50    | 5 Pending |
        | 200   |           |

    Scenario: Taking without an order is refused
      When I read the Orders binding two of their Payments in no order
      Then the read is refused, saying "take/skip in the query of let! payments needs an order"

    Scenario: The outer column need not be the key
      When I read the Orders by total, binding the Orders of the same Customer
      Then the Orders and the totals of the Orders they bind are:
        | order | others |
        | 5     |        |
        | 10    | 10, 50 |
        | 50    | 10, 50 |
        | 200   | 200    |

    Scenario: A load's query refers to the outer record only by one equality
      When I read the Orders binding the Payments made after the Order was placed
      Then the read is refused, naming the condition that refers to the Order

  Rule: A load's query may select what its list holds

    Scenario: Each Order with the amounts of its Payments
      When I read the Orders by total, binding the amounts of their Payments by minute
      Then the Orders and the values they bind are:
        | order | values    |
        | 5     |           |
        | 10    | 10, 10    |
        | 50    | 20, 30, 5 |
        | 200   |           |
      And the Payments command reads only the amount and the Order it belongs to

    Scenario: The newest amount of each Order
      When I read the Orders by total, binding the amount of each one's newest Payment
      Then the Orders and the values they bind are:
        | order | values |
        | 5     |        |
        | 10    | 10     |
        | 50    | 5      |
        | 200   |        |

    Scenario: The distinct statuses of each Order's Payments
      When I read the Orders by total, binding the distinct statuses of their Payments
      Then the Orders and the values they bind are:
        | order | values             |
        | 5     |                    |
        | 10    | Succeeded, Failed  |
        | 50    | Pending, Succeeded |
        | 200   |                    |

    Scenario: Taking distinct values per Order is refused
      When I read the Orders binding two distinct statuses of their Payments
      Then the read is refused, saying "take and skip cannot be used with selectDistinct"

  Rule: A query binds any number of loads with and!, and a load its own

    Scenario: Each Order with its Payments and the Orders of the same Customer
      When I read the Orders by total, binding their Payments by minute and the Orders of the same Customer
      Then the Orders, their Payments and the totals of the Orders they bind are:
        | order | payments                              | others |
        | 5     |                                       |        |
        | 10    | 10 Succeeded, 10 Failed               | 10, 50 |
        | 50    | 20 Succeeded, 30 Succeeded, 5 Pending | 10, 50 |
        | 200   |                                       | 200    |
      And 5 commands are sent: the Orders, their lines, the Payments, and the Orders of the same Customer with their lines
      And the plan lists the loads payments and others

    Scenario: Two loads of one type side by side, each in the field of its name
      When I read the Orders by total, binding the amounts of their two newest Payments and of their succeeded Payments
      Then the Orders and their two lists of amounts are:
        | order | newest | succeeded |
        | 5     |        |           |
        | 10    | 10, 10 | 10        |
        | 50    | 5, 30  | 20, 30    |
        | 200   |        |           |

    Scenario: Each Order of the same Customer with its own Payments
      When I read the Orders by total, binding the Orders of the same Customer, each binding its Payments by minute
      Then the Orders and the Orders they bind, with their Payments, are:
        | order | others                                                                  |
        | 5     |                                                                         |
        | 10    | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | 50    | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | 200   | 200 ()                                                                  |
      And 5 commands are sent: the Orders and their lines, the Orders of the same Customer and their lines, and the Payments

    Scenario: Each Customer with their Orders, each with its Payments
      When I read the Customers by name, binding their Orders by total, each binding its Payments by minute
      Then the Customers and their Orders, with their Payments, are:
        | customer | orders                                                                  |
        | Ada      | 10 (10 Succeeded, 10 Failed); 50 (20 Succeeded, 30 Succeeded, 5 Pending) |
        | Bob      | 200 ()                                                                  |

    Scenario: Take applies per record at every level
      When I read the Orders by total, binding the Orders of the same Customer, each binding its newest Payment
      Then the Orders and the Orders they bind, with their Payments, are:
        | order | others                         |
        | 5     |                                |
        | 10    | 10 (10 Failed); 50 (5 Pending) |
        | 50    | 10 (10 Failed); 50 (5 Pending) |
        | 200   | 200 ()                         |

    Scenario: Seventeen loads side by side
      When I read the Orders by total, binding seventeen lists: their Payments of at least 1 up to at least 17
      Then each Order comes with seventeen lists, the n-th holding its Payments of at least n:
        | order | counts                            |
        | 5     | 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 |
        | 10    | 2 2 2 2 2 2 2 2 2 2 0 0 0 0 0 0 0 |
        | 50    | 3 3 3 3 3 2 2 2 2 2 2 2 2 2 2 2 2 |
        | 200   | 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 |

  Rule: A let! of the one record a tie can find binds an option

    Scenario: Each Order with its Customer
      When I read the Orders by total, binding the Customer each refers to
      Then the Orders and their Customers are:
        | order | customer |
        | 5     | none     |
        | 10    | Ada      |
        | 50    | Ada      |
        | 200   | Bob      |
      And 3 commands are sent: the Orders, their lines, and the Customers

    Scenario: What is selected of it is an option too
      When I read the Orders by total, binding the name of the Customer each refers to and their Payments
      Then the Orders, their Customers' names and how many Payments each has are:
        | order | customer | payments |
        | 5     | none     | 0        |
        | 10    | Ada      | 2        |
        | 50    | Ada      | 3        |
        | 200   | Bob      | 0        |

    Scenario: The row's type has an option where the one record is bound
      When I compile code that passes the Orders with their Customer and their Payments to a function of a record with a Customer option
      Then the code compiles

  Rule: return builds a record, each load once in the field of its name

    Scenario: A load put in another's field is refused, naming both
      When I read the Orders binding the amounts of their newest and of their succeeded Payments, returned crossed
      Then the read is refused, saying "succeeded is returned in the field Newest: a load is returned in the field of its name, Succeeded"

    Scenario: A field computed from a load is refused, naming it
      When I read the Orders binding their Payments, returning how many there are
      Then the read is refused, saying "the field Count of return is List.length payments: a field of return is the query's record (o), one of its fields (o.Id), or a name bound by let! or and!, each once; compute it from the rows read"

    Scenario: A query bound with let is not loaded
      When I read the Orders with their Payments bound by let, not let!
      Then the read is refused, saying "the field Payments of return is payments, a query bound with let, which is not loaded: bind it with let!"

    Scenario: A load that is not returned is refused
      When I read the Orders binding their Payments and their succeeded Payments' amounts, returning only the Payments
      Then the read is refused, saying "succeeded is bound by and! but not returned: return it in a field, or remove it, as each load is one more command"

    Scenario: A load returned twice is refused
      When I read the Orders binding their Payments, returning them in two fields
      Then the read is refused, saying "payments is returned twice"

    Scenario: return builds a record
      When I read the Orders binding their Payments, returning a tuple
      Then the read is refused, saying "return builds a record or an anonymous record, naming each field, as in return {| Order = o; Payments = payments |}"

  Rule: It is checked and prepared like any query

    Scenario: A prepared query's arguments bind inside the load's query
      When I read the Orders binding their Payments of at least 25, through a prepared query
      Then the Order of 50 comes with the Payments 30 Succeeded

    Scenario: A query that binds loads is planned once
      When I run the Orders with their Payments twice through one repository
      Then it is translated once

    Scenario: A module's queries that bind loads are checked
      When I check the queries of a module whose queries bind loads that translate
      Then no problem is reported

  Rule: What it cannot do is refused

    Scenario: A record loaded with its owner cannot be bound
      When I read the Orders binding their OrderLines
      Then the read is refused, saying "OrderLine records are loaded with their Order"

    Scenario: A query that joins cannot bind loads
      When I read the Orders joined with their Customers, binding their Payments
      Then the read is refused, saying "a query that joins or groups cannot bind loads"

    Scenario: A load written as a function that is not reflected is refused
      When I read the Orders binding their Payments through a function that is not reflected
      Then the read is refused, saying "let! payments takes a query written in place, plasm { for x in source do where (...) }"

    Scenario Outline: <use> does not compile
      When I compile code where <code>
      Then the code does not compile

      Examples:
        | use                                | code                                                 |
        | a where after the loads            | a query filters after binding its Payments           |
        | a second let! in place of and!     | a query binds its Payments and its Customer with let! twice |
        | ordering the one record            | a query orders what referredBy finds                 |
        | counting it                        | the Orders with their Payments are counted           |
