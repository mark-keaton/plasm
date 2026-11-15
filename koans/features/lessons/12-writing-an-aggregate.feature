# Lesson 12 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About writing an aggregate
  An aggregate is written whole, as it is read: inserting an order inserts its lines, an update from an order read writes the difference by key (new lines inserted, changed ones updated, missing ones deleted), and deleting an order deletes its lines. A line's error is the order's.

  Prerequisites: lesson 11, lesson 09.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                      | the learner discovers                                                                                       | teaches                                                                        |
      | Inserting an order inserts its lines      | one Tx.insert of an Order writes the order and every line in its list                                       | owned-collections: Inserting an Invoice inserts its lines                      |
      | The stored order holds its stored lines   | the returned Order holds its lines as stored, each with the new order's key                                 | owned-collections: Inserting an Invoice inserts its lines                      |
      | Saved lines are read back                 | reading the order again reads the lines that were written                                                   | owned-collections: Inserting an Invoice inserts its lines                      |
      | Adding a line to an order read            | a line without a key, added to the list, is inserted by the update                                          | owned-collections: Changing the lines of an Invoice read writes the difference |
      | Lines that did not change are not written | an update that changes only the order's own fields sends nothing to order_lines but a read                  | typed-transactions: An update writes only the columns that changed             |
      | Lines are matched by key                  | a line still in the list is updated in place and keeps its key                                              | owned-collections: Changing the lines of an Invoice read writes the difference |
      | A line left out of the list is deleted    | the list is the whole truth about the lines: a line removed from it is deleted, with no policy to configure | owned-collections: Changing the lines of an Invoice read writes the difference |
      | Changing some lines is changing the list  | to remove some lines, filter the list in F#; the lines kept stay as they were                               | owned-collections: Changing the lines of an Invoice read writes the difference |
      | A line's error is the order's field error | a line's broken check comes back as the order's field error Lines[1].UnitPrice, and nothing is written      | constraints: A broken column check is the field's error                        |
      | Deleting an order deletes its lines       | Tx.delete of an order read deletes the lines it owns                                                        | owned-collections: Deleting an Invoice deletes its lines                       |
