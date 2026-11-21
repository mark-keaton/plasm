# Lesson 28 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About recursive queries
  A record that refers to its own kind makes a tree. Query.recursive reads
  everything under a row, or above it, in one statement, with each row's
  depth, and the query made from it filters, orders and pages the whole.

  Prerequisites: lesson 14.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                   | the learner discovers                                                                         | teaches                                                                                               | hint                                                                                                                                                  |
      | Everything under a category            | Query.recursive reads a row and, again and again, the rows its step reaches, in one statement   | recursive-queries: Everything under a row, with its depth                                              | Start at Tools in the categories table under Given and follow each row's parent back down: its depth is how many steps found it, from 0.          |
      | Everything above a category            | the same form follows the parent the other way when the step joins found from the other side    | recursive-queries: Everything above a row, with its depth                                              | Start at Hand saws in the categories table under Given and follow its parent up, a step at a time, from depth 0.                                  |
      | The query made from it is a query       | the query made from a recursive query filters, orders and pages the whole, by depth too         | recursive-queries: A filter on depth leaves out the start                                              | Of the rows under Tools in the categories table under Given, leave out Tools itself, and give the names alphabetically.                           |
      | A cycle in the data ends               | a row already on the path that reached it is not followed again, so the statement always ends  | recursive-queries: A tree whose root has been given a parent under it is read once round               | With Tools under Hand saws, each row is still found once on its way round: the same rows as the first koan.                                       |
      | A step must join the rows found        | a step that does not join found is refused when the query is translated, saying so             | recursive-queries: A step that does not join the rows found is refused                                  | The comment says what such a query is refused with, and the text the refusal says.                                                                |
