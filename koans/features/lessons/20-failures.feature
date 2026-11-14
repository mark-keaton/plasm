# Lesson 20 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About failures
  Expected failures are values you can match on; Plasm's error cases are always qualified, so your domain keeps its own names.

  Prerequisites: lesson 11, lesson 13.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                            | the learner discovers                                                         | teaches                                                                   |
      | Failures are values             | a read through an ended sandbox returns an error instead of throwing          | error-model: Using a sandbox's repository after the sandbox ended         |
      | Error cases are qualified       | DbError.Database never collides with a domain case called Database            | error-model: Domain unions keep their own case names                      |
      | Unreadable rows are named       | a stored value the model rejects names its model and field                    | error-model: A written row that cannot be read back                       |
      | SQL mismatches name the column  | a hand-written query whose column does not fit names that column              | raw-sql: Mismatched results fail with the column identified               |
      | Too many rows is about the data | One over several rows returns DbError.MoreThanOneRow, not a translation error | reading: More than one matching row is reported as a property of the data |
      | Rejections keep their type      | Tx.fail with a domain error gives it back with rejection.As<'R>()             | error-model: A step's rejection reason comes back with its type           |
      | Every union is qualified        | Plasm's union cases never take your domain's case names                       | error-model: <union> cases must be qualified                              |
