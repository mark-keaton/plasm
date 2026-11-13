# Lesson 09 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About transactions
  A transaction is a value made of named steps. Running it is explicit; a failure names its step; retries are opt-in.

  Prerequisites: lesson 07.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                             | the learner discovers                                                   | teaches                                                            |
      | Steps compose with let!                          | transaction { } chains named steps whose results feed later ones        | core: Named transaction steps compose                              |
      | A failure names its step                         | the error says which step failed and everything is rolled back          | core: A failed transaction identifies its failed step              |
      | An empty transaction is fine                     | transaction { return () } runs and does nothing                         | elegance: Empty transaction descriptions are valid                 |
      | Retries are opt-in                               | without a RetryPolicy, a serialisation failure is returned, not retried | elegance: Retry behavior is explicit                               |
      | Custom steps use the transaction                 | Tx.run gets commands enlisted in the transaction, and cannot end it     | tx-steps: A custom step's commands run in the transaction          |
      | Effects outside the database are not rolled back | only database work is undone; send emails after Ok                      | core: Non-transactional effects are not presented as rollback-safe |
