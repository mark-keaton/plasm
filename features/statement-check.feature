# Executable acceptance specification (Reqnroll): the database describes every
# statement a module defines before the repository starts. Written before the
# implementation.
@implemented
Feature: The database describes every statement before any runs
  As an F# application developer whose queries include SQL of my own
  I want every statement my modules define parsed by the database at start-up
  So that a misspelled column in hand-written SQL fails start-up, not a request

  Repo.Start translates every query value a module defines, as before, and
  now asks the database to describe the SQL of each: its query values (their
  loads included), its bulk writes, and its hand-written statements
  (FormattableString values). The database parses each and says what it would
  return, and runs none; a statement it refuses is a problem named by its
  value, under RepoOptions.Queries as a query that cannot be translated is,
  with the database's reason. Each parameter is sent as an untyped NULL, so a
  prepared query is described through its template.

  Not reached this way: SQL written in place at a call (repo.Sql, Tx.sql and
  Tx.executeSql given a string where they are called), a query a function
  builds, the steps of a transaction, and a query's loads bound with let!,
  which are described with the query only in so far as its own statement
  goes. A provider that cannot describe a statement describes none, and says
  how many it did not, as a note that never fails start-up. So is a statement
  whose parameter the database cannot type from the statement alone (SQLSTATE
  42P18, as `select @p0` with an untyped NULL): that is the description's
  limit, not the statement's fault, and the statement is checked when it runs. The statements are
  described only over tables that match the model: when the schema check finds
  a difference, or does not run (RepoOptions.Schema = Ignore), a description
  would report that difference again, or describe tables the model does not
  know, so none is described and the schema check's report stands.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: What the database refuses is a problem named by its value

    # The reason is the database's own words, which name the misspelled column
    # on both (PostgreSQL: column "totl" does not exist; SQLite: no such
    # column: totl).
    Scenario Outline: <what> the database refuses is reported by its name
      When the database describes the statements of the module of refused ones
      Then "<name>" is reported as refused by the database, saying "<reason>"

      Examples:
        | what                                 | name               | reason                        |
        | A query whose fragment               | misspelledFragment | syntax error                  |
        | A hand-written statement             | lateOrders         | totl                          |
        | A bulk write                         | purgeQuoted        | syntax error                  |

    Scenario: What the database accepts is not reported, and none of it runs
      When the database describes the statements of the module of accepted ones
      Then no statement is reported
      And the customers are as they were

    Scenario: Repo.Start reports a refused statement with the other problems
      When I start a repository over the steps' modules, warning
      Then the problems include "lateOrders", refused by the database

    Scenario: Over tables that differ from the model, no statement is described
      Given the customers table has lost its email column
      When I start a repository over the steps' modules, warning
      Then the problems include the missing column customers.email, and none refused by the database

  Rule: A provider that cannot describe says so

    Scenario: The provider describes each statement, and what it says is reported
      Given a provider like the database's that refuses every statement it is asked to describe, saying "not by this provider"
      When it describes the statements of the module of accepted ones
      Then each of them is reported as refused, saying "not by this provider"

    Scenario: A provider that cannot describe notes the statements it did not
      When a provider that cannot describe describes the statements of the module of refused ones
      Then it notes that 3 statements were not described, and reports none as refused

    @postgres
    Scenario: A statement whose parameter the database cannot type alone is noted, not refused
      When the database describes the statements of the module whose parameter has no type of its own
      Then it notes that 1 statement was not described, and reports none as refused
