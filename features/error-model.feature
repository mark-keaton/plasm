# Executable acceptance specification (Reqnroll): how failures are reported.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit, which found exceptions where values belong.
@implemented
Feature: Failures are values with qualified names
  As an F# application developer
  I want every expected failure of a repository call to come back as a Result
  So that I can handle failures with pattern matching and never be surprised by an exception

  Exceptions remain for programming errors found before any database work (for
  example a changeset given to the wrong kind of call) and for cancellation.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Expected failures are returned, not thrown

    Scenario: Using a sandbox's repository after the sandbox ended
      Given a repository kept from a sandbox that has ended
      When I read Customers through it
      Then the read returns an error saying the repository is closed
      And no exception is thrown

    Scenario: Using a closed playground
      Given a playground that has been closed
      When I read Customers through it
      Then the read returns an error saying the repository is closed
      And no exception is thrown

    Scenario: A written row that cannot be read back
      Given the database changes an inserted OrderLine's quantity to zero
      When I insert an OrderLine
      Then the insert returns a materialisation error naming OrderLine.Quantity
      And no exception is thrown

  Rule: Error cases are always qualified

    Scenario: Domain unions keep their own case names
      When I compile a domain union with cases named Translation, Database, Materialization and UnmappedType alongside Plasm
      Then the domain cases are used without qualification
      And Plasm's error cases must be written with their type name

    Scenario Outline: <union> cases must be qualified
      When I compile a domain union reusing the case names of <union> alongside Plasm
      Then the domain cases are used without qualification
      And Plasm's <union> cases must be written with their type name

      Examples:
        | union          |
        | ChangeAction   |
        | SortDirection  |
        | FieldChange    |
        | AssocKind      |
        | LoadStrategy   |
        | TxFailure      |

  Rule: Transaction failures are typed

    Scenario: A step's rejection reason comes back with its type
      When a transaction step fails with a domain error value
      Then the failure gives back the domain error with its type, without casting
      And reading it as another type does not compile

    Scenario: A step's invalid changes come back as the changeset's errors
      When a transaction step's changeset is invalid
      Then the failure names the entity and lists the field errors
