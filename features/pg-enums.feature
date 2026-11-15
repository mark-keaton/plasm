# Executable acceptance specification (Reqnroll): PostgreSQL enum types.
# Written before the implementation, following the conventions of core.feature.
# The research behind it is in docs/comparisons/enums.md.
@implemented
Feature: Fieldless unions stored as PostgreSQL enum types
  As an F# application developer
  I want to map a fieldless union to a PostgreSQL enum type
  So that I keep the database's enum without configuring the driver for it

  Plasm sends a value as text cast to the named type, and reads the column back
  as text, so the driver needs no enum mapping: no MapEnum, global type mapper
  or type reloading.

  # ShipmentStatus is declared Packed | Shipped | Delivered. Alphabetically the
  # labels would sort delivered first.
  Background:
    Given a configured relational database
    And a shipment_status enum type with the labels packed, shipped and delivered, in that order
    And Shipments whose Status is mapped to the shipment_status type

  Rule: Values are written and read without driver configuration

    Scenario: Inserting and reading a Shipment keeps its status
      When I insert a Shipment with status Shipped
      Then reading it back gives status Shipped

    Scenario: The enum type is created after the data source first connected
      Given the data source connected before the shipment_status type was created
      When I insert a Shipment with status Packed
      Then reading it back gives status Packed

    Scenario: Updating a status
      Given a Shipment with status Packed
      When I update its status to Delivered
      Then the database holds the label delivered

    Scenario: An optional status is written and read, including None
      When I insert Shipments with and without a previous status
      Then each reads back with the previous status it was given

    Scenario: Bulk writes accept statuses
      When I bulk insert Shipments with every status
      And I upsert one of them with status Delivered
      And I bulk update the Packed Shipments to Shipped
      Then every Shipment holds the status F# expects

  Rule: Queries keep their F# meaning

    Scenario: Filtering on a status
      Given Shipments with every status
      When I keep Shipments whose status is Shipped
      Then only the Shipped Shipments are returned

    Scenario Outline: Filtering on a captured <container> of statuses
      Given Shipments with every status
      When I keep Shipments whose status is in a captured <container> of Packed and Delivered
      Then the result equals evaluating the predicate over the same Shipments in F#

      Examples:
        | container |
        | list      |
        | array     |
        | set       |

    Scenario: Ordering and comparisons follow the union's case order
      Given Shipments with every status
      When I sort Shipments by status, and keep those whose status is after Packed
      Then both equal doing the same over the Shipments in F#

    Scenario: The earliest and latest status follow the union's case order
      Given Shipments with every status
      When I select the minimum and maximum status of the Shipments
      Then they are Packed and Delivered

    Scenario: A prepared query binds a status argument
      Given Shipments with every status
      When I run a prepared query for Shipments with a status argument of Delivered
      Then only the Delivered Shipments are returned

    Scenario: A status in raw SQL is sent as the enum type
      Given Shipments with every status
      When I read Shipments with raw SQL that filters on the status Shipped
      Then only the Shipped Shipments are returned

    Scenario: A filter on the status can use an index
      When I explain a query that filters on a status
      Then the parameter is cast to the enum type and the column is not

  Rule: The schema check finds mismatches before a request fails

    Scenario: A matching enum type passes
      When I check the Shipments model against the database
      Then no problems are reported for the Shipments

    Scenario: A text column mapped to an enum type is reported
      Given the status column is text
      When I check the Shipments model against the database
      Then an IncompatibleType issue for Shipment.Status says "column is text; ShipmentStatus needs the enum type shipment_status"

    Scenario: A missing enum type is reported
      Given the status column has another enum type, and shipment_status does not exist
      When I check the Shipments model against the database
      Then an IncompatibleType issue for Shipment.Status says "enum type shipment_status does not exist"

    Scenario: A label the model does not list is reported
      Given the shipment_status type also has the label lost
      When I check the Shipments model against the database
      Then an EnumLabels issue for Shipment.Status says "label lost is not a ShipmentStatus case; reading it would fail"

    Scenario: A case the type lacks is reported
      Given the shipment_status type has only the labels packed and shipped
      When I check the Shipments model against the database
      Then an EnumLabels issue for Shipment.Status says "no label delivered for Delivered; writing it would fail"

    Scenario: Labels in a different order from the cases are reported
      Given the shipment_status type declares delivered before shipped
      When I check the Shipments model against the database
      Then an EnumLabels issue for Shipment.Status says "labels are ordered packed, delivered, shipped but the cases Packed, Shipped, Delivered; the database would sort differently from F#"

  Rule: Mistakes are reported where they are made

    Scenario: A stored label the model does not list fails to load, naming it
      Given the shipment_status type also has the label lost, and a Shipment is stored with it
      When I read the Shipments
      Then the read fails with a materialization error saying "'lost' is not a known ShipmentStatus"

    Scenario: An invalid type name is rejected when the mapping is made
      When I map the status to the enum type "shipment status; drop table shipments"
      Then the mapping is rejected with a configuration error naming "shipment status; drop table shipments"
