# Behaviour specification: a Plasm provider for PGlite.
# The second provider the architecture audits asked for: it proves the
# IProvider / ISqlDialect boundary with a different transport (JavaScript
# interop instead of Npgsql) while the SQL stays PostgreSQL's.
Feature: Plasm runs against PGlite
  As a Plasm maintainer
  I want a provider that runs Plasm against PGlite
  So that Plasm can run in a browser and the provider boundary is proven by a second implementation

  Rule: The provider passes the acceptance scenarios the koans teach

    Scenario: Every acceptance scenario a koan teaches passes against PGlite
      Given the acceptance scenarios named by the koans' lesson specifications
      When they are run with the PGlite provider instead of Npgsql
      Then every one of them passes

    Scenario: Values cross the boundary without loss
      When rows with every column type the koans use are written and read through the PGlite provider
      Then every value reads back equal, including decimals, timestamps, options and arrays

    Scenario: Diagnostics report the same commands
      When a koan runs with the PGlite provider
      Then the commands it reports are the same as with Npgsql

  Rule: What PGlite cannot do is explicit

    Scenario: Capabilities PGlite lacks are refused before execution
      When an operation needs a capability the PGlite provider does not declare
      Then it fails before execution as unsupported by the provider, naming the capability
