# Executable acceptance specification (Reqnroll): a step tried under a
# savepoint, its failure a value the transaction goes on from. Written before
# the implementation.
@implemented
Feature: A transaction can try a step and go on when it fails
  As an F# application developer writing a transaction that has a way out
  I want to try a step, and on its failure do something else in the same transaction
  So that an insert that breaks a constraint need not end the transaction

  Tx.attempt runs a step under a savepoint and gives a Result: Ok with what
  the step gave, its writes kept (RELEASE SAVEPOINT), or Error with its
  failure, everything the step wrote undone (ROLLBACK TO SAVEPOINT) and the
  transaction able to go on. Attempts nest, each its own savepoint. Any
  failure of the step is a value, a Conflict and a rejection included,
  except those that end the transaction itself: a serialization failure, a
  deadlock and a lost connection stay the transaction's failure, so that a
  retry policy can run it again. Steps an attempt rolled back are not among
  those a later failure reports as rolled back.

  A transaction a retry policy runs again runs every step again: a step of
  your own (Tx.run) runs once per attempt, its database work undone and done
  again with the rest. An effect that must happen once belongs after
  Transact returns Ok, or in a row the transaction writes for it.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A tried step's failure is a value

    Scenario: A duplicate tried under Tx.attempt is a value, and the transaction goes on
      Given a Customer with the email "ada@example.com"
      When a transaction tries to insert another Customer with the email "ada@example.com", and on its failure inserts one with "ada.2@example.com"
      Then the attempt gave the Email field's error
      And the transaction committed, with the Customer "ada.2@example.com" and no other new one

    Scenario: A tried step that succeeds is kept
      When a transaction tries to insert a Customer with the email "bo@example.com"
      Then the attempt gave the inserted Customer
      And the Customer "bo@example.com" is stored

    Scenario: The savepoint is reported with the statements
      Given a Customer with the email "ada@example.com"
      When a transaction tries to insert another Customer with the email "ada@example.com", and on its failure inserts one with "ada.2@example.com"
      Then OnCommand reported a SAVEPOINT before the failed INSERT and a ROLLBACK TO that savepoint after it, of Kinds Begin and Rollback

    Scenario: A tried step's Conflict is a value, and what it wrote is undone
      Given an Order changed by another writer since it was read
      When a transaction tries, in one attempt, to insert a Customer and then update the Order from the copy read before
      Then the attempt gave a Conflict on the Order
      And the Customer the attempt inserted is not stored

    Scenario: Attempts nest, each undoing only its own writes
      When a transaction tries an attempt that inserts a Customer and then tries, inside it, a step that fails
      Then the inner attempt gave its failure and the outer one succeeded
      And the Customer the outer attempt inserted is stored

    Scenario: A failure that ends the transaction is not caught
      When a transaction tries a step that fails as a serialization failure
      Then the transaction fails as a serialization failure, not as a value of the attempt

  Rule: A retried transaction runs its own steps again

    Scenario: A step of your own runs once per attempt, its database work undone and done again
      When a transaction with a retry policy inserts a Customer in a step of its own and fails as a serialization failure the first time it runs
      Then the transaction committed on its second attempt
      And the step of its own ran twice
      And one Customer was stored by it, not two
