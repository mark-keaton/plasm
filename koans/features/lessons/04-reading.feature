# Lesson 04 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About reading
  The repository runs queries and returns Results. Counting and existence transfer no rows; projections read only the columns they use.

  Prerequisites: lesson 03.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                             | the learner discovers                                                             | teaches                                                 |
      | Reading returns typed rows       | repo.All returns Result<Order list, DbError>, with fields of their declared types | core: Querying a mapped property uses its declared type |
      | Counting transfers no rows       | repo.Count sends COUNT(*) and returns a number                                    | aggregates: Counting a filtered query                   |
      | Asking whether anything exists   | repo.Exists stops at the first matching row                                       | aggregates: Testing existence                           |
      | Selecting only what you need     | select reads only the columns the projection uses                                 | core: Projection retrieves the requested data           |
      | Leaving the database is explicit | after the database work, ordinary F# list functions run in memory, visibly        | core: Client-side processing is an explicit boundary    |
      | One means at most one            | repo.One keeps the query's take and ordering, and returns an Option               | reading: One keeps the query's own ordering and limit   |
