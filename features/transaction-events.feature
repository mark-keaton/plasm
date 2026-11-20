# Executable acceptance specification (Reqnroll): the commands that begin and
# end a transaction, reported beside its statements. Written before the
# implementation.
@implemented
Feature: A transaction's own commands are reported
  As an F# application developer reading the SQL Plasm sent
  I want the BEGIN, COMMIT and ROLLBACK around it reported with it
  So that I can see which statements ran together, at which isolation level,
  and whether they were kept

  RepoOptions.OnCommand reports each command that begins or ends a
  transaction, in the text PostgreSQL logs: BEGIN with its isolation level,
  COMMIT, ROLLBACK, and the savepoints a sandbox works in. Each event's Kind
  says what it is, Statement, Begin, Commit or Rollback, so a subscriber that
  counts statements keeps the events whose Kind is Statement and reads no SQL
  to do it. A savepoint is the Begin of a nested transaction; releasing it is
  its Commit, and rolling back to it its Rollback.

  Npgsql sends BEGIN, and SAVEPOINT, with whatever the connection sends next,
  so the event has the round trip of the command it travelled with. COMMIT,
  ROLLBACK and the release of a savepoint are round trips of their own. A
  transaction that sends no statement still sends BEGIN and its end, together.

  # Customer = { Id: CustomerId (generated); Name: string; [<Unique>] Email: string }
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A transaction reports how it began and how it ended

    Scenario: A committed transaction reports its BEGIN and its COMMIT
      When a transaction inserts a Customer
      Then OnCommand reported, in order:
        | Kind      | Sql                                              |
        | Begin     | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Statement | INSERT INTO "customers"                          |
        | Commit    | COMMIT                                           |
      And 1 of them is a statement, by its Kind

    Scenario Outline: BEGIN names the isolation level the transaction runs at
      When a transaction at <isolation> inserts a Customer
      Then the first event is a Begin, "<begin>"

      Examples:
        | isolation      | begin                                            |
        | ReadCommitted  | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | RepeatableRead | BEGIN ISOLATION LEVEL REPEATABLE READ            |
        | Serializable   | BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE   |

    Scenario: BEGIN travels with the first statement, and COMMIT on its own
      When a transaction inserts a Customer
      Then the Begin has the round trip of the INSERT
      And the Commit has a round trip of its own

    Scenario: A refused transaction reports its ROLLBACK
      When a transaction reads the Customers and then refuses
      Then OnCommand reported, in order:
        | Kind      | Sql                                              |
        | Begin     | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Statement | SELECT                                           |
        | Rollback  | ROLLBACK                                         |

    Scenario: A failed transaction reports its failed statement and its ROLLBACK
      Given a Customer with the email "ada@example.com"
      When a transaction inserts a Customer with the email "ada@example.com"
      Then OnCommand reported, in order:
        | Kind      | Sql                                              |
        | Begin     | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Statement | INSERT INTO "customers"                          |
        | Rollback  | ROLLBACK                                         |
      And the INSERT did not succeed

    Scenario: A transaction that sends no statement sends BEGIN and ROLLBACK together
      When a transaction refuses before it reads anything
      Then OnCommand reported, in order:
        | Kind     | Sql                                              |
        | Begin    | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Rollback | ROLLBACK                                         |
      And they share one round trip

    Scenario: A read in one snapshot reports the transaction it reads in
      Given an Order with one Line
      When I read the Orders in one snapshot
      Then OnCommand reported, in order:
        | Kind      | Sql                                   |
        | Begin     | BEGIN ISOLATION LEVEL REPEATABLE READ |
        | Statement | SELECT                                |
        | Statement | SELECT                                |
        | Commit    | COMMIT                                |

  Rule: A sandbox reports its ROLLBACK, and the savepoints it works in

    Scenario: A sandbox that read the Customers is rolled back
      When a sandbox reads the Customers
      Then OnCommand reported, in order:
        | Kind      | Sql                                              |
        | Begin     | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Begin     | SAVEPOINT plasm_sandbox_1                        |
        | Statement | SELECT                                           |
        | Commit    | RELEASE SAVEPOINT plasm_sandbox_1                |
        | Rollback  | ROLLBACK                                         |

    Scenario: A transaction in a sandbox is a savepoint inside the operation's
      When a sandbox's transaction inserts a Customer
      Then OnCommand reported, in order:
        | Kind      | Sql                                              |
        | Begin     | BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED |
        | Begin     | SAVEPOINT plasm_sandbox_1                        |
        | Begin     | SAVEPOINT plasm_sandbox_2                        |
        | Statement | INSERT INTO "customers"                          |
        | Commit    | RELEASE SAVEPOINT plasm_sandbox_2                |
        | Commit    | RELEASE SAVEPOINT plasm_sandbox_1                |
        | Rollback  | ROLLBACK                                         |
