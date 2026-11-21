# Executable acceptance specification (Reqnroll): what a provider must supply.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit, which found PostgreSQL SQL in the core.
@implemented
Feature: Database-specific SQL comes from the provider
  As an author of a provider for another database
  I want database-specific constructs to come from the dialect or be guarded by a capability
  So that a second provider needs as few changes to the core library as possible

  These scenarios use a test dialect that differs from PostgreSQL. Queries are
  only explained (translated, never executed), so no second database is needed.
  Not everything is behind the dialect yet: LIMIT/OFFSET, the catalogue
  queries and the many-to-many link SQL are still PostgreSQL's
  (see docs/status.md).

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And a test dialect without array parameters, upserts, RETURNING or row locking, with its own null-safe comparison and string length

  Rule: Constructs the dialect can express are rendered by it

    Scenario: Relationship loads without array parameters send one parameter per key
      When I explain a query for Orders, which own their Lines, with the test dialect
      Then the load command matches keys with an IN list of parameters

    Scenario: Null-safe comparisons are rendered by the dialect
      When I explain a query comparing an optional field with <> with the test dialect
      Then the comparison uses the dialect's null-safe operator

    Scenario: String functions are named by the dialect
      When I explain a query filtering on the length of a name with the test dialect
      Then the length uses the dialect's function name

    Scenario: A write's every-column check compares by the dialect's null-safe operator
      Given a dialect that is PostgreSQL's, its null-safe comparison spelled its own way
      When a Customer read through it is renamed
      Then the UPDATE compares the columns it was read with by the dialect's null-safe operator alone

  Rule: Constructs the dialect cannot express are refused before execution

    Scenario Outline: <operation> needs <capability>
      When I attempt <operation> with the test dialect
      Then it fails before execution as unsupported by the provider, naming <capability>

      Examples:
        | operation                  | capability    |
        | an upsert                  | upserts       |
        | an insert                  | RETURNING     |
        | deleting an Order and its Lines | row locking   |
        | changing an Article's links     | array parameters |

  Rule: An unsupported operation is reported the same way by every write step

    Scenario Outline: <call> reports an unsupported operation as UnsupportedByProvider
      When I attempt an operation the test dialect cannot perform through <call>
      Then the failure contains TranslationError.UnsupportedByProvider naming the missing capability

      Examples:
        | call               |
        | an insert step     |
        | a bulk write step  |
