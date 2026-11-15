# Lesson 11 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About writes
  Every write is a step of a transaction: Tx.insert, Tx.update and Tx.delete take the records themselves, and repo.Transact runs them. An update writes only what differs from the record read, and refuses a change made from a stale copy.

  Prerequisites: lesson 10.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                            | the learner discovers                                                                                       | teaches                                                                                  |
      | Inserting adds a row                            | every write is a transaction step: repo.Transact runs Tx.insert, which returns the stored record            | typed-transactions: Inserting a record gives it back with its key                        |
      | An insert assigns the key                       | the returned record carries the key the database chose                                                      | typed-transactions: Inserting a record gives it back with its key                        |
      | An update returns the new record                | Tx.update (original, changed) returns a new record; the one you read is unchanged                           | core: Generated values are returned immutably                                            |
      | The version goes up on update                   | each update of a versioned record adds one to its Version                                                   | concurrency: A versioned record is checked by its version alone                          |
      | Deleting a record                               | Tx.delete deletes the record read                                                                           | typed-transactions: Deleting the record read deletes it                                  |
      | Inserting returns the stored record             | the returned record has its generated key; the input is unchanged                                           | core: Generated values are returned immutably                                            |
      | Updates write only the changes                  | an update sends only the columns that differ between the original and the changed record                    | typed-transactions: An update writes only the columns that changed                       |
      | Stale updates are refused                       | a change made from a copy whose version is no longer stored is TxFailure.Conflict, and overwrites nothing   | core: Stale updates do not overwrite newer state                                         |
      | Without a version, every column is checked      | a record without a version is checked against every column it was read with, so a late change is a Conflict | concurrency: A change after another writer changed the row is a conflict                 |
      | A copy from elsewhere says which version it saw | expectVersion checks the version a caller saw, and a mismatch is a Conflict that writes nothing             | typed-transactions: An update expecting a version the record no longer has is a conflict |
