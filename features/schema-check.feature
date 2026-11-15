# Executable acceptance specification (Reqnroll): checking the model against the database.
# Written before the implementation, following the conventions of core.feature.
@implemented
Feature: Checking the model against the database schema
  As an F# application developer
  I want to compare my mappings with the live database at start-up
  So that a missing column or a mismatched type is found before a request fails

  Plasm does not create or migrate schemas; keep using your migration tool. The
  check reads the database catalogue and reports every difference that would
  make a mapped read or write fail.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: The check reports what would fail, and nothing else

    Scenario: A model that matches the database passes
      When I check the model against the database
      Then no problems are reported
      And the check only reads the database catalogue

    Scenario: Missing tables and columns are reported
      Given a model with a field whose column does not exist and a record whose table does not exist
      When I check the model against the database
      Then the missing column is reported with its model and field
      And the missing table is reported with its model

    Scenario: Incompatible column types are reported
      Given a model that maps a text field to a numeric column
      When I check the model against the database
      Then the incompatible type is reported with the expected and actual types

    Scenario: A required field over a nullable column is reported
      Given a model that maps a non-optional field to a nullable column
      When I check the model against the database
      Then the field is reported as able to read NULL, with a suggestion to make it optional

    Scenario: Required columns the model cannot write are reported
      Given a model that does not map a required column without a default
      When I check the model against the database
      Then the unmapped column is reported because inserts would fail

    Scenario: Join tables are checked
      Given mappings for Articles and Tags linked through a join table
      And a link whose join table column does not exist
      When I check the model against the database
      Then the missing join table column is reported with its relationship
