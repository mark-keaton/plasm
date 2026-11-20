# Lesson 22 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About testing with Plasm
  Test against the real database inside sandboxes that always roll back. Check the model against the schema and every query value against the model at start-up or in a test, before anything runs, and print the tables the model expects.

  Prerequisites: lesson 13.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                          | the learner discovers                                                                                            | teaches                                                                  | hint                                                                                                                                           |
      | A sandbox gives back its body's result        | repo.Sandbox runs a function with its own repository and returns its result                                      | sandbox: Changes inside a sandbox are visible inside and gone afterwards | The sandbox returns what customers returns: the count of the customers table under Given.                                                      |
      | An insert is visible inside its sandbox       | inside the sandbox, the new Customer is counted                                                                  | sandbox: Changes inside a sandbox are visible inside and gone afterwards | Inside the sandbox the inserted customer is counted with the rows of the customers table under Given.                                          |
      | A sandbox's insert is gone afterwards         | once the sandbox ends, the new Customer is gone                                                                  | sandbox: Changes inside a sandbox are visible inside and gone afterwards | The comment says the sandbox has ended, and the first koan of this lesson said what a sandbox does with its changes.                           |
      | Every query is checked before it runs         | Repo.Start translates every query value of the shop, runs none, and under Warn names the one it cannot translate | query-check: Warned problems are reported and the repository starts      | The comment names the query that does not translate and the module Shop.fs is, and a check sends nothing to the database. |
      | The model prints the tables it expects        | repo.Ddl() prints the tables the model expects, with the constraint names Plasm maps errors by, and runs nothing | ddl: Uniqueness and references carry their names                         | Lesson 11's comments gave these constraints the same default names, and the comment here says what Ddl runs.                                   |
      | Transactions keep their meaning in a sandbox  | a failed transaction inside a sandbox is still rolled back                                                       | sandbox: Transactions inside a sandbox keep their meaning                | The transaction ends in Tx.fail, as in lesson 13, so its insert is rolled back and the customers table is as under Given.                      |
      | A database error does not end a sandbox       | after a constraint violation, later work succeeds                                                                | sandbox: A database error does not break the sandbox                     | The first insert repeats Ada's email, which is unique, and the koan's name says whether the sandbox can still be used afterwards.              |
      | The model can be checked against the database | CheckSchema returns no issues for a matching model                                                               | schema-check: A model that matches the database passes                   | Lesson 02's koan 'A repo pairs a model with a database' found what CheckSchema lists for this database, and here the Result is compared whole. |
      | Drift is reported precisely                   | a missing column is reported with its model and field                                                            | schema-check: Missing tables and columns are reported                    | The column dropped is the order's cancellation reason, and the comment says what each part of a SchemaIssue holds. |
