# Executable acceptance specification (Reqnroll): every query an application
# defines is checked before it is used. Written before the implementation.
@implemented
Feature: Every query is checked before it is used
  As an F# application developer
  I want every query my application defines translated in one check
  So that a query Plasm cannot translate is found by a test or at start-up, never on first use

  `repo.CheckQueries assembly` finds the queries defined as values in the
  assembly's modules, and the prepared queries, and translates each without
  running it; a prepared query is translated with its arguments as parameters.
  A query built inside a function is not a value, so it is not found.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: The check translates every query without running it

    Scenario: Queries that translate pass
      When I check the queries of a module whose queries all translate
      Then no problem is reported
      And no database command is executed

    Scenario: A query that cannot be translated is reported by its name
      When I check the queries of a module with a query Plasm cannot translate
      Then one problem is reported, for the query "Bad.normalized", saying why it cannot be translated

    Scenario: A prepared query that cannot be translated is reported without being called
      When I check the queries of a module with a prepared query Plasm cannot translate
      Then one problem is reported, for the query "BadPrepared.normalizedNamed", saying why it cannot be translated

  Rule: A prepared query is still called like a function

    Scenario: A checked prepared query reads with its argument
      Given Customers named "Ada" and "Bob"
      When I read with the checked prepared query for the name "Bob"
      Then only the Customer named "Bob" is returned
