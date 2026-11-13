# Executable acceptance specification (Reqnroll): the pipeline query functions.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit, which found the pipeline forms incomplete.
@implemented
Feature: Every query operation has a pipeline form
  As an F# application developer who prefers pipelines to computation expressions
  I want a Query function for every operation of the plasm { } expression
  So that I can build the same queries either way and pick by readability

  The two forms are equivalent: the same operations in the same order produce
  the same query, and so the same SQL.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Pipeline queries equal their computation-expression forms

    Scenario Outline: <operation> in a pipeline produces the same SQL
      When I write a query using <operation> both as plasm { } and as a Query pipeline
      Then both produce the same SQL and the same load plan
      And both return the same rows

      Examples:
        | operation                      |
        | thenBy                         |
        | thenByDescending               |
        | preload with a child query     |
        | replacePreload                 |
        | limitPerParent                 |
        | take and skip from arguments   |
        | select                         |
        | join                           |
        | leftJoin                       |
        | groupBy and having             |

  Rule: Pipelines chain like expressions

    Scenario: Chaining joins in a pipeline
      When I join Orders to OrderLines and OrderLines to Products in a pipeline and filter on the Product
      Then the SQL equals the same plasm { } query with two joins
      And the rows are the same Order, OrderLine and Product triples
