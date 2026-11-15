# Lesson 04 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: Plasm's promises
  Plasm keeps a few promises everywhere: plain records, nothing run until asked, no hidden or in-memory work, failures as values, minimal writes, a checked model, F# meaning, batched loads and tests that roll back. Each koan shows one and names the lesson that goes deeper.

  Prerequisites: lesson 03.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                  | the learner discovers                                                                   | teaches                                                               |
      | Records stay plain F#                 | a record read from the database is an ordinary value: changing a copy sends nothing     | elegance: Persistence metadata does not require mutable records       |
      | Nothing runs until you ask            | describing a query and an insert sends no command                                       | core: Constructing a change performs no database operations           |
      | No hidden queries                     | an order's lines come with it, and reading them is reading a list: nothing more is sent | owned-collections: Reading an invoice reads its lines                 |
      | Nothing is evaluated behind your back | code Plasm cannot turn into SQL is an Error, and no rows are read to run it in memory   | core: Unsupported translation is explicit                             |
      | A failure is a value                  | a duplicate email comes back as an Error naming the Email field; nothing is thrown      | constraints: A duplicate of a unique field is the field's error       |
      | Only what you change is written       | an update sets only the fields that differ from the record read                         | typed-transactions: An update writes only the columns that changed    |
      | The model is checked before any query | building a model reports what it cannot store, before any query runs                    | core: Unsupported model representations fail during configuration     |
      | Queries mean what F# means            | = None in a query is IS NULL in SQL, and matches the rows = None matches in F#          | elegance: Null comparisons have explicit semantics                    |
      | What an order owns loads in one batch | reading every order takes one command for the orders and one for all their lines        | owned-collections: Every invoice's lines are read in one more command |
      | Tests roll back                       | a sandbox's changes are seen inside it and gone when it ends                            | sandbox: A sandbox inside a sandbox rolls back to the outer sandbox   |
