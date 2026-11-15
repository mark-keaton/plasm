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
      | koan                                 | the learner discovers                                                                           | teaches                                                           |
      | The simplest inner join              | join pairs each OrderLine with its Product                                                      | joins: An inner join returns matching pairs                       |
      | Selecting a pair's fields            | select can take fields from both sides of a join                                                | joins: Projections over joins retrieve only the requested columns |
      | Counting joined rows                 | an Order with two Payments gives two joined rows                                                | joins: A join returns one row per matching pair                   |
      | A left join's missing side is None   | an Order without a Customer is paired with None                                                 | joins: A left join represents a missing match as None             |
      | An inner join gives pairs            | a join can select a whole Customer, but only an Order's fields: an Order is read with its lines | joins: An inner join returns matching pairs                       |
      | A left join keeps every row          | a missing Customer is None                                                                      | joins: A left join represents a missing match as None             |
      | Join keys are typed                  | joining an OrderId to a CustomerId does not compile                                             | joins: Join keys are checked at compile time                      |
      | Filters on joined columns run in SQL | where (c.Name = "Ada") filters in the database                                                  | joins: Filters on joined columns run in the database              |
      | Joined queries compose               | an ordinary function can extend a joined query                                                  | joins: Joined queries compose through ordinary functions          |
