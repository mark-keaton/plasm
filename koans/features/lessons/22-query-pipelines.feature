# Lesson 22 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About query pipelines
  Every plasm { } operation has a pipeline form; both produce the same query.

  Prerequisites: lesson 07, lesson 14, lesson 15.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                | the learner discovers                                                             | teaches                                                         |
      | Filtering in a pipeline             | Query.where takes the query last, so it follows Query.from with the pipe operator | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Ordering in a pipeline              | Query.orderBy sorts the rows of a pipeline                                        | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Taking in a pipeline                | Query.take keeps the first rows of an ordered pipeline                            | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Selecting in a pipeline             | Query.select ends a pipeline with the values it projects                          | query-pipeline: <operation> in a pipeline produces the same SQL |
      | A pipeline filter is a plasm filter | Query.where and plasm's where send the same SQL                                   | query-pipeline: <operation> in a pipeline produces the same SQL |
      | A pipeline select is a plasm select | both forms of select return the same rows                                         | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Pipelines and expressions agree     | Query.from \|> Query.where \|> Query.orderBy equals the same plasm { }            | query-pipeline: <operation> in a pipeline produces the same SQL |
      | Pipelines join as often as you like | Query.join chains, giving ((order, line), product)                                | query-pipeline: Chaining joins in a pipeline                    |
