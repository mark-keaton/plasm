# Lesson 09 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About aggregates and references
  An aggregate is a record with the records it owns, read whole: an Order is always read with its lines, in one more command for any number of orders. Other aggregates are referred to by id and reached with a query, a join, an exists, or a let! that binds them to each record.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                              | the learner discovers                                                                                      | teaches                                                               |
      | An order is read with its lines                   | an Order owns its lines: reading an order always reads its Lines, a plain list                             | owned-collections: Reading an invoice reads its lines                 |
      | Lines come in the order of their key              | owned records are read in the order of their key unless [<OrderedBy>] names a field                        | owned-collections: Reading an invoice reads its lines                 |
      | No lines is an empty list                         | an order without lines reads with []: an owned list is never left unread                                   | owned-collections: An invoice without lines reads with an empty list  |
      | Every order's lines come in one more command      | four orders and all their lines take two commands, and reading Lines sends nothing                         | owned-collections: Every invoice's lines are read in one more command |
      | A page of orders reads only its lines             | with take 2, only the lines of those two orders are read                                                   | elegance: Database work remains bounded by the requested page         |
      | A customer is referred to by id                   | an Order holds its customer's CustomerId, a reference to tableOf<Customer>; a query reaches the customer        | constraints: A typed id references the schema whose key it is         |
      | A join reads both aggregates' fields              | a join pairs orders with their customers in one command; an order without a customer is left out           | joins: An inner join returns matching pairs                           |
      | A whole order from a join comes with its lines    | a whole Order selected from a join is read with its lines, in one more command | owned-collections: A whole Invoice selected from a join comes with its lines |
      | Filtering orders by their lines                   | List.exists over o.Lines is run by the database, and keeps each matching order once                        | linked-collections: Cards are kept by their tags, as F# reads a list  |
      | Every line passes, as F# reads it                 | List.forall over o.Lines keeps an order with no lines, as F# would                                         | linked-collections: Cards whose every tag passes, as F# reads a list  |
      | Orders with a succeeded payment                   | exists (an inner query over referring<Payment> o) reaches another aggregate inside a where                    | query-exists: Orders with a succeeded payment                         |
      | Orders without a succeeded payment                | not (exists ...) keeps the orders no matching payment refers to                                            | query-exists: Orders without a succeeded payment                      |
      | Each order with its payments                      | referring<Payment> o is the payments that refer to an order; let! binds them to each order, in one more command for any number of orders, and return builds the row | referring: Each Order with the Payments referring to it |
      | A relationship that says more is written once     | a [<ReflectedDefinition>] function built on referring says more than the tie; it serves exists and is narrowed inside a let!, where take 1 is per order and select picks what the list holds | loads: A relationship that says more than the tie is written once and serves let! and exists |
      | Each order with its payments and its deliveries   | and! binds a second load beside the first, each in the field of its name and one more command | loads: Each Order with its Payments and the Orders of the same Customer |
      | Each customer with their orders, each with its payments | a load's query may bind loads of its own and build its own row; referring finds an optional field too | loads: Each Customer with their Orders, each with its Payments    |
      | Each order with its customer                      | referredBy<Customer> o is the one customer an order refers to; a let! of it, or of what is selected of it, binds an option          | referring: Each Order with the Customer it refers to                  |
