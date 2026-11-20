# Lesson 03 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: Meet the shop
  The koans database is a small shop: customers place orders, each order holds its lines, and each line is for a product. Every koan starts from the same data, and its changes vanish when it ends.

  Prerequisites: lesson 02.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                              | the learner discovers                                                                                | teaches                                                                  | hint                                                                                                                                       |
      | The shop has three customers      | repo.All(Query.from (tableOf<Customer>)) reads every customer, each as a Customer record             | core: Querying a mapped property uses its declared type                  | Read the names in the customers table under Given, in alphabetical order.                                                                  |
      | Counting rows                     | repo.Count counts a table's rows without reading them                                                | aggregates: Counting a filtered query                                    | The comment says how many products the starting data has, and a count is written with an L.                                                |
      | The database numbers the rows     | the database gives each row a key in the order the starting data inserts it, so Bob is CustomerId 2L | elegance: Domain scalar wrappers preserve their meaning                  | Find Bob's place in the customers table under Given, and wrap that number as the comment says ids come back.                               |
      | An order belongs to a customer    | an order's CustomerId names the customer who placed it; Ada has placed two orders                    | core: Client-side processing is an explicit boundary                     | Find Ada's id in the customers table under Given, then count the rows of the orders table whose customer_id is that id.                    |
      | An order may have no customer     | order 4 has no customer, so its CustomerId is None                                                   | core: Nullable values remain explicit                                    | Look at customer_id in the fourth row of the orders table under Given, and recall from lesson 01 how an option says that nothing is there. |
      | An order holds its lines          | an Order's Lines is a plain list of its own OrderLines, read with the order                          | owned-collections: Reading an invoice reads its lines                    | Read product_id in the rows of order_lines that belong to the first order, and wrap each in the ProductId type the field has.              |
      | A koan's changes stay in the koan | a customer inserted in a koan is there for the rest of that koan                                     | sandbox: Changes inside a sandbox are visible inside and gone afterwards | Count the customers the database starts with, then add the one this koan inserts.                                                          |
      | The next koan starts afresh       | the customer the previous koan inserted is gone: every koan starts from the starting data            | sandbox: Changes inside a sandbox are visible inside and gone afterwards | This koan's sandbox starts from the customers table under Given, without the previous koan's insert.                                       |
