# Lesson 20 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
# Caveat: there are no prepared projections yet.
Feature: About prepared queries
  A hot query can be built and planned once and bound to new arguments on each call.

  Prerequisites: lesson 07.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                     | the learner discovers                                                  | teaches                                                                            | hint                                                                                                                                                         |
      | A prepared query takes an argument       | Query.prepare turns a query with an Arg into a function of its value   | prepared-queries: A prepared query is built once and runs with different arguments | Find the name of the product with that sku in the products table under Given.                                                                                |
      | One prepared query, two values           | the same prepared query runs with a different value on each call       | prepared-queries: A prepared query is built once and runs with different arguments | Find the name for each sku in the products table under Given.                                                                                                |
      | A prepared query can be counted          | repo.Count takes a prepared query like any other query                 | prepared-queries: Prepared queries can be inspected and counted                    | Look for Cy's id in the customer_id column of the orders table under Given.                                                                                  |
      | Built once, run many times               | Query.prepare builds the query once; each call only binds the argument | prepared-queries: A prepared query is built once and runs with different arguments | Count the rows of the orders table under Given for each customer_id, and the argument travels as a parameter, so ask what is left to differ in the SQL text. |
      | Arguments are parameters                 | Arg values are always sent as parameters of their declared type        | prepared-queries: Arguments are typed and always parameters                        | The koan's name says how an argument reaches the database, and lesson 06 showed whether a parameter's value is in the SQL text.                              |
      | Paging by arguments                      | take size.Value and skip offset.Value bind per call                    | prepared-queries: Paging arguments                                                 | In id order, skip as many orders as the second argument says and take as many as the first argument says.                                                    |
      | Prepared queries still compose           | extending a prepared query applies the extension everywhere            | prepared-queries: Extending a prepared query applies the extension everywhere      | Count Ada's rows in the orders table under Given that are published.                                                                                         |
      | Arguments are read only inside the query | reading .Value while building is refused with an explanation           | prepared-queries: Arguments are only read inside query operations                  | The koan's name says where an argument may be read, and the comment marks where this code reads it.                                                          |
      | An exists takes an argument too          | an exists inside a prepared query reads its arguments too              | query-exists: The inner query takes a prepared argument too                        | For each amount, list the order_id of the rows of the payments table under Given that are at least that much, once each and in order, as OrderIds.           |
