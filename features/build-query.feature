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
