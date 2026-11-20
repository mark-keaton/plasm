# Lesson 14 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About joins
  Joins return combined rows from one command; left joins give the missing side as None.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                 | the learner discovers                                                                         | teaches                                                           | hint                                                                                                                                             |
      | The simplest inner join              | join pairs each OrderLine with its Product                                                    | joins: An inner join returns matching pairs                       | For each row of order_lines under Given, in id order, give the name of the product its product_id points at.                                     |
      | Selecting a pair's fields            | select can take fields from both sides of a join                                              | joins: Projections over joins retrieve only the requested columns | Take the first order's rows in order_lines under Given, and pair each product's name with the row's unit_price.                                  |
      | Counting joined rows                 | an Order with two Payments gives two joined rows                                              | joins: A join returns one row per matching pair                   | The comment says an order appears once per payment, so count the rows of the payments table under Given.                                         |
      | A left join's missing side is None   | an Order without a Customer is paired with None                                               | joins: A left join represents a missing match as None             | The comment says what the other side is when no row matches, and the fourth order has no customer_id.                                            |
      | An inner join gives pairs            | a join can select the whole record of either side: here an order's id with its whole Customer | joins: An inner join returns matching pairs                       | Lesson 09's koan 'A join reads both aggregates' fields' gave these same pairs.                                                                   |
      | A left join keeps every row          | a missing Customer is None                                                                    | joins: A left join represents a missing match as None             | The last row is the order with the largest id, which has no customer, so its other side is what the earlier left-join koan found.                |
      | Join keys are typed                  | joining an OrderId to a CustomerId does not compile                                           | joins: Join keys are checked at compile time                      | Every other join of orders and customers in this lesson writes this same on condition.                                                           |
      | Filters on joined columns run in SQL | where (c.Name = "Ada") filters in the database                                                | joins: Filters on joined columns run in the database              | The query has a where, which the koan's name says runs in SQL, and the ids are those of Ada's rows in the orders table under Given, as OrderIds. |
      | Joined queries compose               | an ordinary function can extend a joined query                                                | joins: Joined queries compose through ordinary functions          | Of the orders that have a customer, keep those with a total over twenty, and give each one's customer's name in order of the order's id.         |
