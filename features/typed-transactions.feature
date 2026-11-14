# Executable acceptance specification (Reqnroll): typed transactions whose
# steps need no names. Written before the implementation, following core.feature.
@implemented
Feature: Transactions are typed, and their steps need no names
  As an F# application developer
  I want a transaction's rejections to have my own type, and its steps to be found by where they are written
  So that nothing about a step is stated twice and no rejection needs a cast

  A transaction is a Tx<'T, 'E>: it gives a 'T, or fails. Its own failures are
  rejections of type 'E; Plasm's are invalid changes, conflicts and database
  failures. A failure names the file and line of the step that failed.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A failure says where its step is written

    Scenario: A failed step is reported at its file and line
      When a transaction inserts a Customer and then fails at a later step
      Then the error names the file and line of the failing step
      And the insert is reported as rolled back, at its own line

  Rule: Rejections keep their type

    Scenario: A rejection has the transaction's error type
      When a transaction rejects with OutOfStock "widget"
      Then the failure is the rejection OutOfStock "widget", read without a cast

    Scenario: A custom step's Error is a rejection of the same type
      When a custom step returns Error (OutOfStock "gadget")
      Then the failure is the rejection OutOfStock "gadget", read without a cast

    Scenario: A domain decision's Error becomes a rejection
      When a transaction takes a decision that returns Error "too late" through Tx.ofResult
      Then the failure is the rejection Refused "too late", read without a cast

    Scenario: A rejection cannot be read as another type
      When I compile code that reads a transaction's rejection as another type
      Then the code does not compile

  Rule: Reading one row and counting are steps

    Scenario: Tx.one gives the only matching row
      Given a Customer named "Ada"
      When a transaction reads the Customer named "Ada" with Tx.one
      Then it gives that Customer

    Scenario: Tx.one gives None when nothing matches
      When a transaction reads the Customer named "Nobody" with Tx.one
      Then it gives None

    Scenario: A missing row becomes a rejection with Tx.orReject
      When a transaction reads the Customer named "Nobody" with Tx.one and Tx.orReject (Missing "customer")
      Then the failure is the rejection Missing "customer", read without a cast

    Scenario: Tx.count counts without reading the rows
      Given Customers named "Ada", "Bob" and "Cy"
      When a transaction counts the Customers with Tx.count
      Then it gives 3
      And the command it sent counts the rows

  Rule: Steps need no names

    Scenario: A transaction is written without naming its steps
      When I compile a transaction that inserts a Customer and reads it back, naming no step
      Then the code compiles
