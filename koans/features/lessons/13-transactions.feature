# Lesson 13 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About transactions
  A transaction is a value made of steps, of type Tx<'T, 'E>: it gives a 'T, or fails with a rejection of its own error type 'E. Running it is explicit; it reads and writes inside one database transaction; the domain decides and the transaction writes what it decided; a failure says where its step is written; retries are opt-in.

  Prerequisites: lesson 11.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                             | the learner discovers                                                                                | teaches                                                                |
      | A transaction does nothing until run             | transaction { } builds a value; no command is sent until repo.Transact                               | core: Constructing a change performs no database operations            |
      | Running a transaction commits it                 | repo.Transact runs the steps and returns Ok with the result                                          | core: Transaction steps compose                                        |
      | An Error rolls it back                           | when a step fails, the steps before it are undone                                                    | core: A failed transaction identifies its failed step                  |
      | A step's result flows to the next                | let! gives the next step the record an earlier step stored                                           | core: Transaction steps compose                                        |
      | Steps compose with let!                          | transaction { } chains steps whose results feed later ones                                           | core: Transaction steps compose                                        |
      | A failure says where its step is written         | the error gives the failed step's action and place, and everything is rolled back                    | typed-transactions: A failed step is reported at its file and line     |
      | Reading inside a transaction                     | Tx.one and Tx.count read inside the transaction, as steps                                            | typed-transactions: Tx.count counts without reading the rows           |
      | A missing row is a rejection                     | Tx.orReject turns a query that found nothing into a rejection of the transaction's own error type    | typed-transactions: A missing row becomes a rejection with Tx.orReject |
      | The domain decides, the transaction writes       | a plain domain function decides; Tx.ofResult turns its Error into a rejection, and its Ok is written | typed-transactions: A domain decision's Error becomes a rejection      |
      | An empty transaction is fine                     | transaction { return 42 } runs and does nothing                                                      | elegance: Empty transaction descriptions are valid                     |
      | Retries are opt-in                               | without a RetryPolicy, a serialisation failure is returned, not retried                              | elegance: Retry behavior is explicit                                   |
      | Custom steps use the transaction                 | Tx.run gets commands enlisted in the transaction, and cannot end it                                  | tx-steps: A custom step's commands run in the transaction              |
      | Effects outside the database are not rolled back | only database work is undone; send emails after Ok                                                   | core: Non-transactional effects are not presented as rollback-safe     |
