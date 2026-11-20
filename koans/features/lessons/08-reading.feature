# Lesson 08 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About reading
  The repository runs queries and returns Results. Counting and existence transfer no rows; projections read only the columns they use.

  Prerequisites: lesson 07.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                             | the learner discovers                                                             | teaches                                                 | hint                                                                                                                                                 |
      | All returns a list               | repo.All gives a Result holding an F# list of records                             | core: Querying a mapped property uses its declared type | Read the sku column of the products table under Given, in id order.                                                                                  |
      | One returns an option            | repo.One gives Some record when one row matches                                   | reading: A query matching no rows returns None          | A customer with that name is in the customers table under Given, so the option holds the name that Option.map picked.                                |
      | Count returns an int64           | repo.Count gives the number of matching rows as an int64                          | aggregates: Counting a filtered query                   | Count the rows of the products table under Given, and write the number the way the comment shows.                                                    |
      | Selecting one field              | select c.Name reads a list of names instead of whole Customers                    | core: Projection retrieves the requested data           | Read the name column of the customers table under Given, in id order.                                                                                |
      | Reading returns typed rows       | repo.All returns Result<Order list, DbError>, with fields of their declared types | core: Querying a mapped property uses its declared type | Read total and status in the first row of the orders table under Given: the total with an m, and the status as the OrderStatus case that text names. |
      | Counting transfers no rows       | repo.Count sends COUNT(*) and returns a number                                    | aggregates: Counting a filtered query                   | Count the published rows of the orders table under Given, and ask what SQL would ask for when only a number is wanted.                               |
      | Asking whether anything exists   | repo.Exists stops at the first matching row                                       | aggregates: Testing existence                           | Look for a total over a thousand in the orders table under Given.                                                                                    |
      | Selecting only what you need     | select reads only the columns the projection uses                                 | core: Projection retrieves the requested data           | The row is the pair the select names, read from the first row of the orders table under Given, and the SQL reads only the columns the select names.  |
      | Leaving the database is explicit | after the database work, ordinary F# list functions run in memory, visibly        | core: Client-side processing is an explicit boundary    | Add the amounts of the succeeded rows of the payments table under Given: the sum is worked out in memory, so count only the repo calls.              |
      | One means at most one            | repo.One keeps the query's take and ordering, and returns an Option               | reading: One keeps the query's own ordering and limit   | The first is the largest total in the orders table under Given, wrapped as an option holds a value, and the second query matches no row at all.      |
