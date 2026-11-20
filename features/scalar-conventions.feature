# Executable acceptance specification (Reqnroll): domain scalars stored by
# convention. Written before the implementation, following core.feature.
@implemented
Feature: Domain scalars are stored by convention
  As an F# application developer
  I want my domain's wrapper types and fieldless unions stored without a mapping for each
  So that each fact about a type is stated once, in the type itself

  A single-case union over a built-in type is stored as the type it wraps. When
  the type has a create function returning a Result, stored values are read
  through it, so what is read obeys the domain's rules. A fieldless union is
  stored as its case names in snake case and ordered as it is declared. A
  mapping given to the model overrides the convention.

  # Ticket = { Id: TicketId; Code: Code; Title: Title; Priority: Priority; Reviewer: Code option }
  # Code is `Code of string`. Title is `private Title of string` with
  # `create: string -> Result<Title, string>` (1 to 20 characters).
  # Priority is `Low | Normal | VeryHigh`.
  Background:
    Given a configured relational database
    And a tickets table

  Rule: Wrapper types need no mapping

    Scenario: A single-case union is stored as the type it wraps
      Given Tickets in a model built with no scalar mappings
      When I insert a Ticket with code "T-1"
      Then the tickets table holds "T-1" in its code column
      And reading the Ticket back gives the code "T-1"

    Scenario: An optional wrapper is stored as NULL or its value
      Given Tickets in a model built with no scalar mappings
      When I insert a Ticket with reviewer "ada" and one without a reviewer
      Then reading them back gives the reviewer Some "ada" and None

    Scenario: A query compares a wrapper with a captured value
      Given Tickets in a model built with no scalar mappings
      And Tickets with the codes "T-1" and "T-2"
      When I keep the Tickets whose code is "T-2"
      Then only the Ticket with code "T-2" is returned

    Scenario: A type with a create function is read through it
      Given Tickets in a model built with no scalar mappings
      And a ticket row whose title is empty text
      When I read the Tickets
      Then the read fails for the Title field, with the domain's reason "must be 1 to 20 characters"

    Scenario: A private representation without a create function is refused
      When I build a model of a record with a field of a private wrapper type without create
      Then the model is refused for that field, saying the type has a private representation and no create function

    Scenario Outline: A create function whose error is not a string is refused
      When I build a model of a record with a field of a <representation> wrapper type whose create returns another error
      Then the model is refused for that field, saying the type has a create function whose error is not a string

      Examples:
        | representation |
        | private        |
        | public         |

    Scenario Outline: A create function Plasm cannot read through is refused
      When I build a model of a record with a field of a wrapper type whose create <shape>
      Then the model is refused for that field, saying the type has a create function Plasm cannot read through, because <why>

      Examples:
        | shape                    | why                          |
        | takes two arguments      | it takes 2 arguments         |
        | takes a string, not int  | its argument is a String     |
        | returns an option        | it returns Optional option, not a Result |
        | returns the type itself  | it returns Plain, not a Result           |

    Scenario: Two create functions for one type are refused
      When I build a model of a record with a field of a type with a create function in a member and in its module
      Then the model is refused for that field, saying the type has more than one create function

  Rule: A wrapper's value is read as its column, through a member Plasm can read

    # Title.Value is `let (Title v) = t in v`, marked [<ReflectedDefinition>]:
    # Plasm reads its body, the wrapper's own field, which is the column; the
    # text operators of the value are then the column's. Code.Text is the same
    # member unmarked, which Plasm cannot see into.
    Scenario: A wrapper's value is searched as its column's text
      Given Tickets in a model built with no scalar mappings
      And Tickets titled "Write the spec" and "Ship it"
      When I keep the Tickets whose title's value contains "spec"
      Then only the Ticket titled "Write the spec" is returned
      And the query's SQL reads 'WHERE "t"."title" LIKE @p0'

    Scenario: A wrapper's value is compared and started with as its column
      Given Tickets in a model built with no scalar mappings
      And Tickets titled "Write the spec" and "Ship it"
      When I keep the Tickets whose title's value starts with "Ship" or is "Write the spec"
      Then the Tickets titled "Write the spec" and "Ship it" are returned
      And the query's SQL reads 'WHERE "t"."title" LIKE @p0 OR "t"."title" = @p1'

    Scenario: A member Plasm cannot read is refused, saying how to make it readable
      Given Tickets in a model built with no scalar mappings
      When I explain the Tickets whose code's text is "T-1"
      Then the query is refused with "Code.Text is a member Plasm cannot read: mark it [<ReflectedDefinition>]"

  Rule: Fieldless unions need no mapping

    Scenario: A fieldless union is stored as its case name in snake case
      Given Tickets in a model built with no scalar mappings
      When I insert a Ticket with priority VeryHigh
      Then the tickets table holds "very_high" in its priority column

    Scenario: Ordering by a fieldless union follows its declaration
      Given Tickets in a model built with no scalar mappings
      And Tickets with the priorities VeryHigh, Low and Normal
      When I order the Tickets by priority
      Then they come back as Low, Normal and VeryHigh

  Rule: A mapping given to the model overrides the convention

    Scenario: An explicit mapping is used instead of the convention
      Given Tickets in a model built with the mapping Scalar.enum [ Low, "lo"; Normal, "mid"; VeryHigh, "hi" ]
      When I insert a Ticket with priority VeryHigh
      Then the tickets table holds "hi" in its priority column
