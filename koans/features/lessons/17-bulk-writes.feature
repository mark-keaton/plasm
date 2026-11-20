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
      | koan                                 | the learner discovers                                               | teaches                                                           | hint                                                                                                                                                        |
      | A bulk update returns a count        | Bulk.update changes the rows a query matches and returns how many   | bulk-writes: Updating every row a query matches                   | Count the rows of the products table under Given with the sku that the where names, as a plain int.                                                         |
      | A bulk update can match nothing      | a query matching no rows updates nothing and returns 0              | bulk-writes: Updating every row a query matches                   | Look for that sku in the products table under Given.                                                                                                        |
      | A bulk delete returns a count        | Bulk.delete removes the rows a query matches and returns how many   | bulk-writes: Deleting every row a query matches                   | Count the rows of the payments table under Given with that order_id.                                                                                        |
      | Deleting every payment               | Bulk.delete of Query.from removes every row of the table            | bulk-writes: Deleting every row a query matches                   | Count all the rows of the payments table under Given.                                                                                                       |
      | Updating what a query matches        | Bulk.update changes every matching row and returns how many         | bulk-writes: Updating every row a query matches                   | Count the draft rows of the orders table under Given, then the published rows once the drafts have joined them, the second written as repo.Count writes it. |
      | Updating from the current row        | the new value can be computed from the row's own fields             | bulk-writes: Bulk updates can compute from the current row        | Add the totals of the orders table under Given and double the sum.                                                                                          |
      | Deleting what a query matches        | Bulk.delete removes matching rows and returns how many              | bulk-writes: Deleting every row a query matches                   | Count the failed rows of the payments table under Given, then the rows that are left.                                                                       |
      | Inserting many records               | Bulk.insert writes many records in one command and returns them     | bulk-writes: Inserting many records in one command                | The inserted records come back in the order they were given, and a bulk write, as the first koan of this lesson said, is a single command.                  |
      | Inserting only what is missing       | insertMissing skips records whose key already exists                | bulk-writes: Inserting only what is missing                       | Check each sku of the catalogue against the products table under Given: only the records not already there are inserted and returned.                       |
      | Bulk writes are steps like any other | a bulk write is a step: a later failure rolls it back with the rest | bulk-writes: Bulk writes run inside transactions                  | The transaction ends in Tx.fail, and the comment says what happens to the bulk delete, so count the payments table as it is under Given.                    |
      | Bulk writes can be inspected         | repo.Explain shows a bulk write's SQL without running it            | bulk-writes: Bulk writes can be inspected without database access | A delete of Payment starts by naming its table, and lesson 02 showed whether Explain sends anything.                                                        |
