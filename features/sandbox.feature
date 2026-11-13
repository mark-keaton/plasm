# Executable acceptance specification (Reqnroll): rolled-back test sandboxes.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Test sandboxes
  As an F# application developer
  I want each test to run against the real database inside a transaction that is rolled back
  So that tests are isolated without recreating the database for each one

  A sandbox is a repository whose work happens inside one transaction that is
  always rolled back. Code under test uses it like any repository, including
  its own transactions.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Everything inside a sandbox is rolled back

    Scenario: Changes inside a sandbox are visible inside and gone afterwards
      When code inside a sandbox inserts a Customer and reads it back
      Then the Customer is visible inside the sandbox
      And after the sandbox ends the Customer does not exist

    Scenario: Transactions inside a sandbox keep their meaning
      When code inside a sandbox runs one transaction that succeeds and one that fails
      Then the successful transaction's changes are visible inside the sandbox
      And the failed transaction's changes are not
      And after the sandbox ends neither exists

    Scenario: A database error does not break the sandbox
      When code inside a sandbox hits a unique constraint violation and then continues
      Then the violation is reported as usual
      And later work in the same sandbox succeeds

    Scenario: Concurrent sandboxes are isolated
      When two sandboxes run at the same time and each inserts a Customer
      Then each sandbox sees only its own Customer
      And after both end neither Customer exists

    Scenario: Overlapping work in one sandbox is serialised
      When code inside a sandbox starts several reads at once
      Then they all complete with correct results
