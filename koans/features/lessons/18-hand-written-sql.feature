# Lesson 18 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About hand-written SQL
  When a query is easier in SQL, write SQL; interpolated values are always parameters and rows still come back typed.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                      | the learner discovers                                            | teaches                                            | hint                                                                                                                                                                                       |
      | A hand-written SELECT returns records     | repo.Sql<Product> reads each row into a Product                  | raw-sql: Reading mapped records from SQL           | Read the sku column of the products table under Given, in id order.                                                                                                                        |
      | No matching rows is an empty list         | a SELECT that matches nothing returns []                         | raw-sql: Reading mapped records from SQL           | Look for that sku in the products table under Given: repo.Sql gives a list of the rows it read.                                                                                            |
      | SQL can return a single number            | repo.Sql<int64> reads a one-column row as a number               | raw-sql: Reading tuples and scalars from SQL       | repo.Sql always gives a list, here of a single row: the count of the products table under Given, as an int64.                                                                              |
      | SQL can return pairs                      | a two-column row becomes a tuple, by position                    | raw-sql: Reading tuples and scalars from SQL       | Read sku and name in the first row of the products table under Given, as a tuple.                                                                                                          |
      | An interpolated value becomes a parameter | the value is sent beside the SQL, never inside it                | raw-sql: Interpolated values are always parameters | Find the name of the product with that sku in the products table under Given, and the comment says whether a parameter's value is ever pasted into the SQL text.                           |
      | SQL can return mapped records             | repo.Sql<Payment> reads rows into Payments by column name        | raw-sql: Reading mapped records from SQL           | List the ids of the rows of the payments table under Given whose amount is at least the minimum, each as a PaymentId.                                                                      |
      | SQL can return tuples and scalars         | rows become tuples by position, or single values                 | raw-sql: Reading tuples and scalars from SQL       | Read the customers' names in alphabetical order, then find the status that sorts first alphabetically in the orders table under Given and pair it with the count of its rows, as an int64. |
      | Interpolation is parameters               | an interpolated value is a parameter, so injection is impossible | raw-sql: Interpolated values are always parameters | The whole text of name is sent as a single parameter, so ask which customer has exactly that name, and what stands in its place in the SQL.                                                |
      | A fragment inside a typed query           | Raw.fragment adds literal SQL to a plasm { } query               | raw-sql: A fragment inside a typed query           | ILIKE matches text whatever its case, and a% is any text that starts with an a, so look for such a name in the customers table under Given.                                                |
      | Commands return affected rows             | Tx.executeSql, a transaction step, returns how many rows changed | raw-sql: Executing hand-written commands           | Count the rows of the orders table under Given whose status is published, as a plain int.                                                                                                  |
      | Hand-written SQL inside a transaction     | Tx.sql reads, inside the transaction, what an earlier step wrote | raw-sql: Hand-written SQL inside a transaction     | Count the rows of the payments table under Given that are left once the failed rows are deleted, as an int64.                                                                              |
