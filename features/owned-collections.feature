# Executable acceptance specification (Reqnroll): owned collections, the
# records an aggregate owns. Written before the implementation.
@implemented
Feature: An owned collection is a plain list, loaded and written with its owner
  As an F# application developer
  I want my domain record to hold the records it owns as a plain list
  So that the domain record is the stored record, with no Assoc and nothing left unloaded

  A schema states what its record owns: `owns (fun i -> i.Lines) (fun l -> l.Invoice)`.
  An owned list is always loaded with its owner (one more command per level, for
  every owner read), so it is never missing. Writing the owner writes what it owns:
  an insert inserts its lines, a change from a record read writes the difference,
  and a delete deletes them. Records outside the aggregate are referred to by id.

  # Invoice = { Id: InvoiceId; Number: string; Lines: Line list }
  # Line = { Id: LineId; Invoice: InvoiceId; Item: string; Amount: decimal }
  Background:
    Given a configured relational database
    And invoices and invoice_lines tables

  Rule: An owned list is always loaded

    Scenario: Reading an invoice reads its lines
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I read the Invoices
      Then "INV-1" reads with the lines "pen" 2 and "ink" 5, in the order they were written

    Scenario: An invoice without lines reads with an empty list
      Given an Invoice "INV-2" without lines
      When I read the Invoices
      Then "INV-2" reads with no lines

    Scenario: Every invoice's lines are read in one more command
      Given Invoices "INV-1", "INV-2" and "INV-3", each with one line
      When I read the Invoices
      Then two commands are sent

    Scenario: A whole Invoice selected from a join comes with its lines
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I select whole Invoices joined with their lines
      Then each joined Invoice holds all its lines, read in one more command

  Rule: Writing the owner writes what it owns

    Scenario: Inserting an Invoice inserts its lines
      When I insert an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      Then the invoice_lines table holds "pen" 2 and "ink" 5 for that Invoice
      And the Invoice returned holds its lines with their keys

    Scenario: Changing the lines of an Invoice read writes the difference
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I read it, then change the pen to 3, remove the ink and add "pad" 4
      Then the invoice_lines table holds "pen" 3 and "pad" 4 for that Invoice
      And the pen line kept its key

    Scenario: A line that belongs to the Invoice is changed with one command
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I read it, then change only the pen to 3
      Then the pen line's change is one command, which itself says the line belongs to the Invoice
      And the invoice_lines table holds "pen" 3 and "ink" 5 for that Invoice

    Scenario: A line of another Invoice is refused, not copied
      Given Invoices "INV-1", "INV-2" and "INV-3", each with one line
      When I read them, then add the line of "INV-2" to "INV-1"
      Then the change is refused, saying the line does not belong to "INV-1"
      And the invoice_lines table holds 3 lines

    Scenario: The same line twice is refused
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I read it, then write it with its pen line listed twice
      Then the change is refused as a duplicate line
      And the invoice_lines table holds "pen" 2 and "ink" 5 for that Invoice

    Scenario: Deleting an Invoice deletes its lines
      Given an Invoice "INV-1" with the lines "pen" 2 and "ink" 5
      When I delete the Invoice I read
      Then the invoice_lines table is empty

  Rule: A list field is owned or refused

    Scenario: A list of records that are not stored is refused at build
      When I build a model of Invoices whose lines name no table
      Then the model is refused for that field, saying which names no table
