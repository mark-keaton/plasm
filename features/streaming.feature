# Executable acceptance specification (Reqnroll): streaming reads. Written
# before the implementation.
@implemented
Feature: A query's rows can be read as a stream
  As an F# or C# application developer reading more rows than I want in memory
  I want a read that gives me a query's rows as I ask for them
  So that an export or a scan holds one row at a time, not the whole result

  `repo.Stream query` (or a projection) is an IAsyncEnumerable of
  Result<'T, DbError>: the query's rows, read from the database as the caller
  asks for the next, never gathered into a list. Nothing is sent until the
  caller asks for the first row. From then it holds one connection, and gives
  it back when the stream ends, when the caller disposes of it (stopping
  early), and when the caller's token is cancelled. A failure is a value: the
  last element is an Error (a database failure, or a value that cannot be
  read back) and the stream ends there; a query that cannot be streamed or
  translated is a single Error, and no connection is opened. Cancellation is
  not a failure and raises OperationCanceledException, as every read does.

  A stream is one command. A record that owns or links a list is read by more
  than one, so a query of such records, or a projection that selects one, is
  refused with the list named. A stream reads outside a transaction; in a
  sandbox it reads the sandbox's rows. OnCommand and the activity report it
  once, when it ends, with the rows read.

  # The customers: Person = { Id (generated); Name: string; Place: int }, written
  # Cal 3, Ann 1, Eve 5, Bea 2, Dee 4
  # Order = { ...; Lines: OrderLine list (owned) }
  Background:
    Given a configured relational database
    And five customers named Ann, Bea, Cal, Dee and Eve

  Rule: The rows come as the caller asks for them

    Scenario: A stream gives every row of its query, in order
      When I stream the customers ordered by name
      Then the stream gives Ann, Bea, Cal, Dee and Eve, each as a value
      And the stream was reported once, with 5 rows

    Scenario: Nothing is sent until the caller asks for the first row
      When I make a stream of the customers and do not read it
      Then no command was sent
      And no connection is open

    Scenario: A stream holds one connection from its first row until it ends
      When I stream the customers ordered by name and read the first row
      Then one connection is open
      And the stream has not been reported yet
      When I read the rest of the stream
      Then no connection is open

    Scenario: A caller that stops early gives the connection back
      When I stream the customers ordered by name, read two rows and stop
      Then no connection is open
      And the stream was reported once, with 2 rows

    Scenario: A cancelled stream gives the connection back
      When I stream the customers ordered by name, read one row and cancel
      Then asking for the next row raises OperationCanceledException
      And no connection is open

  Rule: A stream is one command

    Scenario: A projection is streamed
      When I stream the customers' names ordered by name
      Then the stream gives Ann, Bea, Cal, Dee and Eve, each as a value

    Scenario: A query of records that own a list is refused, naming the list
      When I stream the orders
      Then the stream gives one error, saying Order.Lines is read by another command
      And no connection is open
      And no command was sent

    Scenario: A query that cannot be translated is one error
      When I stream the customers filtered by a function SQL cannot express
      Then the stream gives one error, an unsupported expression
      And no command was sent

  Rule: A failure ends the stream as a value

    Scenario: A database failure in the middle of a stream is its last element
      When I stream 100 divided by each customer's place less five, in the order they were written, the third being zero
      Then the stream gives two values and then a database error
      And no connection is open

    Scenario: A stored value that cannot be read back is its last element
      Given three order lines whose quantities are 1, 2 and 0
      When I stream the order lines' quantities in the order they were written
      Then the stream gives two values and then a materialization error naming OrderLine.Quantity
      And no connection is open

  Rule: Where a stream reads

    Scenario: A stream in a sandbox sees the sandbox's rows
      When a sandbox adds a customer named Fay and streams the customers ordered by name
      Then the sandbox's stream gives Ann, Bea, Cal, Dee, Eve and Fay
      And outside the sandbox there are five customers

    Scenario: The activity reports a stream once, with the rows read
      When I listen to Plasm's activities and stream the customers ordered by name
      Then one activity was reported for the stream, with 5 rows
