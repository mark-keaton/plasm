# Lesson 27 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About bulk writes that return their rows
  A bulk update can give the rows it changed, as they are after it, and a
  bulk delete the rows it removed, instead of a count.

  Prerequisites: lesson 18.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                          | the learner discovers                                                                     | teaches                                                                                  | hint                                                                                                                                          |
      | A bulk update gives back the rows it changed  | Bulk.updateReturning gives the rows as they are after it, read whole with what they own     | bulk-writes: A bulk update returns the rows it changed, as they are after it             | Find the draft rows of the orders table under Given; each is now cancelled, and its lines are its rows in the order_lines table.             |
      | A bulk delete gives back the rows it removed  | Bulk.deleteReturning gives the rows as they were                                           | bulk-writes: A bulk delete returns the rows it deleted                                   | Find the failed rows of the payments table under Given, give their amounts, and count the rows left.                                         |
      | A delete cannot return what records own       | a delete of records that own others cannot read what they owned, so it is refused by name  | bulk-writes: A bulk delete of records that own others does not return them               | The comment says what the transaction fails with, and the text the refusal says; nothing is sent, so count the rows of orders under Given.   |
