# Executable acceptance specification (Reqnroll): a program that builds a
# query at run time (a GraphQL layer, a report builder) does it from fields
# declared once, and the query it builds is the query an application would
# write. Written before the implementation (docs/hotchocolate.md).
@implemented
Feature: A program builds a query at run time from declared fields
  As the author of a program that lets its callers choose a query's
  conditions and order (a GraphQL layer, a report builder)
  I want to build that query from fields declared once, each a quotation
  with the operators it takes
  So that what a caller can combine is known, and checked, when the program
  starts, and each query built is translated as the written one is

  An application writes its queries with plasm { }; BuildQuery and
  QueryField are for programs that build them. A declared field has a name, a
  quotation reading it from the record, the operators it takes, the type of
  the value a caller gives and, when that differs from the field's own, how
  one is made from the other. A condition built from a field, an operator and
  a value, and conditions joined by and, or and not, become a query's where; a
  field's ordering its orderBy; a count its take or skip. Each is what the
  query builder adds for the same clause written, so the SQL, the rows and the
  refusals are the written query's.

  # Customer = { Id: CustomerId; Name: string; [<Unique>] Email: string }
  # Order = { Id: OrderId; CustomerId: CustomerId option; Status: OrderStatus; Total: decimal; ... }
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: A built query is the query written

    Scenario: A condition built from a declared field sends the written query's SQL
      Given the Customer's name declared as a field compared for equality
      When I build the Customers whose name equals "Ada"
      Then the built query's SQL is the SQL of the Customers whose name is "Ada", written

    Scenario: Conditions joined by and, or and not read as the written conditions
      Given the Order's total declared as a field compared and ordered
      And the Order's status declared as a field given as text
      When I build the Orders whose total is over 10 or whose status equals "draft", and whose total is not 99
      Then the built query's SQL is the SQL of the same Orders, written

    Scenario: An ordering and a page built from a declared field read as written
      Given the Order's total declared as a field compared and ordered
      When I build the Orders by total descending, skipping 5 and taking 10
      Then the built query's SQL is the SQL of the Orders by total descending, skipping 5 and taking 10, written

    Scenario: A text field is searched as the written query searches it
      Given the Customer's name declared as a text field
      When I build the Customers whose name contains "da" or starts with "Gr"
      Then the built query's SQL is the SQL of the Customers whose name contains "da" or starts with "Gr", written

    Scenario: A built query reads the rows the written query reads
      Given the Customer's name declared as a field compared for equality
      And Customers named "Ada" and "Grace"
      When I read the Customers whose name equals "Grace", built
      Then the built read finds exactly the Customer "Grace"

    Scenario: A value the field cannot make its own is refused, naming the field
      Given the Order's status declared as a field given as text
      When I build the Orders whose status equals "shipped"
      Then building is refused with 'Order.status: no OrderStatus is "shipped"'

    Scenario: A page built twice is refused as the written query is
      Given the Order's total declared as a field compared and ordered
      When I build the Orders by total, taking 10 and then taking 5
      Then the built query is refused with "take is written twice: SQL pages once, so write one take"

  Rule: A program reads the fields its caller chose
    # A read field has a name, a quotation reading it from the record and,
    # when the caller is given something else, an F# function making it of
    # what was read, run after the read. BuildQuery.select reads the chosen
    # fields, and only those, into Selected rows; a row is read through the
    # field's declaration (GraphQL's selection, slice v).

    Scenario: Only the chosen fields' columns are read
      Given the Order's status and total, and the number of its lines, declared as fields to read
      And Orders of 10, 20 and 30, each with two lines
      When I read the Orders' status, chosen
      Then the read sends one command, reading only the status column
      And each row holds its Order's status

    Scenario: A field not chosen is refused by name when read
      Given the Order's status and total, and the number of its lines, declared as fields to read
      And Orders of 10, 20 and 30, each with two lines
      When I read the Orders' status, chosen, and then the total of the first row
      Then reading is refused with "Order.total was not chosen"

    Scenario: A list the record owns is read with one more command, for every row at once
      Given the Order's status and total, and the number of its lines, declared as fields to read
      And Orders of 10, 20 and 30, each with two lines
      When I read the Orders' total and number of lines, chosen
      Then the read sends 2 commands: the Orders' totals, and every Order's lines
      And each row holds its Order's total and 2 lines

    Scenario: A list the record links is read with one more command, for every row at once
      Given the Article's title and the names of its tags declared as fields to read
      And Articles "Plasm" tagged "fsharp" and "sql", and "Ecto" tagged "elixir"
      When I read the Articles' title and tag names, chosen
      Then the read sends 2 commands: the Articles' titles, and every Article's tags
      And the rows are "Ecto: elixir; Plasm: fsharp, sql"

    Scenario: The chosen fields are read from the query built
      Given the Order's status and total, and the number of its lines, declared as fields to read
      And Orders of 10, 20 and 30, each with two lines
      When I read the status of the Orders over 15 by total descending, taking 1, chosen
      Then the read sends one command, with the query's where, order and page
      And the one row holds the status of the Order of 30

    Scenario: A list the record owns can be selected in a written query too
      Given Orders of 10, 20 and 30, each with two lines
      When I select each Order's total and its lines, written
      Then the read sends 2 commands: the Orders' totals, and every Order's lines
      And each written row holds its Order's total and its 2 lines

  Rule: A program pages each value of a field in one command
    # BuildQuery.pageEach key skip take: the query's rows are numbered per
    # value of key, in the query's order, and each value keeps the rows from
    # skip to skip + take; one command for every value, as a let!'s take is
    # (a GraphQL layer's nested page: a board's cards).

    Scenario: Each Order's two newest Payments, in one command
      Given an Order with Payments of 10, 20 and 30, made in that order, and another with one Payment of 5
      When I read the Payments of both Orders, newest first, two of each Order
      Then the Payments are "first: 30, 20; second: 5"
      And the read sends one command, numbering the rows per Order

    Scenario: Skipping within each value
      Given an Order with Payments of 10, 20 and 30, made in that order, and another with one Payment of 5
      When I read the Payments of both Orders, newest first, skipping one and taking two of each Order
      Then the Payments are "first: 20, 10; second: "

    Scenario: The where is done before each value is paged
      Given an Order with Payments of 10, 20 and 30, made in that order, and another with one Payment of 5
      When I read the Payments over 15 of both Orders, newest first, one of each Order
      Then the Payments are "first: 30; second: "

    Scenario: A page of each value needs an order
      When I build the Payments two of each Order, without an order
      Then the built Payments query is refused with "take/skip on Payment requires an explicit ordering"

    Scenario: A query pages once
      When I build the Payments newest first, taking 10, and then two of each Order
      Then the built Payments query is refused with "pageEach is written after take: a query pages once"

    Scenario: A list chosen of each page is read for the paged rows alone
      Given Customers "Ada" and "Grace", Ada with Orders of 10, 20 and 30, and Grace with one of 40, each Order with one line
      When I read the totals and lines of each Customer's newest Order, chosen
      Then the totals are "30, 40", each with its line
      And the read sends 2 commands: the Orders, numbered per Customer, and the lines of the two Orders kept

  Rule: Declared fields are checked once, when the program starts

    Scenario: Fields Plasm can translate pass the check
      Given the Order's total declared as a field compared and ordered
      And the Order's status declared as a field given as text
      When I check the declared Order fields
      Then the check passes

    Scenario: A field Plasm cannot translate fails the check, by field and operator
      Given the Customer's name declared as a field read through its hash code, compared and ordered
      When I check the declared Customer fields
      Then the check fails start-up, naming each operator and the ordering:
        | refused                              |
        | Customer.nameHash Equal              |
        | Customer.nameHash NotEqual           |
        | Customer.nameHash GreaterThan        |
        | Customer.nameHash GreaterThanOrEqual |
        | Customer.nameHash LessThan           |
        | Customer.nameHash LessThanOrEqual    |
        | Customer.nameHash ordering           |

    Scenario: A field to read Plasm cannot translate fails the check, by name
      Given the Customer's name declared as a field to read through its hash code
      When I check the declared Customer fields to read
      Then the check fails start-up, naming "Customer.nameHash read"
