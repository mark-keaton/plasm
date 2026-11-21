# Lesson 26 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About locks and tries
  A transaction can lock the rows it reads until it ends, and try a step
  under a savepoint, going on from its failure as from any value.

  Prerequisites: lesson 13.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                        | the learner discovers                                                                   | teaches                                                                                     | hint                                                                                                                                              |
      | A transaction locks the rows it reads       | locking RowLock.ForUpdate locks the query's rows, FOR UPDATE OF its own table, in Tx.all   | row-locks: A locked read sends its lock on the query's own rows, not on what they own        | Find the draft rows of the orders table under Given and give their ids as OrderIds; the comment says what the SELECT ends with.                   |
      | What an order owns is read, not locked      | what a locked record owns is loaded with it in a command of its own, without a lock       | row-locks: A locked read sends its lock on the query's own rows, not on what they own        | Count the first order's rows in the order_lines table under Given, and the statements sent that say FOR UPDATE: only the order's own SELECT does.             |
      | A tried step's failure is a value           | Tx.attempt gives a step's failure as an Error the transaction goes on from                | tx-attempt: A duplicate tried under Tx.attempt is a value, and the transaction goes on       | Ada's email is already in the customers table under Given, so the first insert's error is that field's, and the customer stored has the other.    |
      | A tried step that succeeds is kept          | a tried step that succeeds gives Ok, and what it wrote is kept                            | tx-attempt: A tried step that succeeds is kept                                               | No customer under Given has Dan's email, so the insert succeeds; count the rows of the customers table under Given, with Dan.                     |
      | A tried Conflict is a value                 | a stale write tried under Tx.attempt is its Conflict, as a value                          | tx-attempt: A tried step's Conflict is a value, and what it wrote is undone                  | first was read before order 1 was changed, so the update made from it is stale; a Conflict carries the record type's name and its key.            |
