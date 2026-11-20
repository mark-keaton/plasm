# Lesson 15 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About loads
  A load reads what another query finds for each record: let! binds it, and return builds the row. Plasm reads it for every record in one more command, however many records there are, so a list never multiplies another's rows.

  Prerequisites: lesson 09, lesson 14.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                                    | the learner discovers                                                                                                                                                                        | teaches                                                                                      | hint                                                                                                                                                                                 |
      | Each order with its payments                            | referring<Payment> o is the payments that refer to an order; let! binds them to each order, in one more command for any number of orders, and return builds the row                          | referring: Each Order with the Payments referring to it                                      | Count each order's rows in the payments table under Given, and add the command the comment gives the payments to the two a read of orders always takes.                              |
      | A relationship that says more is written once           | a [<ReflectedDefinition>] function built on referring says more than the tie; it serves exists and is narrowed inside a let!, where take 1 is per order and select picks what the list holds | loads: A relationship that says more than the tie is written once and serves let! and exists | Only the orders with a succeeded payment are kept, and each comes with a list holding the status of its newest succeeded payment.                                                    |
      | Each order with its payments and its deliveries         | and! binds a second load beside the first, each in the field of its name and one more command                                                                                                | loads: Each Order with its Payments and the Orders of the same Customer                      | For each order, count its rows in the payments table and give its delivery's StageName as Shop.fs spells it, and add a command for each load to the two a read of orders takes.      |
      | Each customer with their orders, each with its payments | a load's query may bind loads of its own and build its own row; referring finds an optional field too                                                                                        | loads: Each Customer with their Orders, each with its Payments                               | Under each customer, in id order, list that customer's rows of the orders table under Given, each with its count of payments rows, and give a customer with no orders an empty list. |
      | Each order with its customer                            | referredBy<Customer> o is the one customer an order refers to; a let! of it, or of what is selected of it, binds an option                                                                   | referring: Each Order with the Customer it refers to                                         | Give each order its customer's name from the customers table under Given as an option, following the comment for the order that has no customer.                                     |
