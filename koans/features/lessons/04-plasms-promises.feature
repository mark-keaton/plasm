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
      | koan                                  | the learner discovers                                                                   | teaches                                                               | hint                                                                                                                             |
      | Records stay plain F#                 | a record read from the database is an ordinary value: changing a copy sends nothing     | elegance: Persistence metadata does not require mutable records       | The with expression makes a copy and leaves widget as it was read, so count only the repo calls.                                 |
      | Nothing runs until you ask            | describing a query and an insert sends no command                                       | core: Constructing a change performs no database operations           | Look for a repo call in this koan: the comment says only those send anything.                                                    |
      | No hidden queries                     | an order's lines come with it, and reading them is reading a list: nothing more is sent | owned-collections: Reading an invoice reads its lines                 | Count the first order's rows in order_lines under Given, and then the commands the comment says a read of an order takes.        |
      | Nothing is evaluated behind your back | code Plasm cannot turn into SQL is an Error, and no rows are read to run it in memory   | core: Unsupported translation is explicit                             | The comment says Plasm refuses this query, so ask what kind of Result a refused query gives and whether it was sent.             |
      | A failure is a value                  | a duplicate email comes back as an Error naming the Email field; nothing is thrown      | constraints: A duplicate of a unique field is the field's error       | The comment says which of Customer's fields is unique and already taken, and the error names that field as the record spells it. |
      | Only what you change is written       | an update sets only the fields that differ from the record read                         | typed-transactions: An update writes only the columns that changed    | Compare the original and the changed record given to Tx.update: the comment says which fields the UPDATE sets.                   |
      | The model is checked before any query | building a model reports what it cannot store, before any query runs                    | core: Unsupported model representations fail during configuration     |                                                                                                                                  |
      | Queries mean what F# means            | = None in a query is IS NULL in SQL, and matches the rows = None matches in F#          | elegance: Null comparisons have explicit semantics                    | The comment says what the comparison becomes in SQL, and the orders table under Given shows which rows have no customer_id.      |
      | What an order owns loads in one batch | reading every order takes one command for the orders and one for all their lines        | owned-collections: Every invoice's lines are read in one more command | Add the command the comment says the lines take to the command that reads the orders.                                            |
      | Tests roll back                       | a sandbox's changes are seen inside it and gone when it ends                            | sandbox: A sandbox inside a sandbox rolls back to the outer sandbox   | Inside the sandbox Dan is counted with the starting customers, and after it ends only the customers table under Given is left.   |
