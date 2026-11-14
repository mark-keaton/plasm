# Lesson 21 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About testing with Plasm
  Test against the real database inside sandboxes that always roll back, and check the model against the schema at start-up.

  Prerequisites: lesson 13.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                          | the learner discovers                                                       | teaches                                                                  |
      | A sandbox gives back its body's result        | repo.Sandbox runs a function with its own repository and returns its result | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
      | An insert is visible inside its sandbox       | inside the sandbox, the new Customer is counted                             | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
      | A sandbox's insert is gone afterwards         | once the sandbox ends, the new Customer is gone                             | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
      | A matching model has no schema issues         | CheckSchema finds no issues when the model fits the tables                  | schema-check: A model that matches the database passes                   |
      | A sandbox rolls everything back               | changes are visible inside the sandbox and gone afterwards                  | sandbox: Changes inside a sandbox are visible inside and gone afterwards |
      | Transactions keep their meaning in a sandbox  | a failed transaction inside a sandbox is still rolled back                  | sandbox: Transactions inside a sandbox keep their meaning                |
      | A database error does not end a sandbox       | after a constraint violation, later work succeeds                           | sandbox: A database error does not break the sandbox                     |
      | The model can be checked against the database | CheckSchema returns no issues for a matching model                          | schema-check: A model that matches the database passes                   |
      | Drift is reported precisely                   | a missing column is reported with its model and field                       | schema-check: Missing tables and columns are reported                    |
