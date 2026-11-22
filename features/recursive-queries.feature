# Executable acceptance specification (Reqnroll): recursive queries. Written
# before the implementation.
@implemented
Feature: A query can follow a record that refers to its own kind
  As an F# application developer whose records form a tree
  I want everything under a row, or above it, in one statement
  So that a tree of any depth is read without a query per level

  `Query.recursive (start, step)` is a query of the rows `start` finds and,
  again and again, the rows `step` finds from the rows found so far: `step`
  is given them as a query, `found`, which it joins as any query is joined,
  and selects the record it reaches. It is sent as one statement, WITH
  RECURSIVE ... UNION ALL, read as a source like a set operation, so the
  query made from it filters, orders and pages the whole, never a step.
  `Query.depth c` is how many steps found c: 0 for the rows `start` finds.
  A cycle in the data ends: a row already on the path that reached it is not
  followed again (CYCLE on the key), so the statement always ends. Each path
  to a row is a row: in a tree there is one.

  The start neither orders nor pages, as a set operation's operands do not;
  the step joins `found` once, selects the record it reaches and neither
  orders, pages, groups nor takes distinct rows; anything else is refused when
  the query is translated, saying what to do instead. A recursive query is
  not locked.

  # Category = { Id: CategoryId (generated); Name: string; Parent: CategoryId option }
  Background:
    Given a configured relational database
    And the categories:
      | name      | parent |
      | Tools     |        |
      | Saws      | Tools  |
      | Hand saws | Saws   |
      | Drills    | Tools  |
      | Garden    |        |
      | Hoses     | Garden |

  Rule: A tree is read in one statement

    Scenario: Everything under a row, with its depth
      When I read the categories under Tools, with their depth, by depth and name
      Then the rows are:
        | name      | depth |
        | Tools     | 0     |
        | Drills    | 1     |
        | Saws      | 1     |
        | Hand saws | 2     |
      And one statement was sent, a recursive one

    Scenario: Everything above a row, with its depth
      When I read the categories above Hand saws, with their depth, by depth
      Then the rows are:
        | name      | depth |
        | Hand saws | 0     |
        | Saws      | 1     |
        | Tools     | 2     |

    Scenario: A recursive query of records reads the records
      When I read the Category records under Garden, by name
      Then the records are Garden and Hoses, each with its parent

  Rule: The query made from it filters, orders and pages the whole

    Scenario: A filter on depth leaves out the start
      When I read the names of the categories under Tools deeper than 0, by name
      Then the names are Drills, Hand saws and Saws

    Scenario: A page of the whole tree
      When I read the second and third names under Tools, by name
      Then the names are Hand saws and Saws

  Rule: A cycle in the data ends

    Scenario: A tree whose root has been given a parent under it is read once round
      Given Tools has been given Hand saws as its parent
      When I read the categories under Tools, with their depth, by depth and name, on a connection whose statements time out after 5 seconds
      Then the rows are:
        | name      | depth |
        | Tools     | 0     |
        | Drills    | 1     |
        | Saws      | 1     |
        | Hand saws | 2     |
      And the statement sent stops a cycle by the category's key

  Rule: What a recursive query cannot be

    Scenario: A step that does not join the rows found is refused
      When I read a recursive query whose step reads the categories without joining the rows found
      Then it is refused, saying "the step of a recursive query joins found"

    Scenario: A step that orders its rows is refused
      When I read a recursive query whose step orders its rows
      Then it is refused, saying "order and page the rows a recursive query finds, not its step"

    Scenario: A start that pages its rows is refused
      When I read a recursive query whose start takes one row
      Then it is refused, saying "order and page the rows a recursive query finds, not its start"

    @postgres
    Scenario: A recursive query is not locked
      When I lock the categories under Tools in a transaction
      Then it is refused, saying "a recursive query's rows cannot be locked"

    @sqlite
    Scenario: A recursive query is not locked on SQLite
      When I lock the categories under Tools in a transaction
      Then it is refused, saying "Provider SQLite does not support row locks"

    Scenario: The depth of a row that no recursive query found is refused
      When I read the depth of the categories of a query that is not recursive
      Then it is refused, saying "Query.depth reads the rows of a recursive query"

    Scenario: The start-up check translates a recursive query
      When a repository starts with the checks on, over an assembly whose recursive query is refused
      Then it does not start, naming that query
