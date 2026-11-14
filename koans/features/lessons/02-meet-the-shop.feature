# Lesson 02 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: Meet the shop
  The koans database is a small shop: customers place orders, each order has lines, and each line is for a product. Every koan starts from the same data, and its changes vanish when it ends.

  Prerequisites: lesson 01.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                              | the learner discovers                                                                                | teaches                                                                  |
      | The shop has three customers      | k.Repo.All(Query.from Db.customers) reads every customer, each as a Customer record                  | core: Querying a mapped property uses its declared type                  |
      | Counting rows                     | k.Repo.Count counts a table's rows without reading them                                              | aggregates: Counting a filtered query                                    |
      | The database numbers the rows     | the database gives each row a key in the order the starting data inserts it, so Bob is CustomerId 2L | elegance: Domain scalar wrappers preserve their meaning                  |
      | An order belongs to a customer    | an order's CustomerId names the customer who placed it; Ada has placed two orders                    | core: Client-side processing is an explicit boundary                     |
      | An order may have no customer     | order 4 has no customer, so its CustomerId is None                                                   | core: Nullable values remain explicit                                    |
      | A koan's changes stay in the koan | a customer inserted in a koan is there for the rest of that koan                                     | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
      | The next koan starts afresh       | the customer the previous koan inserted is gone: every koan starts from the starting data            | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
