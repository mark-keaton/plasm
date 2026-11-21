# Executable acceptance specification (Reqnroll): what a provider must supply.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit, which found PostgreSQL SQL in the core.
@implemented
Feature: Database-specific SQL comes from the provider
  As an author of a provider for another database
  I want database-specific constructs to come from the dialect or be guarded by a capability
  So that a second provider needs as few changes to the core library as possible

  These scenarios use a test dialect that differs from PostgreSQL. Queries are
  only explained (translated, never executed), so no second database is needed.
  Every construct a provider renders differently is behind the dialect.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And a test dialect without array parameters, upserts, RETURNING or row locking, with its own null-safe comparison and string length

  @postgres
  Rule: Constructs the dialect can express are rendered by it

    Scenario: Relationship loads without array parameters send one parameter per key
      When I explain a query for Orders, which own their Lines, with the test dialect
      Then the load command matches keys with an IN list of parameters

    Scenario: A prepared query's list argument is one parameter, as the dialect sends a list
      When I explain, with the test dialect, a prepared query of the Customers whose id is in a list argument
      Then the membership is the dialect's, of one parameter, whatever the list's length

    Scenario: A load under a key of several parts matches the keys as the dialect sends lists
      When I explain, with a dialect that sends lists as JSON, a query of the tickets, which own their notes under a key of two parts
      Then the notes' load matches both parts against the dialect's lists, one parameter a part

    Scenario: Links are written as the dialect sends lists, with or without array parameters
      Given an Article "Intro" and the Tags "fsharp" and "sql"
      When the Article's Tags are made "fsharp" and "sql" through a provider like the database's without array parameters
      Then the Article is stored linked to "fsharp" and "sql"

    Scenario: Null-safe comparisons are rendered by the dialect
      When I explain a query comparing an optional field with <> with the test dialect
      Then the comparison uses the dialect's null-safe operator

    Scenario: String functions are named by the dialect
      When I explain a query filtering on the length of a name with the test dialect
      Then the length uses the dialect's function name

    Scenario: Aggregates are named by the dialect
      When I explain, with the test dialect, whether any of each customer's Orders is published
      Then the aggregate is the dialect's name for it

    Scenario: An aggregate's empty list is the dialect's empty array
      When I explain, with the test dialect, each customer's Order totals in the order they were placed
      Then a customer with no Orders has the dialect's empty array

    Scenario: The least stored name of an ordered union is read back without an array
      When I explain, with the test dialect, the least status of the Orders
      Then the name is read back from its position by a CASE, with no array

    Scenario: A value stored as a type of the database's own is sent as the dialect casts it
      When I explain a query of the shipments whose status is Shipped, with a dialect that stores an enum as text
      Then the status is compared with its parameter as it is, cast to no type

    Scenario: A document's column is the type the dialect's JSON documents are
      When I print the DDL of the documented cards model, with a dialect whose JSON documents are json
      Then the cards' checklist is declared json

    Scenario: Rows skipped with no limit are paged as the dialect pages them
      When I explain a query of the Customers by name that skips one, with the test dialect, which needs a LIMIT before an OFFSET
      Then the query takes the dialect's unbounded limit before its offset

    Scenario: The DDL is printed in the dialect's form
      When I print the DDL with the test dialect, which declares a generated key its own way and its foreign keys in their tables
      Then an Order's key is declared the dialect's way
      And the orders table declares its reference to the customers inside it, with no alter table

    Scenario: The schema check reads the catalogue as the dialect asks it
      When the schema checks the model's tables through a provider like the database's whose dialect writes its own catalogue queries
      Then the columns were read by the dialect's query, and no difference is found

    Scenario: The DDL declares the dialect's column type, whatever names its catalogue accepts
      When I print the DDL with the test dialect, which accepts "character varying" before "text" for a string
      Then a Customer's name is declared "text"

    Scenario: A list column is tested as the dialect tests one
      When I explain a query of the Issues labelled "bug" or with no assignee, with a dialect whose tests of a list column are its own
      Then the labels are tested by the dialect's membership, and the assignees by its emptiness

    Scenario: A write's every-column check compares by the dialect's null-safe operator
      Given a dialect that is PostgreSQL's, its null-safe comparison spelled its own way
      When a Customer read through it is renamed
      Then the UPDATE compares the columns it was read with by the dialect's null-safe operator alone

  Rule: Decimals are read by value as the dialect's exact hooks read them

    A dialect that stores decimals as something its database does not compare,
    order and add by value (SQLite's text) gives exact hooks; PostgreSQL's
    numeric is exact and has none, so the SQL it is sent is as it was. On
    PostgreSQL the dialect here is PostgreSQL's with hooks of its own, each
    marked; on SQLite it is Plasm.Sqlite's own. The queries run on both.

    Background:
      Given Orders totalling 2.00, 9.50 and 10.10, through a dialect whose decimals are exact by its own hooks

    @postgres
    Scenario: A comparison of decimals is made by the dialect's exact comparison
      When I query through it the Orders whose total is more than 9.4 and not less than 2.0, whose id is not 0
      Then each comparison of a total is the dialect's exact comparison, and the id's is as it is
      And the Orders found total 9.50 and 10.10

    @sqlite
    Scenario: A comparison of decimals is made by the dialect's exact comparison on SQLite
      When I query through it the Orders whose total is more than 9.4 and not less than 2.0, whose id is not 0
      Then each comparison of a total is SQLite's plasm_decimal_compare, and the id's is as it is
      And the Orders found total 9.50 and 10.10

    @postgres
    Scenario: Equality of decimals is the dialect's exact comparison, negated as it is
      When I query through it the Orders whose total is not 2.0
      Then the comparison of the total is the dialect's exact comparison, not equal
      And the Orders found total 9.50 and 10.10

    @sqlite
    Scenario: Equality of decimals is the dialect's exact comparison, negated as it is on SQLite
      When I query through it the Orders whose total is not 2.0
      Then the comparison of the total is SQLite's plasm_decimal_compare, not equal
      And the Orders found total 9.50 and 10.10

    @postgres
    Scenario: Decimals are ordered by the dialect's exact order
      When I query through it the Orders by total, then by id
      Then the order by total is the dialect's exact order, and the order by id is as it is
      And the Orders found total 2.00, 9.50 and 10.10

    @sqlite
    Scenario: Decimals are ordered by the dialect's exact order on SQLite
      When I query through it the Orders by total, then by id
      Then the order by total is in SQLite's PLASM_DECIMAL collation, and the order by id is as it is
      And the Orders found total 2.00, 9.50 and 10.10

    @postgres
    Scenario: Decimals are grouped by the dialect's exact group key
      When I count through it the Orders of each total, with a fourth of 2.0
      Then the group key is the dialect's exact group key
      And there are three groups, the first of two Orders

    @sqlite
    Scenario: Decimals are grouped by the dialect's exact group key on SQLite
      When I count through it the Orders of each total, with a fourth of 2.0
      Then the group key is the total in SQLite's PLASM_DECIMAL collation
      And there are three groups, the first of two Orders

    @postgres
    Scenario: Sources joined on decimals are joined by the dialect's exact comparison
      When I join through it the Orders to the Payments of their total, a Payment of 2.0 for the first
      Then the join compares the total and the amount by the dialect's exact comparison
      And the Order of 2.00 is joined to the Payment of 2.0, and no other

    @sqlite
    Scenario: Sources joined on decimals are joined by the dialect's exact comparison on SQLite
      When I join through it the Orders to the Payments of their total, a Payment of 2.0 for the first
      Then the join compares the total and the amount by SQLite's plasm_decimal_compare
      And the Order of 2.00 is joined to the Payment of 2.0, and no other

    @postgres
    Scenario: A window partitioned by a decimal is partitioned by the dialect's exact group key
      When I number through it the Orders within each total, with a fourth of 2.0
      Then the partition key is the dialect's exact group key
      And the Orders are numbered 1, 1, 1 and 2, the two of 2 in one partition

    @sqlite
    Scenario: A window partitioned by a decimal is partitioned by the dialect's exact group key on SQLite
      When I number through it the Orders within each total, with a fourth of 2.0
      Then the partition key is the total in SQLite's PLASM_DECIMAL collation
      And the Orders are numbered 1, 1, 1 and 2, the two of 2 in one partition

    @postgres
    Scenario: A value holding decimals is compared part by part, each decimal by the dialect's exact comparison
      When I explain through it a query of the customers at a place, a value of two decimals
      Then each part of the value is compared by the dialect's exact comparison

    @sqlite
    Scenario: A value holding decimals is compared part by part, each decimal by the dialect's exact comparison on SQLite
      When I explain through it a query of the customers at a place, a value of two decimals
      Then each part of the value is compared by SQLite's plasm_decimal_compare

    @postgres
    Scenario: A union case holding a decimal is compared field by field, its decimal by the dialect's exact comparison
      When I explain through it a query of the listings at a fixed price, a union case holding a decimal
      Then the case's decimal is compared by the dialect's exact comparison, and its tag as it is

    @sqlite
    Scenario: A union case holding a decimal is compared field by field, its decimal by the dialect's exact comparison on SQLite
      When I explain through it a query of the listings at a fixed price, a union case holding a decimal
      Then the case's decimal is compared by SQLite's plasm_decimal_compare, and its tag as it is

    @postgres
    Scenario: A decimal among a list's values is compared with them as the dialect's exact order takes it
      When I query through it the Orders whose total is among 2.0 and 10.1
      Then the total is the operand of the list as the dialect's exact order takes it
      And the Orders found total 2.00 and 10.10

    @sqlite
    Scenario: A decimal among a list's values is compared with them as the dialect's exact order takes it on SQLite
      When I query through it the Orders whose total is among 2.0 and 10.1
      Then the total is the operand of the list in SQLite's PLASM_DECIMAL collation
      And the Orders found total 2.00 and 10.10

    @postgres
    Scenario: Distinct decimals are told apart as the dialect's exact order takes them
      When I select through it the distinct Order totals, with a fourth of 2.0
      Then the total is told apart as the dialect's exact order takes it
      And there are three totals, 2, 9.50 and 10.10

    @sqlite
    Scenario: Distinct decimals are told apart as the dialect's exact order takes them on SQLite
      When I select through it the distinct Order totals, with a fourth of 2.0
      Then the total is told apart in SQLite's PLASM_DECIMAL collation
      And there are three totals, 2, 9.50 and 10.10

    @postgres
    Scenario: A set operation over decimals tells its rows apart as the dialect's exact order takes them
      When I read through it the union of the Order totals and the Payment amounts, a Payment of 2.0 for the first
      Then each operand's total or amount is told apart as the dialect's exact order takes it
      And there are three values, 2, 9.50 and 10.10

    @sqlite
    Scenario: A set operation over decimals tells its rows apart as the dialect's exact order takes them on SQLite
      When I read through it the union of the Order totals and the Payment amounts, a Payment of 2.0 for the first
      Then each operand's total or amount is told apart in SQLite's PLASM_DECIMAL collation
      And there are three values, 2, 9.50 and 10.10

    @postgres
    Scenario: A window's sum of decimals is the dialect's exact aggregate
      When I select through it each Order's running sum of totals, by id
      Then the running sum is the dialect's exact aggregate over the window
      And the running sums are 2.00, 11.50 and 21.60

    @sqlite
    Scenario: A window's sum of decimals is the dialect's exact aggregate on SQLite
      When I select through it each Order's running sum of totals, by id
      Then the running sum is SQLite's json_group_array over the window, summed by plasm_decimal_json_sum
      And the running sums are 2.00, 11.50 and 21.60

    @postgres
    Scenario: A model keyed by a decimal is refused when the repository is made, by a dialect with exact hooks
      When a repository is made through it for a model keyed by a decimal
      Then it is refused when made, naming Rate.Id and saying a key of 2.0 and one of 2.00 would be two
      And a repository is made for it on PostgreSQL, whose numeric keys compare by value

    @sqlite
    Scenario: A model keyed by a decimal is refused when the repository is made, by a dialect with exact hooks on SQLite
      When a repository is made through it for a model keyed by a decimal
      Then it is refused when made, naming Rate.Id and saying a key of 2.0 and one of 2.00 would be two

    @postgres
    Scenario: Arithmetic on decimals is the dialect's exact arithmetic
      When I query through it the Orders whose total doubled is more than 19.5
      Then the doubling is the dialect's exact arithmetic, compared by its exact comparison
      And the Orders found total 10.10

    @sqlite
    Scenario: Arithmetic on decimals is the dialect's exact arithmetic on SQLite
      When I query through it the Orders whose total doubled is more than 19.5
      Then the doubling is SQLite's plasm_decimal_multiply, compared by its plasm_decimal_compare
      And the Orders found total 10.10

    @postgres
    Scenario: Aggregates of decimals are the dialect's exact aggregates
      When I select through it the count, sum, minimum, maximum and average of the Order totals
      Then the sum, minimum, maximum and average are the dialect's exact aggregates, and the count is as it is
      And they are 3, 21.60, 2.00, 10.10 and 7.20

    @sqlite
    Scenario: Aggregates of decimals are the dialect's exact aggregates on SQLite
      When I select through it the count, sum, minimum, maximum and average of the Order totals
      Then the sum and average are SQLite's plasm_decimal_sum and plasm_decimal_avg, the minimum and maximum in its PLASM_DECIMAL collation, and the count is as it is
      And they are 3, 21.60, 2.00, 10.10 and 7.20

    @postgres
    Scenario: A decimal is sent as the dialect's exact hooks send it
      When I query through it the Orders whose total is 9.50
      Then the total is sent as 9.50000
      And the Orders found total 9.50

    @sqlite
    Scenario: A decimal is sent as the dialect's exact hooks send it on SQLite
      When I query through it the Orders whose total is 9.50
      Then the total is sent as the text 9.50
      And the Orders found total 9.50

    @postgres
    Scenario: A write's every-column check compares a decimal by the dialect's exact null-safe comparison
      Given a Payment of 12.50 for the first Order
      When the Payment read through it is marked succeeded
      Then the UPDATE compares the amount it was read with by the dialect's exact null-safe comparison, and every other column as it is

    @sqlite
    Scenario: A write's every-column check compares a decimal by the dialect's exact null-safe comparison on SQLite
      Given a Payment of 12.50 for the first Order
      When the Payment read through it is marked succeeded
      Then the UPDATE compares the amount it was read with by SQLite's plasm_decimal_same, and every other column as it is

  @postgres
  Rule: Constructs the dialect cannot express are refused before execution

    Scenario Outline: A model storing <what> is refused when the repository is made, by a dialect without <capability>
      When a repository is made with the test dialect for a model storing <what>
      Then it is refused when made, naming <fields> and saying it has no <capability>

      Examples:
        | what                     | capability    | fields                                                                  |
        | a status as an enum type | enum types    | Shipment.Status, Shipment.PreviousStatus                                |
        | lists of scalars         | array columns | Issue.Labels, Issue.Assignees, Issue.Points, Issue.Topics, Issue.Kinds |

    Scenario Outline: <operation> needs <capability>
      When I attempt <operation> with the test dialect
      Then it fails before execution as unsupported by the provider, naming <capability>

      Examples:
        | operation                  | capability    |
        | an upsert                  | upserts       |
        | an insert                  | RETURNING     |
        | deleting an Order and its Lines | row locking   |

  @postgres
  Rule: An unsupported operation is reported the same way by every write step

    Scenario Outline: <call> reports an unsupported operation as UnsupportedByProvider
      When I attempt an operation the test dialect cannot perform through <call>
      Then the failure contains TranslationError.UnsupportedByProvider naming the missing capability

      Examples:
        | call               |
        | an insert step     |
        | a bulk write step  |
