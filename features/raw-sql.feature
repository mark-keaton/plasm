# Executable acceptance specification (Reqnroll): typed SQL escape hatches.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Typed SQL escape hatches
  As an F# application developer
  I want to write SQL myself when the query language cannot express something
  So that I am never blocked, without giving up parameters or typed results

  SQL written by hand is explicit: interpolated values always become command
  parameters, and results are mapped through the same model as every query.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Hand-written queries return typed values

    Scenario: Reading mapped records from SQL
      Given Orders for several Customers
      When I read Customers with hand-written SQL
      Then the results are Customer records built through the model

    Scenario: Reading a record that owns others from SQL
      Given Orders for several Customers, each with an OrderLine
      When I read Orders with hand-written SQL
      Then each Order holds its OrderLines, read in one more command

    Scenario: Reading tuples and scalars from SQL
      Given Orders for several Customers
      When I read Customer names with their Order counts using hand-written SQL
      Then the results are typed tuples in column order

    Scenario: Reading unmapped records from SQL
      Given Orders for several Customers
      When I read a summary record that is not part of the model
      Then each field is read from the column of the same name

    Scenario: Interpolated values are always parameters
      Given Orders for several Customers
      When I interpolate a value containing SQL into hand-written SQL
      Then the value is sent as a command parameter
      And the SQL text does not contain the value

    Scenario: Mismatched results fail with the column identified
      When hand-written SQL omits a column the record requires
      Then execution fails with the missing column identified

  Rule: A value is read only as a type that holds it exactly

    A number is read as another number type when the value survives the trip:
    a bigint 4 or a numeric 3 is an int. A value that would be rounded, or text
    that would be parsed, is a materialization error naming both types.

    @postgres
    Scenario Outline: <sql> is read as an int: <outcome>
      When I read an int with the hand-written SQL "<sql>"
      Then the read <outcome>

      Examples:
        | sql                      | outcome                                    |
        | SELECT 4::bigint         | gives 4                                    |
        | SELECT 3::numeric        | gives 3                                    |
        | SELECT 2.5::numeric      | fails, saying a Decimal 2.5 is not an int  |
        | SELECT '42'::text        | fails, saying a String 42 is not an int    |

    # SQLite's values are integers, reals and text.
    @sqlite
    Scenario Outline: <sql> is read as an int: <outcome> on SQLite
      When I read an int with the hand-written SQL "<sql>"
      Then the read <outcome>

      Examples:
        | sql                      | outcome                                    |
        | SELECT 4                 | gives 4                                    |
        | SELECT 3.0               | gives 3                                    |
        | SELECT 2.5               | fails, saying a Double 2.5 is not an int   |
        | SELECT '42'              | fails, saying a String 42 is not an int    |

  Rule: SQL fragments extend typed queries

    Scenario: A fragment inside a typed query
      Given Orders for several Customers
      When I filter Customers with a SQL fragment over a column
      Then the fragment is part of the executed SQL with its arguments as columns and parameters
      And the results are typed records

    Scenario: Fragment SQL must be literal
      When I attempt to build a fragment from a runtime string
      Then the fragment is rejected at compile time

  Rule: Hand-written commands and transactions

    Scenario: Executing hand-written commands
      Given Orders for several Customers
      When I execute a hand-written update
      Then the number of affected rows is returned

    Scenario: Hand-written SQL inside a transaction
      Given Orders for several Customers
      When a transaction runs hand-written SQL and then fails
      Then the hand-written change is rolled back
