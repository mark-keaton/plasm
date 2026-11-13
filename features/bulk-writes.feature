# Executable acceptance specification (Reqnroll): set-based writes and upserts.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Bulk writes and upserts
  As an F# application developer
  I want to update, delete and insert many rows with one statement
  So that set-based changes do not require loading every record into memory

  Bulk writes are values like queries: they can be inspected before they run,
  and run by the repository or as a transaction step. They are set-based, so
  they do not run changeset validations; database constraints still apply.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Updates and deletes are driven by typed queries

    Scenario: Updating every row a query matches
      Given 3 draft Orders and 2 published Orders
      When I cancel every draft Order with one bulk update
      Then 3 rows are reported as updated
      And the draft Orders are now cancelled and the published Orders are unchanged
      And the update runs as a single database command filtered by the query

    Scenario: Bulk updates can compute from the current row
      Given 3 draft Orders and 2 published Orders
      When I add 10 to the total of every published Order
      Then each published total is computed by the database from its old value
      And fields not mentioned in the update are not written

    Scenario: Bulk updates respect optimistic concurrency
      Given an Order loaded by two independent workflows
      When one workflow bulk-cancels it
      And the other workflow saves a changeset based on the old version
      Then the second save is rejected as stale

    Scenario: Deleting every row a query matches
      Given 3 draft Orders and 2 published Orders
      When I delete every draft Order with one bulk delete
      Then 3 rows are reported as deleted
      And only the published Orders remain

    Scenario: Bulk writes are explicit about what they bypass
      Given 3 draft Orders and 2 published Orders
      When a bulk update would violate a database constraint for one row
      Then the bulk update fails with a classified constraint violation
      And no row was changed

    Scenario: Unsupported bulk requests are rejected before execution
      When I request a bulk update with an ordering or a limit
      Then the request is rejected with an explanation
      When I request a bulk update that changes the primary key
      Then the request is rejected with an explanation
      And no database command is executed

  Rule: Inserts and upserts take many records at once

    Scenario: Inserting many records in one command
      When I insert 3 Products with one bulk insert
      Then the 3 Products are returned with their generated keys
      And the insert runs as a single database command

    Scenario: Upserting on a unique key
      Given a Product "SKU-1" named "Old name"
      When I upsert Products "SKU-1" named "New name" and "SKU-2" named "Second" on the SKU
      Then "SKU-1" keeps its key and is now named "New name"
      And "SKU-2" is inserted
      And the upsert runs as a single database command

    Scenario: Inserting only what is missing
      Given a Product "SKU-1" named "Old name"
      When I insert Products "SKU-1" and "SKU-2" ignoring existing SKUs
      Then only "SKU-2" is inserted and returned
      And "SKU-1" is unchanged

  Rule: Bulk writes are values

    Scenario: Bulk writes can be inspected without database access
      When I describe a bulk update without running it
      Then its SQL can be inspected
      And inspection performs no database reads or writes

    Scenario: Bulk writes run inside transactions
      Given 3 draft Orders and 2 published Orders
      When a transaction bulk-cancels the drafts and then fails
      Then the transaction reports the failing step
      And the draft Orders are unchanged
