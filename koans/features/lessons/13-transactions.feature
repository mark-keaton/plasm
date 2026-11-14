# Lesson 13 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About transactions
  A transaction is a value made of steps. Running it is explicit; a failure says where its step is written; retries are opt-in.

  Prerequisites: lesson 11.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                             | the learner discovers                                                   | teaches                                                            |
      | A transaction does nothing until run             | transaction { } builds a value; no command is sent until repo.Transact  | core: Constructing a change performs no database operations        |
      | Running a transaction commits it                 | repo.Transact runs the steps and returns Ok with the result             | core: Transaction steps compose                              |
      | An Error rolls it back                           | when a step fails, the steps before it are undone                       | core: A failed transaction identifies its failed step              |
      | A step's result flows to the next                | let! gives the next step the record an earlier step stored              | core: Transaction steps compose                              |
      | Steps compose with let!                          | transaction { } chains steps whose results feed later ones        | core: Transaction steps compose                              |
      | A failure says where its step is written         | the error gives the failed step's action and place, and everything is rolled back | core: A failed transaction identifies its failed step              |
      | An empty transaction is fine                     | transaction { return () } runs and does nothing                         | elegance: Empty transaction descriptions are valid                 |
      | Retries are opt-in                               | without a RetryPolicy, a serialisation failure is returned, not retried | elegance: Retry behavior is explicit                               |
      | Custom steps use the transaction                 | Tx.run gets commands enlisted in the transaction, and cannot end it     | tx-steps: A custom step's commands run in the transaction          |
      | Effects outside the database are not rolled back | only database work is undone; send emails after Ok                      | core: Non-transactional effects are not presented as rollback-safe |
