# Lesson 10 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About writes
  Every write is a step of a transaction: Tx.insert, Tx.update and Tx.delete take the records themselves, and repo.Transact runs them. An update writes only what differs from the record read, and refuses a change made from a stale copy.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                            | the learner discovers                                                                                       | teaches                                                                                              | hint                                                                                                                                                         |
      | Inserting adds a row                            | every write is a transaction step: repo.Transact runs Tx.insert, which returns the stored record            | typed-transactions: Inserting a record gives it back with its key                                    | The stored customer has the name it was inserted with, and the customers table now has a row more than under Given.                                          |
      | An insert assigns the key                       | the returned record carries the key the database chose                                                      | typed-transactions: Inserting a record gives it back with its key                                    | The query finds the row just inserted by its new key, so the option holds that customer's name.                                                              |
      | An update returns the new record                | Tx.update (original, changed) returns a new record; the one you read is unchanged                           | core: Generated values are returned immutably                                                        | The first is the Total the with expression sets, and the second is that order's total as the orders table under Given has it.                                |
      | The version goes up on update                   | each update of a versioned record adds one to its Version                                                   | concurrency: A versioned record is checked by its version alone                                      | Read that row's version in the orders table under Given, and the comment says Version counts the row's updates: this is its first.                           |
      | Deleting a record                               | Tx.delete deletes the record read                                                                           | typed-transactions: Deleting the record read deletes it                                              | Count the rows of the customers table under Given, then take away the row the transaction deletes.                                                           |
      | Inserting returns the stored record             | the returned record has its generated key; the input is unchanged                                           | core: Generated values are returned immutably                                                        | The comment says the database generated a key for the stored record, while sprocket itself is still the value New.product built: see New.unsaved in Shop.fs. |
      | Updates write only the changes                  | an update sends only the columns that differ between the original and the changed record                    | typed-transactions: An update writes only the columns that changed                                   | Lesson 04's koan 'Only what you change is written' asked this of the same change.                                                                            |
      | Stale updates are refused                       | a change made from a copy whose version is no longer stored is TxFailure.Conflict, and overwrites nothing   | core: Stale updates do not overwrite newer state                                                     | The comment names the failure a stale change gets and what it carries, and the record updated here is an order. |
      | Without a version, every column is checked      | a record without a version is checked against every column it was read with, so a late change is a Conflict | concurrency: A change after another writer changed the row is a conflict                             | The stale change here is to a customer, and the koan before says how a stale change fails. |
      | A copy from elsewhere says which version it saw | Tx.oneAsSeen reads a record as the version a caller saw, and another stored version is a Conflict           | typed-transactions: A copy the caller saw before another change is stale, before anything is decided | The seen version 7 is not the draft's, so the first read fails as the koans before it did, and the second reads the draft under Given. |
