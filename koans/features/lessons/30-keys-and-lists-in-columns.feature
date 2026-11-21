# Lesson 30 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About keys of several columns and lists in a column
  A record whose Id is a record has a key of several columns, as existing
  tables often have. A list of plain values is kept in its owner's row, as
  one array column, and queried with List.contains and List.isEmpty.

  Prerequisites: lesson 10, lesson 28.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                    | the learner discovers                                                                        | teaches                                                                               | hint                                                                                                                                       |
      | A key of several columns                | a record whose Id is a record is keyed by each of its fields, its table's own columns          | composite-keys: An Issue is read by its whole key                                     | Find the row of the stock table under Given in the north warehouse for WIDGET, and give its quantity.                                     |
      | Reading by one part of the key          | a query reads a part of the key as a column                                                  | composite-keys: Issues are read by one part of the key                                | Give the skus of the north warehouse's rows in the stock table under Given, alphabetically.                                               |
      | An update finds the row by every part   | Tx.update finds the row by every part of the key and its version                             | composite-keys: An Issue is renamed                                                    | The comment names the columns the UPDATE finds the row by; the quantity read back is the one the update wrote.                            |
      | A whole key has no order                | ordering by a whole key is refused, saying to order by its parts                             | composite-keys: Ordering by or comparing a whole key is refused                        | The comment says what such a query is refused with, and the text the refusal says.                                                        |
      | A list kept in its owner's row          | a list of strings is stored as one array column and read back whole                          | array-columns: An Issue is read back whole                                             | Give Saws' keywords from the categories table under Given, in the order the array holds them.                                             |
      | Filtering by a value in a list          | List.contains over an array field is = ANY in SQL                                            | array-columns: Filtering by a value in an array                                       | Give the names of the categories under Given whose keywords include cutting, alphabetically.                                               |
      | An empty list is an empty array         | List.isEmpty over an array field tests its cardinality, an empty list being an empty array   | array-columns: Filtering by an empty array                                            | Give the names of the categories under Given with no keywords.                                                                             |
      | Changing a list writes its column alone | an update to the list sets its column and no other                                           | array-columns: Relabelling an Issue writes the labels column and no other             | The comment says what the UPDATE's SET names; the keywords read back are the ones written.                                                |
