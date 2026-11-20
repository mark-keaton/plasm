# Executable acceptance specification (Reqnroll): the DDL a model expects,
# printed from the model. Written before the implementation.
@implemented
Feature: Plasm prints the DDL its model expects
  As an F# application developer
  I want the tables my model needs printed from the model itself
  So that I write each migration from what the model states, not from memory

  `repo.Ddl()` prints the statements that create every table the model maps:
  columns and their types (a name PostgreSQL reserves, such as `from` or
  `order`, quoted), NOT NULL for fields that are not options (a union's
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
        | Records named by keywords |
        | Shelves lent to members   |

  Rule: It states the model's constraints by the names Plasm maps

    Scenario: Uniqueness and references carry their names
      When I print the DDL for the test domain
      Then it declares "constraint customers_email_key unique (email)"
      And it declares "constraint orders_customer_id_fkey foreign key (customer_id) references customers (id)"

  Rule: It indexes the columns the model's reads filter on

    An aggregate's owned rows are read by their owner column, a linked list
    through its join table's target column, and another aggregate by the typed
    id that refers to it: each gets an index, unless it already leads a unique
    constraint or the primary key. The schema check does not report a missing
    index: a database may have a better one.

    Scenario: Owner columns, link targets and references are indexed
      When I print the DDL for the test domain
      Then it declares "create index order_lines_order_id_idx on order_lines (order_id)"
      And it declares "create index profiles_account_id_idx on profiles (account_id)"
      And it declares "create index article_tags_tag_id_idx on article_tags (tag_id)"
      And it declares "create index payments_order_id_idx on payments (order_id)"
      And it declares "create index orders_customer_id_idx on orders (customer_id)"
      And it does not declare an index on "article_tags (article_id)"

    Scenario: A missing index is not a schema problem
      Given the payments table has no index on order_id
      When I check the model against the database
      Then no issue is reported for Payment.OrderId

    Scenario: Printing the DDL runs nothing
      When I print the DDL for the test domain
      Then no database command is executed
