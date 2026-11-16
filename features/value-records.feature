# Executable acceptance specification (Reqnroll): records stored in their
# owner's row. Written before the implementation.
@implemented
Feature: A record without a table is stored in its owner's row
  As an F# application developer
  I want a field to hold a small record, such as an address
  So that my domain groups what belongs together without a table of its own

  A field whose type is a record with no [<Table>] and no Id is a value: it is
  stored in the owner's row, each of its fields in a column named
  <field>_<subfield> (snake case), nesting chaining the prefix. An optional value
  is None exactly when all its columns are NULL, so it needs at least one field
  that is not optional; a row where it is partly NULL is a read error naming the
  field, never repaired. Queries read its fields as columns (`c.Address.City`),
  and comparing a whole value compares every column. A change writes the
  field's columns. Ddl() prints its columns, and for an optional value a CHECK
  that its columns are all NULL or all present. A record registered as a scalar
  mapping stays a scalar.

  # Customer = { Id: CustomerId; Name: string; Address: Address; Billing: Address option }
  # Address = { Street: string; City: string; Note: string option; Geo: Geo }
  # Geo = { Lat: decimal; Lon: decimal }
  Background:
    Given a configured relational database
    And customers with an address and an optional billing address, in one table

  Rule: A value is stored in its owner's row

    Scenario: A Customer is inserted as one row
      When I insert Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      Then one command is sent, inserting into value_customers
      And the row's address_street is "1 Main St", address_city is "Leeds" and address_geo_lat is 53.8
      And the row's billing columns are all NULL

    Scenario: A Customer is read back whole
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I read the Customers by name
      Then Ada and Bob are read as they were inserted
      And one command is sent

    Scenario: A partly stored value is a read error naming the field
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Ada's billing_street is set to "PO Box 7" behind Plasm's back, with no billing_city
      When I read the Customers by name
      Then the read fails, naming Billing

  Rule: Queries read a value's fields as columns

    Scenario: Filtering by a field of a value
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I read the Customers whose address is in "York"
      Then the Customers read are Bob

    Scenario: Filtering by a field of a nested value
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I read the Customers whose address lies north of 53.9
      Then the Customers read are Bob

    Scenario: Comparing a whole value compares every column
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I read the Customers whose address is Ada's
      Then the Customers read are Ada

    Scenario: An optional value that is absent
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I read the Customers without a billing address
      Then the Customers read are Ada

    Scenario: Ordering by and selecting a field of a value
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When I select the cities of the Customers' addresses, ordered by city
      Then the cities are "Leeds" and "York"

  Rule: The parts of an optional value are read through the option

    Background:
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      And Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      And Cy, living at "3 Low Rd", "Bath" (51.4, -2.4), billed at "4 Low Rd", "Bath" (51.4, -2.4)

    Scenario: Ordering by a part of an optional value puts None first, as F# does
      When I read the Customers ordered by their billing city
      Then the Customers are read in the order Ada, Cy and Bob

    Scenario: Selecting a part of an optional value
      When I select the billing cities of the Customers, ordered by name
      Then the billing cities are none, "York" and "Bath"

    Scenario: Option.exists over an optional value
      When I read the Customers billed in "York"
      Then the Customers are read in the order Bob

    Scenario: Option.forall over an optional value is true when it is absent
      When I read the Customers billed nowhere but "York"
      Then the Customers are read in the order Ada and Bob

    Scenario: .Value reads a part once IsSome has been tested
      When I read the Customers with a billing address in "Bath", through Value
      Then the Customers are read in the order Cy

    Scenario: An expression that cannot be translated is named
      When I read the Customers whose name, passed through id, is "Ada"
      Then the read is refused, naming c.Name in the reason

  Rule: A change writes the value's columns

    Scenario: Moving a Customer writes the address columns only
      Given Ada, living at "1 Main St", "Leeds" (53.8, -1.5), with no billing address
      When Ada moves to "9 New Rd", "Leeds" (53.8, -1.5)
      Then the update sets the address columns and no others
      And Ada is read living at "9 New Rd"

    Scenario: Removing an optional value clears its columns
      Given Bob, living at "2 High St", "York" (54.0, -1.1), billed at "PO Box 7", "York" (54.0, -1.1)
      When Bob's billing address is removed
      Then the row's billing columns are all NULL
      And Bob is read with no billing address

  Rule: The database's tables are described and checked

    Scenario: Ddl prints a value's columns and the CHECK for an optional one
      When I print the DDL of the customers model
      Then it has the columns address_street text not null, address_note text, address_geo_lat numeric not null and billing_street text
      And it has a check that the billing columns are all NULL or all present

    Scenario: The schema check finds a value's missing column
      Given the address_city column is dropped
      When I check the customers model against the database
      Then a missing column address_city is reported for Customer.Address

  Rule: What is unclear is refused when the model is built

    Scenario Outline: <records> is refused
      When I build a model of <records>
      Then the model is refused for <model>, saying "<message>"

      Examples:
        | records                                             | model        | message                                                                  |
        | a field of a stored record that holds no owner's key | Shipper      | as an owned record it needs a field of type ShipperId                    |
        | a value with an Id but no table                      | TablessOwner | looks like a stored record without its table                             |
        | an optional value whose fields are all optional      | VagueOwner   | needs a field that is not optional                                       |
        | a value holding a stored record                      | HolderOwner  | a value cannot hold a stored record                                      |
        | a value with a unique field                          | UniqueOwner  | Unique is not supported inside a value                                   |
