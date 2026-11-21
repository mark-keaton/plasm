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
  Not everything is behind the dialect yet: the catalogue queries and the
  many-to-many link SQL are still PostgreSQL's (see docs/status.md).

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And a test dialect without array parameters, upserts, RETURNING or row locking, with its own null-safe comparison and string length

  Rule: Constructs the dialect can express are rendered by it

    Scenario: Relationship loads without array parameters send one parameter per key
      When I explain a query for Orders, which own their Lines, with the test dialect
      Then the load command matches keys with an IN list of parameters

    Scenario: A prepared query's list argument is one parameter, as the dialect sends a list
      When I explain, with the test dialect, a prepared query of the Customers whose id is in a list argument
      Then the membership is the dialect's, of one parameter, whatever the list's length

    Scenario: A load under a key of several parts matches the keys as the dialect sends lists
      When I explain, with a dialect that sends lists as JSON, a query of the tickets, which own their notes under a key of two parts
      Then the notes' load matches both parts against the dialect's lists, one parameter a part

    Scenario: Null-safe comparisons are rendered by the dialect
      When I explain a query comparing an optional field with <> with the test dialect
      Then the comparison uses the dialect's null-safe operator

    Scenario: String functions are named by the dialect
      When I explain a query filtering on the length of a name with the test dialect
      Then the length uses the dialect's function name

    Scenario: Aggregates are named by the dialect
      When I explain, with the test dialect, whether any of each customer's Orders is published
      Then the aggregate is the dialect's name for it

    Scenario: An aggregate's empty list is the dialect's empty array
      When I explain, with the test dialect, each customer's Order totals in the order they were placed
      Then a customer with no Orders has the dialect's empty array

    Scenario: The least stored name of an ordered union is read back without an array
      When I explain, with the test dialect, the least status of the Orders
      Then the name is read back from its position by a CASE, with no array

    Scenario: A value stored as a type of the database's own is sent as the dialect casts it
      When I explain a query of the shipments whose status is Shipped, with a dialect that stores an enum as text
      Then the status is compared with its parameter as it is, cast to no type

    Scenario: A document's column is the type the dialect's JSON documents are
      When I print the DDL of the documented cards model, with a dialect whose JSON documents are json
      Then the cards' checklist is declared json

    Scenario: Rows skipped with no limit are paged as the dialect pages them
      When I explain a query of the Customers by name that skips one, with the test dialect, which needs a LIMIT before an OFFSET
      Then the query takes the dialect's unbounded limit before its offset

    Scenario: The DDL declares the dialect's column type, whatever names its catalogue accepts
      When I print the DDL with the test dialect, which accepts "character varying" before "text" for a string
      Then a Customer's name is declared "text"

    Scenario: A write's every-column check compares by the dialect's null-safe operator
      Given a dialect that is PostgreSQL's, its null-safe comparison spelled its own way
      When a Customer read through it is renamed
      Then the UPDATE compares the columns it was read with by the dialect's null-safe operator alone

  Rule: Constructs the dialect cannot express are refused before execution

    Scenario Outline: A model storing <what> is refused when the repository is made, by a dialect without <capability>
      When a repository is made with the test dialect for a model storing <what>
      Then it is refused when made, naming <fields> and saying it has no <capability>

      Examples:
        | what                     | capability    | fields                                                                  |
        | a status as an enum type | enum types    | Shipment.Status, Shipment.PreviousStatus                                |
        | lists of scalars         | array columns | Issue.Labels, Issue.Assignees, Issue.Points, Issue.Topics, Issue.Kinds |

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
