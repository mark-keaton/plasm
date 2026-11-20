# Executable acceptance specification (Reqnroll): what one write sends, and
# what one level of loads reads, travels to the database together. Written
# before the implementation.
@implemented
Feature: A write, and each level of loads, is one round trip
  As an F# application developer whose database is across a network
  I want a write of an aggregate, and the loads of one level, sent together
  So that a write or a read waits for the network once per level, however many
  statements it sends

  A write of a record sends its own statement and every statement of what it
  owns and links as one round trip. An insert whose key the database generates
  is two: the record, then what it owns, which needs the key. Each record is
  its own statement, so that a broken constraint names its record
  (`Lines[1].UnitPrice`), which one INSERT of several rows could not. Loads
  that need only the keys of the records read before them travel together:
  what a record owns and links, and the loads a query binds with `let!` and
  `and!`. A load of what a load read waits for those records, so each level
  is one round trip.

  The statements are the ones Plasm sends one at a time, and each is reported
  on its own, with the round trip it travelled in. Every statement's result is
  read: one that wrote nothing fails the write as it would alone, as a
  conflict or, for another owner's record, as that record's error
  (`Lines[1].Id`), and the transaction rolls back. A provider that cannot send statements together sends the same
  statements one at a time, with the same results. Steps of a transaction are
  not sent together: a later step may depend on what an earlier one read.

  # Order = { Id: OrderId (generated); ...; Version: int; Lines: OrderLine list }
  # Sheet = { Id: SheetId (assigned); Name: string; Rows: SheetRow list }
  # Card = { Id: CardId; Title: string; Comments: Comment list; Labels: Label list (linked) }
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And sheets with rows, and cards with comments and labels

  Rule: A write is one round trip, after the key it needs

    Scenario: An Order inserted with two Lines is two round trips, its Lines together
      When I insert an Order with two Lines
      Then the write took 2 round trips
      And both Lines' INSERTs travelled in the second

    Scenario: A record whose key the application assigns is inserted with what it owns in one round trip
      When I insert a Sheet with two Rows
      Then the write took 1 round trip
      And the Sheet reads back with its two Rows

    Scenario: An Order with one Line changed and one removed is written in one round trip
      Given an Order with three Lines
      When I change its first Line's quantity and remove its last Line
      Then the write took 1 round trip
      And the Order reads back with two Lines, the first of quantity 5

    Scenario: An owned Line's update compares its owner once
      Given an Order with three Lines
      When I change its first Line's quantity and remove its last Line
      Then the Line's update compares "order_id" once

    Scenario: A stale Line sent with others fails the write as a conflict, and nothing is written
      Given an Order with three Lines
      And its second Line is changed by another writer
      When I change its first and second Lines' quantities
      Then the write fails as a conflict on the Order
      And no Line has quantity 5

    Scenario: Another Order's Line sent with others is refused by its place in the list, and nothing is written
      Given an Order with three Lines
      And another Order with a Line
      When I change its first Line's quantity and list the other Order's Line second
      Then the write is refused because Lines[1].Id does not belong to the Order
      And no Line has quantity 5

  Rule: The loads of one level are one round trip

    Scenario: A Card's comments and labels are read together
      Given a Card with two comments and two labels
      When I read the Cards with what they own and link
      Then the read took 2 round trips
      And the Card reads with its two comments and two labels

    Scenario: Loads bound with let! and and! are read together
      Given an Order with three Lines and two Payments
      When I read the Orders binding their Payments with let! and their Customer with and!
      Then the read took 2 round trips

    Scenario: A load of what a load read waits for its records
      Given an Order with three Lines and two Payments
      When I read the Customers binding their Orders, each binding its Payments
      Then the read took 3 round trips

  Rule: Tx.traverse sends its items' writes together

    The items of Tx.traverse are independent: what one writes cannot depend on
    what another wrote. When each item is one write (Tx.insert, Tx.update or
    Tx.delete), their statements travel together, a round trip for each level
    a write needs. Every item is checked before anything is sent. A failure
    is the first item's, in order, whose write failed, and the items before it
    are what was rolled back, as if they had run one after another. An item of
    more than one step runs after the one before it.

    Scenario: Three Sheets inserted by Tx.traverse are one round trip
      When I insert three Sheets with two Rows each by Tx.traverse
      Then the write took 1 round trip
      And three Sheets read back with two Rows each

    Scenario: Orders whose keys the database generates take two round trips, however many
      When I insert three Orders with a Line each by Tx.traverse
      Then the write took 2 round trips
      And three Orders read back with a Line each

    Scenario: A failed item fails the transaction at that item, and nothing is written
      Given a Customer with the email "ada@example.com"
      When I insert Customers with the emails "bo@example.com", "ada@example.com" and "cy@example.com" by Tx.traverse
      Then the transaction fails with the Email field's error, at the insert of a Customer
      And the insert of the first Customer is what was rolled back
      And no Customer with the email "bo@example.com" or "cy@example.com" is written

    Scenario: An item its own checks refuse is refused before anything is sent
      Given an Order with three Lines
      And another Order with a Line
      When I update both Orders by Tx.traverse, the second given another version
      Then the transaction is refused, and nothing was sent

    Scenario: Items of more than one step run one after another
      When I insert three Sheets by Tx.traverse, each item an insert followed by a step of its own
      Then the write took 3 round trips

  Rule: A provider that cannot send statements together sends them one at a time

    Scenario: The same statements, one round trip each, with the same results
      Given a provider that sends one statement at a time
      And an Order with three Lines
      When I change its first Line's quantity and remove its last Line
      Then each statement took a round trip of its own
      And the Order reads back with two Lines, the first of quantity 5

    Scenario: Tx.traverse's writes, one round trip each
      Given a provider that sends one statement at a time
      When I insert three Orders with a Line each by Tx.traverse
      Then each statement took a round trip of its own
      And three Orders read back with a Line each
