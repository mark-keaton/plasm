# Executable acceptance specification (Reqnroll): each command as an
# OpenTelemetry activity. Written before the implementation.
@implemented
Feature: Each command is an activity a tracer can listen to
  As an F# application developer whose service is traced with OpenTelemetry
  I want each command Plasm sends to be a span in my traces
  So that a slow request shows which statement it waited on

  Plasm has one ActivitySource, named "Plasm". Wherever RepoOptions.OnCommand
  is given an event, the same event is an activity when something listens to
  the source: a Client activity named by the statement's operation and table
  ("SELECT customers"), or by the transaction's own command ("BEGIN"), lasting
  the command's duration. It is tagged by OpenTelemetry's database
  conventions: db.system.name, db.operation.name, db.collection.name,
  db.query.text (the statement as sent, its values as parameters, never
  inlined), db.response.returned_rows, and error.type when the command failed;
  plasm.round_trip says which round trip it travelled in. With nothing
  listening and no OnCommand, no event is made at all. OnCommand stays the one
  hook: the activity is made from its event.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Scenario: A read is an activity named by its operation and table
    Given something listening to Plasm's activities
    When I count the Customers named "Zed" through a repository with no OnCommand
    Then one Client activity is recorded, named "SELECT customers"
    And it is tagged db.system.name "postgresql", db.operation.name "SELECT", db.collection.name "customers"
    And its db.query.text is the statement sent, with "@p0" and not "Zed" in it
    And its db.response.returned_rows is 1

  Scenario: A transaction's own commands are activities too
    Given something listening to Plasm's activities
    When a transaction inserts a Customer through a repository with no OnCommand
    Then the activities recorded are named "BEGIN", "INSERT customers" and "COMMIT", in order

  Scenario: A failed statement's activity says what failed
    Given something listening to Plasm's activities
    And a Customer with the email "ada@example.com"
    When a transaction inserts another Customer with the email "ada@example.com" through a repository with no OnCommand
    Then the INSERT's activity has error.type "23505" and the status Error

  Scenario: With nothing listening, a repository's OnCommand still has every event
    When a transaction inserts a Customer
    Then OnCommand reported a Begin, a Statement and a Commit
