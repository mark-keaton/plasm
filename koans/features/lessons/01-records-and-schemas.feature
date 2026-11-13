# Lesson 01 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About records and schemas
  Domain types are ordinary immutable F# records and single-case unions. Persistence lives in separate schema values and scalar mappings, and a model is validated once, before any query runs.

  Prerequisites: none.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                  | the learner discovers                                                                            | teaches                                                           |
      | A record needs nothing from Plasm     | persistence metadata lives in a schema value, not on the record                                  | elegance: Persistence metadata does not require mutable records   |
      | Wrapped keys keep their meaning       | Scalar.wrap maps CustomerId to a bigint and back, and CustomerId cannot be compared with OrderId | elegance: Domain scalar wrappers preserve their meaning           |
      | Stored values can be rejected         | Scalar.tryWrap refuses a stored value the domain forbids, with the field named                   | elegance: Domain reconstruction can fail explicitly               |
      | Absent values are options             | a nullable column is an option field; None is not an empty string                                | core: Nullable values remain explicit                             |
      | A model reports every mistake at once | Model.build lists every configuration problem with its model and field                           | core: Unsupported model representations fail during configuration |
