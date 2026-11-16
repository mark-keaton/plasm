# Executable acceptance specification (Reqnroll): ties that the typed ids
# prove, written by Plasm. Written before the implementation.
@implemented
Feature: A tie that the typed ids prove is left to Plasm
  As an F# application developer
  I want to read the Payments of an Order without writing p.OrderId = o.Id
  So that nine relationships do not need nine hand-written functions

  `referring<Payment> o` is the Payments whose one field referring to o's
  record (of its key's typed id, plain or an option) equals o's key: exactly `plasm { for p in tableOf<Payment> do
  where (p.OrderId = o.Id) }`. `referredBy<Customer> o` is the Customer whose
  key equals o's one field referring to a Customer; it finds at most one, so
  `includingOne` takes it. Both are queries: they serve in `including`,
  `includingOne` and `exists`, as the source of a query that narrows them, and
  with a record in hand. The key itself is never the tie, so a record that
  refers to its own kind (a Category's Parent) reads its children with
  `referring` and its parent with `referredBy`. When there is not exactly one
  such field, the query is refused, naming the record and the fields: Plasm
  never picks one. A field is such a field exactly when the model counts it as a
  reference: never one marked [<NotReference>], and never a field of a
  built-in type such as int64, which says nothing about whose key it holds. A named function stays for a relationship that says more
  than the tie, built on it.

  # Order = { Id: OrderId; CustomerId: CustomerId option; Total: decimal; ...; Lines: OrderLine list }
  # Payment = { Id: PaymentId; OrderId: OrderId; Amount: decimal; Status: PaymentStatus; CreatedAt: DateTime }
  Background:
    Given a configured relational database
    And the Customers "Ada" and "Bob"
    And Ada's Orders of 10 and 50, Bob's Order of 200, and an Order of 5 with no Customer
    And the Payments, by order and minute:
      | order | amount | status    | minute |
      | 10    | 10     | Succeeded | 1      |
      | 10    | 10     | Failed    | 2      |
      | 50    | 20     | Succeeded | 1      |
      | 50    | 30     | Succeeded | 3      |
      | 50    | 5      | Pending   | 5      |

  Rule: referring reads the records that refer to a record

    Scenario: Each Order with the Payments referring to it
      When I read the Orders by total, including the Payments referring to each, by minute
      Then the Orders and their Payments are:
        | order | payments                              |
        | 5     |                                       |
        | 10    | 10 Succeeded, 10 Failed               |
        | 50    | 20 Succeeded, 30 Succeeded, 5 Pending |
        | 200   |                                       |
      And the Payments command is the one the tie written by hand sends

    Scenario: A query that narrows the Payments referring to each Order
      When I read the Orders by total, including the succeeded Payments referring to each
      Then the Orders and their Payments are:
        | order | payments                   |
        | 5     |                            |
        | 10    | 10 Succeeded               |
        | 50    | 20 Succeeded, 30 Succeeded |
        | 200   |                            |

    Scenario: In a prepared query
      When I read the Orders including the Payments referring to each of at least 25, through a prepared query
      Then the Order of 50 comes with the Payments 30 Succeeded

    Scenario: In exists
      When I read the Orders a Payment refers to
      Then the Orders found are those of 10 and 50

    Scenario: The Payments referring to an Order in hand
      When I read the Payments referring to the Order of 50, by minute
      Then the Payments read are 20 Succeeded, 30 Succeeded, 5 Pending

  Rule: referredBy reads the one record a record refers to

    Scenario: Each Order with the Customer it refers to
      When I read the Orders by total, including the Customer each refers to
      Then the Orders and their Customers are:
        | order | customer |
        | 5     | none     |
        | 10    | Ada      |
        | 50    | Ada      |
        | 200   | Bob      |

    Scenario: The Customer an Order in hand refers to
      When I read the Customer the Order of 200 refers to
      Then the Customer read is Bob

  Rule: A record that refers to its own kind

    Scenario: Each Category with its children and its parent
      Given the Categories "Tools", "Saws" and "Drills" in Tools, and "Hand saws" in Saws
      When I read the Categories by name, including their children and their parent
      Then the Categories, their children and their parent are:
        | category  | children        | parent |
        | Drills    |                 | Tools  |
        | Hand saws |                 | Saws   |
        | Saws      | Hand saws       | Tools  |
        | Tools     | Drills, Saws    | none   |

  Rule: Without exactly one such field the query is refused, naming them

    Scenario: Two fields of the key's type
      When I check the queries of a module that reads the Transfers referring to an Account
      Then a problem is reported naming Transfer, AccountId, From and To

    Scenario: No field of the key's type
      When I read the Customers referring to an Order
      Then the read is refused, saying Customer has no field holding an OrderId

    Scenario: A field the model does not count as a reference is not a tie
      When I read the Memos referring to an Account, where Memo's one AccountId field is marked NotReference
      Then the read is refused, saying Memo has no field referring to an Account

    Scenario: A built-in key says nothing about whose key a field holds
      When I read the Books referring to a Shelf, where Shelf's key and Book's one other field are int64
      Then the read is refused, saying Book has no field referring to a Shelf, as a Shelf's key is an int64

    Scenario: An option of a record is not a record
      When I read the Payments referring to the Order of 50 as an option
      Then the read is refused, saying Order option is not a stored record
