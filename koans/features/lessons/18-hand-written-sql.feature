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
      | koan                              | the learner discovers                                            | teaches                                            |
      | SQL can return mapped records     | repo.Sql<Order> reads rows into Orders by column name            | raw-sql: Reading mapped records from SQL           |
      | SQL can return tuples and scalars | rows become tuples by position, or single values                 | raw-sql: Reading tuples and scalars from SQL       |
      | Interpolation is parameters       | an interpolated value is a parameter, so injection is impossible | raw-sql: Interpolated values are always parameters |
      | A fragment inside a typed query   | Raw.fragment adds literal SQL to a plasm { } query               | raw-sql: A fragment inside a typed query           |
      | Commands return affected rows     | repo.ExecuteSql returns how many rows changed                    | raw-sql: Executing hand-written commands           |
