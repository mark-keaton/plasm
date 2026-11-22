# Executable acceptance specification (Reqnroll): a division by zero fails,
# on every database.
@implemented
Feature: A division by zero fails, on every database
  As an F# application developer
  I want a division or remainder by zero to fail the query that makes it
  So that no database answers it quietly with a NULL another would refuse

  PostgreSQL fails a division or remainder by zero, of an integer, a double or
  a numeric (SQLSTATE 22012). SQLite answers NULL for an integer's and a
  real's, which an optional field would read as None: so its dialect writes
  every division and remainder the core writes so that a zero divisor fails
  it (ISqlDialect.Division), and a decimal's division and remainder, made by
  Plasm.Sqlite's functions, refuse a zero divisor too. Each is a database
  failure, wherever the query divides: in what it selects, in its condition,
  in an aggregate's argument and in a bulk update's set clause.

  Background:
    Given a configured relational database
    And a row whose integer, double and decimal are divided by divisors of zero

  Scenario Outline: <operation> by zero in <place> is a database failure
    When I <operation> by zero in <place>
    Then it fails as a database failure naming a division by zero

    Examples:
      | operation                        | place                    |
      | divide the integer               | what the query selects   |
      | divide the integer               | the query's condition    |
      | divide the integer               | an aggregate's argument  |
      | divide the integer               | a bulk update's set      |
      | take the integer's remainder     | what the query selects   |
      | take the integer's remainder     | the query's condition    |
      | take the integer's remainder     | an aggregate's argument  |
      | take the integer's remainder     | a bulk update's set      |
      | divide the double                | what the query selects   |
      | divide the double                | the query's condition    |
      | divide the double                | an aggregate's argument  |
      | divide the double                | a bulk update's set      |
      | divide the decimal               | what the query selects   |
      | divide the decimal               | the query's condition    |
      | divide the decimal               | an aggregate's argument  |
      | divide the decimal               | a bulk update's set      |
      | take the decimal's remainder     | what the query selects   |
      | take the decimal's remainder     | the query's condition    |
      | take the decimal's remainder     | an aggregate's argument  |
      | take the decimal's remainder     | a bulk update's set      |

  Scenario: A division by a parameter that is zero is a database failure
    When I divide the integer by a captured zero in what the query selects
    Then it fails as a database failure naming a division by zero

  Scenario: A division by what is not zero divides
    When I divide the integer, the double and the decimal by their divisors plus one
    Then the quotients are 7, 7.5 and 7.50

  Scenario: A division by a literal other than zero divides
    When I divide the integer by the literal 2 in what the query selects
    Then the quotient is 3

