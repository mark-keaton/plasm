# Lesson 18 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About query pipelines
  Every plasm { } operation has a pipeline form; both produce the same query.

  Prerequisites: lesson 03, lesson 10, lesson 11.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                | the learner discovers                                                  | teaches                                                         |
      | Pipelines and expressions agree     | Query.from \|> Query.where \|> Query.orderBy equals the same plasm { } | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Pipelines join as often as you like | Query.join chains, giving ((order, line), product)                     | query-pipeline: Chaining joins in a pipeline                    |
