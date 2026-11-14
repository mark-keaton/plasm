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
      | koan                                 | the learner discovers                                  | teaches                                                  |
      | An inner join gives pairs            | join returns (Order, Customer) pairs for matching keys | joins: An inner join returns matching pairs              |
      | A left join keeps every row          | a missing Customer is None                             | joins: A left join represents a missing match as None    |
      | Join keys are typed                  | joining an OrderId to a CustomerId does not compile    | joins: Join keys are checked at compile time             |
      | Filters on joined columns run in SQL | where (c.Name = "Ada") filters in the database         | joins: Filters on joined columns run in the database     |
      | Joined queries compose               | an ordinary function can extend a joined query         | joins: Joined queries compose through ordinary functions |
