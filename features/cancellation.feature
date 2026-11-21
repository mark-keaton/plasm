# Executable acceptance specification (Reqnroll): cancelling a statement while
# the database runs it. Written before the SQLite provider's implementation.
@implemented
Feature: A cancelled token stops a statement the database is running
  As an application developer whose caller has given up
  I want the statement my read or write is running to stop when my token is cancelled
  So that a cancelled request does not hold a connection, a transaction or a thread for as long as the query would take

  Each statement here would run for many seconds: a recursive query counting
  to a hundred million, which both databases can write. Its token is cancelled
  at 300 ms. Cancellation is not a failure: it raises
  OperationCanceledException, as every cancelled call does. On SQLite, whose
  driver does not hear a token while a statement runs, the provider interrupts
  the statement on its connection, and only while the statement is the
  caller's.

  Background:
    Given a configured relational database
    And the Customers "Ada" and "Bob"

  Scenario: A running query is cancelled by its token
    When I read the Customers through a condition that runs for seconds, cancelled at 300 ms
    Then the read raises OperationCanceledException within 5 seconds

  Scenario: A stream cancelled while its next row is being found raises at once
    When I stream the Customers, many quick to find and then Zed slow to find, read the first and keep asking for the next, cancelled at 300 ms
    Then asking for it raises OperationCanceledException within 5 seconds

  Scenario: A statement cancelled in a transaction leaves the transaction rolled back
    When a transaction inserts a Customer "Cy" and then runs a statement that runs for seconds, cancelled at 300 ms
    Then the transaction raises OperationCanceledException within 5 seconds
    And there is no Customer "Cy"

  Scenario: After a cancelled statement, the next statement on its connection runs as it would
    When a statement on a connection the provider opened is cancelled while it runs
    Then the next statement on that connection gives 42

  Scenario: A token cancelled after its statement finished touches nothing the connection runs next
    When a statement on a connection the provider opened finishes, and its token is cancelled while the next one runs
    Then the next one runs to its end, counting to ten million

  # A rollback-journal file (one SQLite.JournalMode.Keep leaves as another tool
  # made it): there a reader waits for a writer's commit, and a commit for the
  # readers, each for up to the busy timeout. PostgreSQL has no journal mode.

  @sqlite
  Scenario: On a rollback-journal file, a read waiting for a writer is cancelled by its token
    Given a rollback-journal database that another connection holds for writing
    When I read its Customers, cancelled at 300 ms
    Then the read raises OperationCanceledException within 2 seconds

  @sqlite
  Scenario: On a rollback-journal file, a commit waiting for a reader is cancelled by its token
    Given a rollback-journal database that another connection is reading
    When a transaction inserts a Customer "Cy" into it, cancelled at 300 ms
    Then the transaction raises OperationCanceledException within 2 seconds
    And it has no Customer "Cy"
