# Lesson 06 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About query values
  A query is a value: building, extending and inspecting it never touches the database. Execution happens only at the repository.

  Prerequisites: lesson 05.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                              | the learner discovers                                                         | teaches                                                    |
      | Query.from reads every row        | Query.from (tableOf<Customer>) is the plainest query: every Customer                 | core: Constructing a query performs no database operations |
      | A where keeps the matching rows   | one where clause keeps only the rows for which its condition is true          | core: Reusable filters compose                             |
      | An orderBy sorts the rows         | orderBy sorts the rows by a field, smallest first, in the database            | query-semantics: The ordering is done by the database      |
      | take keeps the first rows         | take n keeps the first n rows of an ordered query                             | elegance: Pagination requires stable ordering              |
      | Building a query runs nothing     | plasm { } builds a description; no command is sent                            | core: Constructing a query performs no database operations |
      | Extending makes a new query       | adding a filter returns a new query and leaves the original as it was         | core: Extending a query preserves the original query       |
      | Running does not change a query   | the same query value can be run again with the same meaning                   | core: Executing a query does not mutate its description    |
      | A query can explain itself        | repo.Explain shows the SQL and loads without database access                  | elegance: Loading plans can be inspected                   |
      | Reusable filters are functions    | an ordinary function from query to query composes with any other              | core: Reusable filters compose                             |
      | Captured values become parameters | a value from outside the query is sent as a parameter, never spliced into SQL | core: Captured values are treated as data                  |
