# Behaviour specification: a Plasm provider for PGlite.
# The second provider the architecture audits asked for: it proves the
# IProvider / ISqlDialect boundary with a different transport (JavaScript
# interop instead of Npgsql) while the SQL stays PostgreSQL's.
Feature: Plasm runs against PGlite
  As a Plasm maintainer
  I want a provider that runs Plasm against PGlite
  So that Plasm can run in a browser and the provider boundary is proven by a second implementation

  Background:
    Given the koans page has finished loading

  Rule: The provider supports everything the koans teach

    Scenario: Every acceptance scenario a koan teaches holds against PGlite
      Each koan traces to an acceptance scenario of Plasm, so every koan passing
      against PGlite exercises every scenario the koans teach.
      When every koan is run with its reference answer against PGlite in the page
      Then every koan passes

    Scenario: Values cross the boundary without loss
      When rows with every column type the koans use are written and read through the PGlite provider and through Npgsql
      Then every value reads back the same, including decimals, timestamps, options and arrays

    Scenario: Diagnostics report the same commands
      When the same reads and writes run through the PGlite provider and through Npgsql
      Then the commands they report are the same

  Rule: PGlite's single connection is explicit

    Scenario: Work started while a transaction holds the connection waits for it
      When a read starts in the page while a transaction is still open
      Then the read completes after the transaction ends
      And it sees the transaction's committed rows
