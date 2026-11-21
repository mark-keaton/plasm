# Lesson 24 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About set operations
  Two queries of the same rows combine into one: a union, an intersection or
  a difference, read in one command and ordered and paged as a whole. A join
  takes a query where it takes a table.

  Prerequisites: lesson 09, lesson 14.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                     | the learner discovers                                                                  | teaches                                                                                          | hint                                                                                                                                          |
      | A union gives each row once              | Query.union combines two queries of customers, each customer once                       | set-operations: <operation> of the Customers named Ada or Bob and those with an Order             | Under Given, find the customers named Bob or Cy and those with a row in the orders table, and list each of them once, by name.                 |
      | A union of all keeps every copy          | Query.unionAll keeps a row once for each query that found it                            | set-operations: <operation> of the Customers named Ada or Bob and those with an Order             | Count each customer once for each of the two queries that finds it, under Given, and list the names by name.                                  |
      | An intersection and a difference         | Query.intersect keeps the rows both find; Query.except those only the first finds       | set-operations: <operation> of the Customers named Ada or Bob and those with an Order             | Of the customers named Bob or Cy under Given, see which also have a row in the orders table, and which do not.                                |
      | The combined rows are ordered and paged  | the query made from a union orders and pages the whole, after it is formed               | set-operations: A union is filtered, ordered and paged after it is formed                         | Order the union of the first koan by name and take its first row; the SQL's ORDER BY and LIMIT come after the parenthesis that closes it.     |
      | An operand cannot order itself           | ordering or paging an operand is refused, saying to order the combined rows              | set-operations: <what> is refused                                                                 | The comment says what such a query is refused with, and the text the refusal says.                                                           |
      | Two projections combine into their rows  | a union of two projections of names gives each name once                                 | set-operations: A union of two projections of names gives each name once                          | Under Given, the names of the customers with a row in the orders table, and Ada's, each once, sorted.                                         |
      | A join takes a query                     | a join's source can be a query, joined as its rows                                       | set-operations: Orders joined with a query of Customers                                           | Find the rows of the orders table under Given whose customer is Ada, and give their ids, in order, as OrderIds.                               |
