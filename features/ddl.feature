# Executable acceptance specification (Reqnroll): the DDL a model expects,
# printed from the model. Written before the implementation.
@implemented
Feature: Plasm prints the DDL its model expects
  As an F# application developer
  I want the tables my model needs printed from the model itself
  So that I write each migration from what the model states, not from memory

  `repo.Ddl()` prints the statements that create every table the model maps:
  columns and their types, NOT NULL for fields that are not options (a union's
  case columns always allow NULL), the key (an identity for identityKey), and
  constraints with the names Plasm maps violations by: `<table>_<column>_key`
  for uniqueness and `<table>_<column>_fkey` for references, join tables
  included. It prints; it runs nothing. Plasm does not write or apply
  migrations: the DDL is what a migration should reach.

  Background:
    Given a configured relational database

  Rule: The printed DDL builds a database the schema check accepts

    Scenario Outline: The DDL for <model> builds a matching database
      Given an empty database
      When I run the DDL Plasm prints for <model>
      Then checking <model> against that database reports no problems

      Examples:
        | model                     |
        | the test domain           |
        | Tickets                   |
        | Cards with a Stage        |
        | Invoices with their lines |
        | Cards linked to tags      |

  Rule: It states the model's constraints by the names Plasm maps

    Scenario: Uniqueness and references carry their names
      When I print the DDL for the test domain
      Then it declares "constraint customers_email_key unique (email)"
      And it declares "constraint orders_customer_id_fkey foreign key (customer_id) references customers (id)"

    Scenario: Printing the DDL runs nothing
      When I print the DDL for the test domain
      Then no database command is executed
