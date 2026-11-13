# Behaviour specification: PostgreSQL in the page, used by Plasm.
# PGlite is PostgreSQL compiled to WebAssembly. Plasm reaches it through a
# provider (IProvider and ISqlDialect), not through Npgsql, which needs sockets.
Feature: The koans database runs in the browser
  As a learner
  I want the koans to use a real PostgreSQL database in the page
  So that what I learn is what Plasm does against PostgreSQL

  Background:
    Given the koans page has finished loading

  Rule: The data is the same as the terminal version's

    Scenario: The database starts with the koans' schema and starting data
      When the learner runs the koan "Wrapped keys keep their meaning" with its reference answer
      Then it passes, reading the same customers as the terminal version

    Scenario: Every koan starts from the starting data
      When the learner runs a koan that inserts, updates and deletes rows, twice
      Then it passes both times
      And the next koan sees the starting data

  Rule: Plasm behaves as it does against a PostgreSQL server

    Scenario: The SQL Plasm generates runs unchanged
      When a query with joins, aggregates and relationship loads is run in the page
      Then the same SQL text is sent as against a PostgreSQL server
      And the same rows come back

    Scenario: Database failures are classified as on a server
      When the learner runs a koan that violates a unique constraint
      Then the failure is a unique violation naming the constraint

    Scenario: Transactions and savepoints behave as on a server
      When a transaction with a failing step and a nested sandbox run in the page
      Then the failing step's changes are rolled back and the sandbox's changes are undone

    Scenario: Overlapping work shares the one connection safely
      When several reads start at once in the page
      Then they all complete with correct results, one after another
