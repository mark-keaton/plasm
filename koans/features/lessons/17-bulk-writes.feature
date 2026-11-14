# Lesson 17 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About bulk writes
  Bulk updates and deletes are driven by typed queries; inserts take many records in one command.

  Prerequisites: lesson 11, lesson 07.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                            | the learner discovers                                             | teaches                                                           |
      | A bulk update returns a count   | Bulk.update changes the rows a query matches and returns how many | bulk-writes: Updating every row a query matches                   |
      | A bulk update can match nothing | a query matching no rows updates nothing and returns 0            | bulk-writes: Updating every row a query matches                   |
      | A bulk delete returns a count   | Bulk.delete removes the rows a query matches and returns how many | bulk-writes: Deleting every row a query matches                   |
      | Deleting every payment          | Bulk.delete of Query.from removes every row of the table          | bulk-writes: Deleting every row a query matches                   |
      | Updating what a query matches   | Bulk.update changes every matching row and returns how many       | bulk-writes: Updating every row a query matches                   |
      | Updating from the current row   | the new value can be computed from the row's own fields           | bulk-writes: Bulk updates can compute from the current row        |
      | Deleting what a query matches   | Bulk.delete removes matching rows and returns how many            | bulk-writes: Deleting every row a query matches                   |
      | Inserting many records          | Bulk.insert writes many records in one command and returns them   | bulk-writes: Inserting many records in one command                |
      | Inserting only what is missing  | insertMissing skips records whose key already exists              | bulk-writes: Inserting only what is missing                       |
      | Bulk writes can be inspected    | repo.Explain shows a bulk write's SQL without running it          | bulk-writes: Bulk writes can be inspected without database access |
