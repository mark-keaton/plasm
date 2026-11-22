# Executable acceptance specification (Reqnroll): database constraints stated
# once, in the model. Written before the implementation, following core.feature.
@implemented
Feature: Database constraints are stated once, in the model
  As an F# application developer
  I want uniqueness and references stated once in the model
  So that a violation comes back as the field's error and the schema check finds a missing constraint

  A schema states which fields are unique. A field whose type is the key type
  of another schema (a typed id such as CustomerId) references that schema; the
  model infers it. Constraints have PostgreSQL's default names:
  <table>_<column>_key for uniqueness, <table>_<column>_fkey for a reference
  and <table>_<column>_check for a column check. A violation of a constraint so
  named comes back as the field's error, with nothing declared on the
  changeset; any other violation stays a database failure.

  SQLite names no unique constraint or foreign key in a violation: it gives a
  unique constraint's table and columns, and Plasm.Sqlite finds a foreign
  key's by running the statement again with the check deferred. So on SQLite
  a violation is the field's error by the constraint's table and columns,
  under any name, and the error names the constraint by its default name.
  The statement is not run again in a database with triggers, which would
  fire twice: there a foreign key failure stays a database failure naming no
  constraint.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: Violations come back as the field's errors

    Scenario: A duplicate of a unique field is the field's error
      Given a Customer with the email "ada@example.com"
      When I insert another Customer with the email "ada@example.com"
      Then the insert fails with a uniqueness error on Email, naming customers_email_key

    Scenario: A violation by an owned record is that record's field error
      When I insert an Order with a line for the Product with id 999
      Then the insert fails with a reference error on Lines[0].ProductId, naming order_lines_product_id_fkey
      And no Order was written

    Scenario: A reference to a missing row is the field's error
      When I insert an Order for the Customer with id 999
      Then the insert fails with a reference error on CustomerId, naming orders_customer_id_fkey

    Scenario: A violation the database already held does not take a new violation's field
      Given an order line of the Order with id 999, written while the database did not check its foreign keys
      When I insert an Order with a line for the Product with id 999
      Then the insert fails with a reference error on Lines[0].ProductId, naming order_lines_product_id_fkey

    @sqlite
    Scenario: On SQLite a foreign key failure in a database with triggers stays unnamed
      Given a trigger on customers
      When I insert an Order for the Customer with id 999
      Then the insert fails as a foreign key failure naming no constraint

    Scenario: A broken column check is the field's error
      When I insert an Order with the total -1
      Then the insert fails with a check error on Total, naming orders_total_check

    Scenario: A constraint whose name follows no convention stays a database failure
      Given a table constraint named "orders_sane" that refuses a total of 13
      When I insert an Order with the total 13
      Then the insert fails as a database failure naming "orders_sane"

  Rule: The model knows the constraints

    Scenario: A typed id references the schema whose key it is
      When I list the references the model infers
      Then Order.CustomerId references Customer
      And OrderLine.ProductId references Product
      And no record's own key references itself

    Scenario: A key type shared by two schemas is refused
      When I build a model in which Customers and Suppliers both have a CustomerId key
      Then the model is refused, naming CustomerId and both schemas

  Rule: The schema check finds a missing or misnamed constraint

    A unique field and a reference are facts the model states, and a violation
    is mapped to its field by the constraint's name. A constraint that holds the
    fact under another name would turn the field's error into a database
    failure, so the schema check reports it, with the rename. A check
    constraint is not stated in the model: its default name is how a field
    claims it.

    Scenario: A unique field without a unique constraint is reported
      Given the customers table has lost its unique constraint on email
      When I check the model against the database
      Then the missing uniqueness of Customer.Email is reported

    Scenario: A reference without a foreign key is reported
      Given the orders table has lost its foreign key on customer_id
      When I check the model against the database
      Then the missing reference from Order.CustomerId to customers is reported

    @postgres
    Scenario: A unique constraint under another name is reported, with the rename
      Given the unique constraint on customers.email is renamed to customers_email_uniq
      When I check the model against the database
      Then Customer.Email is reported as misnamed, saying to rename customers_email_uniq to customers_email_key

    @postgres
    Scenario: A unique index under another name is reported, with the rename
      Given the unique constraint on customers.email is replaced by the unique index customers_email_idx
      When I check the model against the database
      Then Customer.Email is reported as misnamed, saying to rename customers_email_idx to customers_email_key

    @postgres
    Scenario: A foreign key under another name is reported, with the rename
      Given the foreign key on orders.customer_id is renamed to orders_customer_ref
      When I check the model against the database
      Then Order.CustomerId is reported as misnamed, saying to rename orders_customer_ref to orders_customer_id_fkey

    @postgres
    Scenario: A misnamed constraint is reported once its field is the only holder
      Given the unique constraint on customers.email is renamed to customers_email_uniq
      And the unique constraint customers_email_key is added on customers.email
      When I check the model against the database
      Then no issue is reported for Customer.Email

    @sqlite
    Scenario: On SQLite a unique index or foreign key under another name holds its field
      Given the unique constraint on customers.email is replaced by the unique index customers_email_idx
      And the foreign key on orders.customer_id is renamed to orders_customer_ref
      When I check the model against the database
      Then no issue is reported for Customer.Email
      And no issue is reported for Order.CustomerId

    @sqlite
    Scenario: On SQLite a duplicate under a unique index of another name is the field's error
      Given the unique constraint on customers.email is replaced by the unique index customers_email_idx
      And a Customer with the email "ada@example.com"
      When I insert another Customer with the email "ada@example.com"
      Then the insert fails with a uniqueness error on Email, naming customers_email_key

    @sqlite
    Scenario: On SQLite a reference to a missing row under a foreign key of another name is the field's error
      Given the foreign key on orders.customer_id is renamed to orders_customer_ref
      When I insert an Order for the Customer with id 999
      Then the insert fails with a reference error on CustomerId, naming orders_customer_id_fkey
