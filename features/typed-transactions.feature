# Executable acceptance specification (Reqnroll): typed transactions whose
# steps need no names. Written before the implementation, following core.feature.
@implemented
Feature: Transactions are typed, and their steps need no names
  As an F# application developer
  I want a transaction's rejections to have my own type, and its steps to be found by where they are written
  So that nothing about a step is stated twice and no rejection needs a cast

  A transaction is a Tx<'T, 'E>: it gives a 'T, or fails. Its own failures are
  rejections of type 'E; Plasm's are invalid changes, conflicts and database
  failures. A failure names the file and line of the step that failed.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A failure says where its step is written

    Scenario: A failed step is reported at its file and line
      When a transaction inserts a Customer and then fails at a later step
      Then the error names the file and line of the failing step
      And the insert is reported as rolled back, at its own line

  Rule: Rejections keep their type

    Scenario: A rejection has the transaction's error type
      When a transaction rejects with OutOfStock "widget"
      Then the failure is the rejection OutOfStock "widget", read without a cast

    Scenario: A custom step's Error is a rejection of the same type
      When a custom step returns Error (OutOfStock "gadget")
      Then the failure is the rejection OutOfStock "gadget", read without a cast

    Scenario: A domain decision's Error becomes a rejection
      When a transaction takes a decision that returns Error "too late" through Tx.ofResult
      Then the failure is the rejection Refused "too late", read without a cast

    Scenario: A rejection cannot be read as another type
      When I compile code that reads a transaction's rejection as another type
      Then the code does not compile

  Rule: Reading one row and counting are steps

    Scenario: Tx.one gives the only matching row
      Given a Customer named "Ada"
      When a transaction reads the Customer named "Ada" with Tx.one
      Then it gives that Customer

    Scenario: Tx.one gives None when nothing matches
      When a transaction reads the Customer named "Nobody" with Tx.one
      Then it gives None

    Scenario: A missing row becomes a rejection with Tx.orReject
      When a transaction reads the Customer named "Nobody" with Tx.one and Tx.orReject (Missing "customer")
      Then the failure is the rejection Missing "customer", read without a cast

    Scenario: Tx.count counts without reading the rows
      Given Customers named "Ada", "Bob" and "Cy"
      When a transaction counts the Customers with Tx.count
      Then it gives 3
      And the command it sent counts the rows

  Rule: Steps need no names

    Scenario: A transaction is written without naming its steps
      When I compile a transaction that inserts a Customer and reads it back, naming no step
      Then the code compiles

  Rule: Steps write records

    A step writes a record: `Tx.insert customer`, `Tx.update (original, changed)`
    and `Tx.delete customer`. An update writes only the columns `changed`
    differs in from `original`, the record it was made from, and finds it by
    its key and the version it was read at. A change made from a copy a caller
    saw earlier, such as one that came back from a client, reads the record
    with `Tx.oneAsSeen (query, seen)`: the record as stored, if its version is
    still `seen`, so a stale caller is told so before anything is decided. The
    version is checked on the record's own row, before what it owns is read, so
    a stale copy costs one SELECT.
    A conflict names the record that was stale by its key, the value of its
    key field (`TxFailure.Conflict (entity, key)`), and the error's step is
    where the stale read or write is written: of two records of one type read
    from the same line, the failure still says which.

    Scenario: Inserting a record gives it back with its key
      When a transaction inserts the Customer "Ada" as a record
      Then it gives the Customer "Ada" with its key

    Scenario: An update writes only the columns that changed
      Given a Customer named "Ada"
      When a transaction updates that Customer's name to "Ada L." from the record read
      Then the Customer is stored with the name "Ada L."
      And the update sent sets only the name

    Scenario: A record read as the caller saw it is changed
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 2 and updates its total to 99
      Then the Order is stored with the total 99 at version 3

    Scenario: A copy the caller saw before another change is stale, before anything is decided
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 1 and would then refuse the change
      Then the failure is a conflict, and the Order is unchanged

    Scenario: A stale copy is refused in one SELECT
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 1, counting what it sends
      Then the failure is a conflict, and the Order is unchanged
      And the read sent one SELECT, of the Order's own row, and none of its Lines

    @postgres
    Scenario: A change landing between the read and the write is stale
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 2, and another writer changes it before the update
      Then the failure is a conflict, and the Order keeps the other writer's change

    Scenario: Of two Orders read as seen by one helper, the conflict names the stale one
      Given two draft Orders, the second at version 2
      When a transaction reads both through one helper as seen at version 1
      Then the failure is a conflict naming the second Order's key

    Scenario: Of Orders read as seen in Tx.traverse, the conflict names the stale one
      Given two draft Orders, the second at version 2
      When a transaction reads both with Tx.traverse as seen at version 1
      Then the failure is a conflict naming the second Order's key

    @postgres
    Scenario: A stale update's conflict names the Order's key
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 2, and another writer changes it before the update
      Then the failure is a conflict naming the Order's key

    Scenario: A stale read's failure is reported at the Tx.oneAsSeen that read it
      Given a draft Order whose version is 2
      When a transaction reads that Order as seen at version 1 at a known line
      Then the failure's step is that Tx.oneAsSeen, at its file and line

    Scenario: A record that is not there is missing, not stale
      When a transaction reads an Order that does not exist as seen at version 1, with Tx.orReject (Missing "order")
      Then the failure is the rejection Missing "order", read without a cast

    Scenario: A record without a version field cannot be read as seen
      Given a Customer named "Ada"
      When a transaction reads that Customer as seen at version 1
      Then the failure says Customer has no version field, and to read it with Tx.one

    Scenario Outline: A write gives back the record as stored, its time to the microsecond
      Given a draft Order, inserted
      When a transaction updates that Order's placed time to <written>
      Then the Order it gives back was placed at <stored>
      And the Order read again is the Order it gave back

      # PostgreSQL keeps microseconds; Npgsql drops what is finer, toward 2000-01-01.
      Examples:
        | written                      | stored                       |
        | 2026-01-01T00:00:00.0000019Z | 2026-01-01T00:00:00.0000010Z |
        | 1999-01-01T00:00:00.0000019Z | 1999-01-01T00:00:00.0000020Z |

    Scenario: Deleting the record read deletes it
      Given a Customer named "Ada"
      When a transaction deletes that Customer
      Then no Customer is stored
